using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace ULKHelper.Automation;

internal sealed class ArrivalGameAdapter(IObjectTable objects, IPartyList party, IFateTable fates, MountGameAdapter? mounts = null) : IArrivalActions
{
    private readonly Dictionary<ulong, (uint Sequence, DateTimeOffset LastAttack)> attacks = new();
    public void Reset() => attacks.Clear();

    public unsafe ulong ReadCleanupTarget(Vector3 position, ulong currentTarget)
    {
        var enmity = TriggerGameReader.ReadEnmity();
        if (enmity is not { Count: > 0 }) return 0;
        return objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.IsTargetable && !n.IsDead
                && n.CurrentHp > 0 && enmity.Contains(n.EntityId) && ((Character*)n.Address)->FateId == 0)
            .OrderByDescending(n => n.GameObjectId == currentTarget).ThenBy(n => Vector3.DistanceSquared(position, n.Position))
            .FirstOrDefault()?.GameObjectId ?? 0;
    }

    public unsafe ArrivalFate? Read(MapDestination destination, DateTimeOffset now, int? islandPlayers = null, ushort? boundId = null)
    {
        var fate = fates.Where(f => f != null && f.State == FateState.Running && f.TerritoryType.RowId == destination.Territory
            && NavigationRules.IsEurekaNm(f.GameData.Value.EurekaFate, f.GameData.Value.Rule)
            && (boundId == null || f.FateId == boundId) && NavigationRules.Distance(f.Position, destination.Position) <= 50)
            .OrderBy(f => NavigationRules.Distance(f.Position, destination.Position)).FirstOrDefault();
        if (fate == null) return null;
        var enemies = objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.MaxHp > 0
            && ((Character*)n.Address)->FateId == fate.FateId).ToArray();
        var enemyIds = enemies.Select(n => n.GameObjectId).ToHashSet();
        // The localized FATE title normally ends with the NM name after an em dash.
        // Never substitute an add's HP when the boss is not loaded or cannot be matched.
        var name = fate.Name.TextValue.Split('—', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        var named = objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.MaxHp > 0 && n.Name.TextValue == name
            && NavigationRules.Distance(n.Position, fate.Position) <= Math.Max(50, fate.Radius)
            && (((Character*)n.Address)->FateId == fate.FateId || ((Character*)n.Address)->FateId == 0)).ToArray();
        // Ignore a stale untargetable model when exactly one living targetable boss is present.
        var bossIndex = ArrivalRules.BossIndex(named.Select(n => (!n.IsDead, n.IsTargetable)).ToArray());
        var boss = bossIndex is { } index ? named[index] : null;
        if (boss != null) enemyIds.Add(boss.GameObjectId);
        float? hp = boss != null ? (float)boss.CurrentHp / boss.MaxHp : null;
        var teammates = party.Select(p => (ulong)p.EntityId).ToHashSet();
        var localId = objects.LocalPlayer?.GameObjectId ?? 0;
        var participants = new List<ArrivalParticipant>();
        var loaded = new HashSet<ulong>();
        foreach (var player in objects.OfType<IPlayerCharacter>())
        {
            if (player.GameObjectId == localId || player.Address == 0) continue;
            loaded.Add(player.GameObjectId);
            var character = (Character*)player.Address;
            var cast = character->GetCastInfo();
            attacks.TryGetValue(player.GameObjectId, out var previous);
            var last = previous.LastAttack;
            if (cast != null)
            {
                // First observation establishes a baseline; don't count an old cached action.
                if (attacks.ContainsKey(player.GameObjectId) && cast->ResponseGlobalSequence != previous.Sequence)
                {
                    var targets = cast->ResponseTargetIds;
                    for (var i = 0; i < Math.Min((int)cast->ResponseTargetCount, targets.Length); i++)
                        if (enemyIds.Contains((ulong)targets[i])) { last = now; break; }
                }
                attacks[player.GameObjectId] = (cast->ResponseGlobalSequence, last);
            }
            var attacking = !player.IsDead && character->InCombat && (last != default && now - last <= TimeSpan.FromSeconds(4)
                || player.IsCasting && enemyIds.Contains(player.CastTargetObjectId));
            participants.Add(new(player.GameObjectId, player.Position, !player.IsDead,
                teammates.Contains(player.EntityId), attacking));
        }
        foreach (var id in attacks.Keys.Where(id => !loaded.Contains(id)).ToArray()) attacks.Remove(id);
        var hud = AgentHUD.Instance();
        var manager = FateManager.Instance();
        int? playerLevel = hud != null && (hud->ExpFlags & AgentHudExpFlag.InEureka) != 0 ? hud->ExpContentLevel : null;
        var sync = ArrivalRules.NeedsSync(playerLevel, fate.MaxLevel, manager != null && manager->SyncedFateId == fate.FateId);
        return new(fate.FateId, fate.StartTimeEpoch, fate.Position, fate.Radius, sync, hp, participants, islandPlayers, true,
            boss?.GameObjectId ?? 0, boss?.IsDead != true, boss?.IsTargetable == true,
            manager != null && manager->SyncedFateId == fate.FateId, boss?.Position,
            FateLevel: fate.GameData.Value.ClassJobLevel, PlayerLevel: playerLevel);
    }

    public EncounterObservation ReadEncounter(MapDestination destination, DateTimeOffset now, int? islandPlayers, ushort? boundId)
    {
        var ended = boundId != null && fates.Any(f => f != null && f.FateId == boundId
            && f.State is FateState.Ending or FateState.Ended or FateState.Failed);
        return new(Read(destination, now, islandPlayers, boundId), ended);
    }

    // IDs verified against this installation's GeneralAction sheet: 9 random mount, 23 dismount.
    // Both operations are state guarded to avoid toggling a mount that is already in place.
    public unsafe bool Mount() => mounts != null ? mounts.Mount() : objects.LocalPlayer is { Address: not 0, IsDead: false } player
        && ((Character*)player.Address)->Mount.MountId == 0 && !player.IsCasting && !((Character*)player.Address)->InCombat && Use(9);
    public unsafe bool Dismount() => mounts != null ? mounts.Dismount() : objects.LocalPlayer is { Address: not 0 } player
        && ((Character*)player.Address)->Mount.MountId != 0 && Use(23);
    private static unsafe bool Use(uint action)
    {
        var manager = ActionManager.Instance();
        return manager != null && manager->GetActionStatus(ActionType.GeneralAction, action) == 0
            && manager->UseAction(ActionType.GeneralAction, action);
    }
}
