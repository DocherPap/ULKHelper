using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal enum CommonAction
{
    Entrance, Camp, Potion, Anemos, Pagos, Pyros, Hydatos, Emergency,
    Coordinate, Fate, WaitForPull, NearestCrystal, Sync, Elemental, Rescue,
}

internal sealed partial class AutomationHelpers
{
    private readonly CommonJourney commonJourney;
    private readonly IPartyList commonParty;
    private volatile bool autoInvite, autoAccept;
    private string commonStatus = "请选择常用功能。";
    private bool commonUsesNavigation, commonUsesFollow;
    private DateTimeOffset nextInvite, nextAccept, nextShout;
    private readonly Dictionary<PlayerIdentity, DateTimeOffset> invited = new();
    private (uint Time, string Name) lastAccepted;

    public bool AutoInvite => autoInvite;
    public bool AutoAccept => autoAccept;
    public string CommonStatus => commonJourney.Active ? commonJourney.Status
        : commonUsesFollow ? follow.Status : commonUsesNavigation ? navigation.Status : commonStatus;

    public void SetCommonParty(bool invite, bool enabled) => commands.Enqueue(() =>
    {
        if (invite) autoInvite = enabled;
        else autoAccept = enabled;
        settings?.Put(invite ? "common.autoInvite" : "common.autoAccept", enabled);
    });

    private void StopTasks()
    {
        commonJourney.Stop("已停止常用功能。");
        trigger.Stop();
        follow.Stop();
        navigation.Stop();
        arrivalGame.Reset();
        if (debug.Active) debug.Stop("已停止测试。", DateTimeOffset.UtcNow);
    }

    public void RunCommon(CommonAction action, MapDestination? elemental = null) => commands.Enqueue(() =>
    {
        commonUsesNavigation = commonUsesFollow = false;
        try
        {
            ExecuteCommon(action, elemental, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            commonJourney.Stop("常用功能失败。");
            commonStatus = "常用功能失败：" + ex.GetBaseException().Message;
        }
    });

    private void ExecuteCommon(CommonAction action, MapDestination? elemental, DateTimeOffset now)
    {
        if (action == CommonAction.Emergency)
        {
            combat.SuppressEmergencyResume();
            StopTasks();
            autoInvite = autoAccept = false;
            settings?.Put("common.autoInvite", false);
            settings?.Put("common.autoAccept", false);
            listener.SetListening(false);
            commands.Clear();
            commonStatus = combat.EmergencyStop();
            try { mesh.Stop(); }
            catch { commonStatus += " 未能确认vnavmesh已停止。"; }
            return;
        }
        var world = Context();
        if (action is CommonAction.Sync or CommonAction.Elemental or CommonAction.Rescue)
        {
            var available = action == CommonAction.Rescue
                ? client.IsLoggedIn && !client.IsPvP && objects.LocalPlayer != null && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51]
                : SafeState();
            if (!available || !IsEureka(world.Territory)) { commonStatus = "请在优雷卡岛内使用此功能。"; return; }
            if (action == CommonAction.Sync)
                commonStatus = SendGameCommand("/等级同步") ? "已提交等级同步命令。" : "无法提交等级同步命令。";
            else if (now >= nextShout)
            {
                var sent = action == CommonAction.Rescue ? SendGameCommand("/shout <pos>999")
                    : elemental != null && elemental.Territory == world.Territory && elemental.Map == world.Map
                    && SendElementalFlag(elemental);
                if (sent) nextShout = now.AddSeconds(3);
                commonStatus = sent ? "已提交喊话。" : "当前地图没有已知元灵或喊话不可用。";
            }
            return;
        }
        if (!CanMove() || objects.LocalPlayer is not { } player)
        { commonStatus = "请脱战并结束交互后重试。"; return; }
        if (commonJourney.Active || trigger.Active || follow.Active || navigation.Active || debug.Active || combat.CleanupPending)
        { commonStatus = "请先停止当前任务。"; return; }

        if (action is CommonAction.Entrance or CommonAction.Potion or CommonAction.Anemos or CommonAction.Pagos or CommonAction.Pyros or CommonAction.Hydatos)
        {
            commonJourney.Start(action, world, now);
            commonStatus = commonJourney.Status;
            return;
        }
        if (!IsEureka(world.Territory)) { commonStatus = "请先进入优雷卡岛内。"; return; }
        switch (action)
        {
            case CommonAction.Camp:
            case CommonAction.NearestCrystal:
                var crystal = action == CommonAction.Camp
                    ? IslandCrystalRoutes.Entrance(world.Territory)
                    : IslandCrystalRoutes.Nearest(world.Territory, player.Position);
                if (crystal == null) { commonStatus = "没有已知水晶。"; return; }
                commonUsesNavigation = true;
                StartPointNavigation(new(world.Territory, world.Map, ArrivalRules.Position(player.Position, crystal.Position, 7),
                    action == CommonAction.Camp ? "入口营地" : "最近的水晶"), false, useCrystals: false);
                break;
            case CommonAction.Coordinate:
            case CommonAction.Fate:
                var goal = CurrentFlag(world);
                if (goal == null) { commonStatus = "请先在本岛地图上放置旗标。"; return; }
                if (action == CommonAction.Fate && NavigationRules.FindFate(goal.Position, ReadFates()) == null)
                { commonStatus = "旗标50米内没有进行中的FATE。"; return; }
                commonUsesNavigation = true;
                StartPointNavigation(goal, action == CommonAction.Fate, useCrystals: false);
                break;
            case CommonAction.WaitForPull:
                var fate = ReadFates().Where(f => NavigationRules.Distance(player.Position, f.Position) <= 100)
                    .MinBy(f => NavigationRules.Distance(player.Position, f.Position));
                if (fate == null) { commonStatus = "附近100米没有进行中的NM。"; return; }
                var destination = new MapDestination(world.Territory, world.Map, fate.Position, fate.Name);
                arrivalGame.Reset();
                var observation = arrivalGame.ReadEncounter(destination, now, roster.OtherPlayers(new(player.Name.TextValue, player.HomeWorld.RowId), now), fate.Id);
                var boss = objects.FirstOrDefault(o => o.GameObjectId == observation.Fate?.BossId);
                if (observation.Fate is not { BossAlive: true, BossTargetable: true } encounter || boss == null)
                { commonStatus = "附近NM的头目尚未加载或不可选中。"; return; }
                usingFollow = commonUsesFollow = true;
                follow.StartAtBoss(world, destination, encounter, boss.Position, player.Position, now);
                break;
        }
    }

