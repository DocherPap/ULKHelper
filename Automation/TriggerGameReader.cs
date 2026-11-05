using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

// Only observed, matching names and levels. No imported/guessed spawn positions.
internal sealed class TriggerGameReader(IObjectTable objects, IDataManager data)
{
    public IReadOnlyList<TriggerMobRule> Rules { get; } = TriggerMobCatalog.Resolve(data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>()!
        .Select(n => (n.RowId, n.Singular.ExtractText())));
    private readonly Dictionary<(ushort, int, int), MobArea> areas = new();
    public IReadOnlyList<MobArea> Areas => areas.Values.ToArray();
    public void Reset() => areas.Clear();
    public static unsafe int? Level()
    {
        var hud = AgentHUD.Instance();
        return hud != null && (hud->ExpFlags & AgentHudExpFlag.InEureka) != 0 && hud->ExpContentLevel > 0 ? hud->ExpContentLevel : null;
    }
    public static unsafe HashSet<uint>? ReadEnmity()
    {
        var ui = UIState.Instance();
        if (ui == null) return null;
        var enemies = new HashSet<uint>();
        var haters = ui->Hater.Haters;
        foreach (var hater in haters[..Math.Clamp(ui->Hater.HaterCount, 0, haters.Length)])
            if (hater.Enmity > 0) enemies.Add(hater.EntityId);
        return enemies;
    }

    public unsafe IReadOnlyList<TriggerMob> Read(uint territory)
    {
        var result = new List<TriggerMob>();
        var localId = objects.LocalPlayer?.GameObjectId;
        var enemies = ReadEnmity();
        foreach (var npc in objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.MaxHp > 0))
        {
        var elementalLevel=EurekaNpcLevel.Read(npc);
        if(elementalLevel==null)continue;
        foreach (var rule in Rules.Where(r => r.Territory == territory && r.Level == elementalLevel && r.NameIds.Contains(npc.NameId)))
        {
            var dead = npc.IsDead || npc.CurrentHp == 0;
            var aggro = enemies?.Contains(npc.EntityId) == true || localId != null && npc.TargetObjectId == localId;
            var engaged = ((Character*)npc.Address)->InCombat;
            result.Add(new(npc.GameObjectId, rule.FateId, npc.Position, dead, aggro, npc.IsTargetable, engaged, npc.CurrentHp, npc.HitboxRadius));
            // Don't turn mobs pulled after the player into false spawn distributions.
            if (!dead && !engaged && areas.Count < 4096)
                areas[(rule.FateId, (int)MathF.Floor(npc.Position.X / 20), (int)MathF.Floor(npc.Position.Z / 20))] = new(rule.FateId, npc.Position);
        }
        }
        return result;
    }
}
