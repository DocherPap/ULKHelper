using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
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

    public unsafe IReadOnlyList<TriggerMob> Read(uint territory, bool includeOtherEngaged = false)
    {
        var result = new List<TriggerMob>();
        var localId = objects.LocalPlayer?.GameObjectId;
        var enemies = ReadEnmity();
        foreach (var npc in objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.MaxHp > 0))
        {
            var elementalLevel = EurekaNpcLevel.Read(npc);
            var matched = false;
            if (elementalLevel != null)
            {
                foreach (var rule in Rules.Where(r => r.Territory == territory && r.Level == elementalLevel && r.NameIds.Contains(npc.NameId)))
                {
                    matched = true;
                    var mob = ReadMob(rule.FateId);
                    result.Add(mob);
                    // Don't turn mobs pulled after the player into false spawn distributions.
                    if (!mob.Dead && !mob.Engaged && areas.Count < 4096)
                        areas[(rule.FateId, (int)MathF.Floor(mob.Position.X / 20), (int)MathF.Floor(mob.Position.Z / 20))] = new(rule.FateId, mob.Position);
                }
            }

            if (!matched && includeOtherEngaged && npc.BattleNpcKind == BattleNpcSubKind.Combatant
                && ((Character*)npc.Address)->InCombat)
            {
                var mob = ReadMob(0);
                if (!mob.Dead) result.Add(mob);
            }

            TriggerMob ReadMob(ushort fateId) => new(npc.GameObjectId, fateId, npc.Position,
                npc.IsDead || npc.CurrentHp == 0,
                enemies?.Contains(npc.EntityId) == true || localId != null && npc.TargetObjectId == localId,
                npc.IsTargetable, ((Character*)npc.Address)->InCombat, npc.CurrentHp, npc.HitboxRadius, npc.MaxHp);
        }
        return result;
    }
}