    private static bool IsEureka(uint territory) => IslandCrystalRoutes.Entrance(territory) != null;
    private static unsafe MapDestination? CurrentFlag(WorldContext world)
    {
        var map = AgentMap.Instance();
        if (map == null || map->FlagMarkerCount == 0) return null;
        var flag = map->FlagMapMarkers[0];
        var position = new Vector3(flag.XFloat, 0, flag.YFloat);
        return flag.TerritoryId == world.Territory && flag.MapId == world.Map && NavigationRules.Finite(position)
            ? new(world.Territory, world.Map, position, "当前地图旗标") : null;
    }

    private static unsafe bool SendElementalFlag(MapDestination destination)
    {
        var map = AgentMap.Instance();
        if (map == null || !NavigationRules.Finite(destination.Position)) return false;
        map->SetFlagMapMarker(destination.Territory, destination.Map, destination.Position);
        return map->FlagMarkerCount > 0 && SendGameCommand("/shout <flag>yl");
    }

    internal static unsafe bool SendGameCommand(string command)
    {
        var module = UIModule.Instance();
        if (module == null) return false;
        var text = Utf8String.FromString(command);
        if (text == null) return false;
        try { module->ProcessChatBoxEntry(text, 0, false); return true; }
        finally { text->Dtor(true); }
    }

    private static bool IsPartyRequest(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && (trimmed.All(c => c == '1') || trimmed.Contains("111", StringComparison.Ordinal));
    }

    private unsafe void InviteFromShout(PlayerIdentity identity, WorldContext world)
    {
        var now = DateTimeOffset.UtcNow;
        if (!autoInvite || world != Context() || !IsEureka(world.Territory) || !SafeState()
            || now < nextInvite || commonParty.Count() >= 8 || invited.GetValueOrDefault(identity) > now
            || objects.LocalPlayer is not { } self
            || identity == new PlayerIdentity(self.Name.TextValue, self.HomeWorld.RowId)) return;
        var loaded = objects.OfType<IPlayerCharacter>().FirstOrDefault(p => p.Name.TextValue == identity.Name && p.HomeWorld.RowId == identity.HomeWorld);
        var entity = loaded?.EntityId ?? (roster.Snapshot.UpdatedAt is { } updated && now - updated <= TimeSpan.FromSeconds(45)
            ? roster.Snapshot.Members.FirstOrDefault(p => p.Identity == identity)?.EntityId : null);
        if (entity is null or 0 or 0xE0000000 || commonParty.Any(p => p.EntityId == entity.Value)) return;
        var proxy = InfoProxyPartyInvite.Instance();
        if (proxy == null) return;
        foreach (var expired in invited.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) invited.Remove(expired);
        nextInvite = now.AddSeconds(3);
        invited[identity] = now.AddSeconds(30);
        proxy->InviteToPartyInInstanceByEntityId(entity.Value);
    }

    private unsafe void TickCommonParty(DateTimeOffset now)
    {
        if (!autoAccept || !IsEureka(client.TerritoryType) || !SafeState() || now < nextAccept) return;
        nextAccept = now.AddSeconds(1);
        var proxy = InfoProxyPartyInvite.Instance();
        if (proxy == null || proxy->InviteTime == 0) return;
        var invite = (proxy->InviteTime, proxy->InviterName.ToString());
        if (invite == lastAccepted || string.IsNullOrWhiteSpace(invite.Item2)) return;
        lastAccepted = invite;
        SendGameCommand("/join");
    }
}
