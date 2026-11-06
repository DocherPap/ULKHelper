using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal enum CommonAction
{
    Entrance, Camp, Potion, Anemos, Pagos, Pyros, Hydatos, Emergency,
    Coordinate, Fate, WaitForPull, NearestCrystal, Sync, Elemental, Rescue, BunnyTreasure, EatFate,
}

internal sealed partial class AutomationHelpers
{
    private readonly CommonJourney commonJourney;
    private readonly IPartyList commonParty;
    private volatile bool autoInvite, autoAccept;
    private volatile bool walkNearPagosDragon;
    private bool wasNearPagosDragon;
    private DateTimeOffset nextDragonCheck;
    // Local CN BNpcName sheet: 虚无冰雪龙.
    private const uint PagosDragonNameId = 7473;
    private string commonStatus = "请选择常用功能。";
    private bool commonUsesNavigation, commonUsesFollow;
    private DateTimeOffset nextInvite, nextAccept, nextShout;
    private readonly Dictionary<PlayerIdentity, DateTimeOffset> invited = new();
    private (uint Time, string Name) lastAccepted;

    public bool AutoInvite => autoInvite;
    public bool AutoAccept => autoAccept;
    public bool CheckPagosRing => commonJourney.CheckPagosRing;
    public bool WalkNearPagosDragon => walkNearPagosDragon;
    public string CommonStatus => bunnySearchOnly ? BunnyAutomationStatus : SurveyActive ? surveyStatus : commonJourney.Active ? commonJourney.Status
        : commonUsesFollow ? follow.Status : commonUsesNavigation ? navigation.Status : commonStatus;

    public void SetCommonParty(bool invite, bool enabled) => commands.Enqueue(() =>
    {
        if (invite) autoInvite = enabled;
        else autoAccept = enabled;
        settings?.Put(invite ? "common.autoInvite" : "common.autoAccept", enabled);
    });

    public void SetCheckPagosRing(bool enabled) => commands.Enqueue(() =>
    {
        commonJourney.CheckPagosRing = enabled;
        settings?.Put("common.checkPagosRing", enabled);
    });

    public void SetWalkNearPagosDragon(bool enabled) => commands.Enqueue(() =>
    {
        walkNearPagosDragon = enabled;
        wasNearPagosDragon = false;
        nextDragonCheck = default;
        settings?.Put("common.walkNearPagosDragon", enabled);
    });

    private unsafe void TickPagosDragon(WorldContext world, bool loading, DateTimeOffset now)
    {
        if (!walkNearPagosDragon || world.Territory != CommonJourney.Pagos)
        {
            wasNearPagosDragon = false;
            return;
        }
        if (loading || !SafeState() || now < nextDragonCheck || objects.LocalPlayer is not { } player) return;
        nextDragonCheck = now.AddMilliseconds(100);
        var nearby = objects.OfType<IBattleNpc>().Any(npc => npc.NameId == PagosDragonNameId && !npc.IsDead
            && Vector3.DistanceSquared(player.Position, npc.Position) <= 15 * 15);
        if (nearby && !wasNearPagosDragon)
        {
            var control = Control.Instance();
            if (control == null) return;
            if (!control->IsWalking) control->IsWalking = true;
        }
        wasNearPagosDragon = nearby;
    }

    private void StopTasks(string remoteReason = "已从游戏内停止 Discord 任务。")
    {
        StopRemote(remoteReason);
        StopBunnyAutomation("已停止自动挖宝。");
        if (SurveyActive) StopSurvey("已停止元灵巡查。");
        commonJourney.Stop("已停止常用功能。");
        trigger.Stop();
        follow.Stop();
        navigation.Stop();
        arrivalGame.Reset();
    }

    public void EatFate(MapDestination destination) => RunCommon(CommonAction.EatFate, destination);

    public void RunCommon(CommonAction action, MapDestination? target = null) => commands.Enqueue(() =>
    {
        commonUsesNavigation = commonUsesFollow = false;
        try
        {
            ExecuteCommon(action, target, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            commonJourney.Stop("常用功能失败。");
            commonStatus = "常用功能失败：" + ex.GetBaseException().Message;
        }
    });

    private void ExecuteCommon(CommonAction action, MapDestination? target, DateTimeOffset now)
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
                    : target != null && target.Territory == world.Territory && target.Map == world.Map
                    && SendElementalFlag(target);
                if (sent) nextShout = now.AddSeconds(3);
                commonStatus = sent ? "已提交喊话。" : "当前地图没有已知元灵或喊话不可用。";
            }
            return;
        }
        if (!SafeState() || objects.LocalPlayer is not { } player)
        { commonStatus = "请结束交互后重试。"; return; }
        if (RemoteActive || BunnyAutomationActive || SurveyActive || commonJourney.Active || trigger.Active || follow.Active || navigation.Active || combat.CleanupPending)
        { commonStatus = "请先停止当前任务。"; return; }

        if (action is CommonAction.Entrance or CommonAction.Potion or CommonAction.Anemos or CommonAction.Pagos or CommonAction.Pyros or CommonAction.Hydatos)
        {
            if (conditions[ConditionFlag.InCombat]) { commonStatus = "请脱战后再开始 NPC 行程。"; return; }
            commonJourney.Start(action, world, now);
            commonStatus = commonJourney.Status;
            return;
        }
        if (!IsEureka(world.Territory)) { commonStatus = "请先进入优雷卡岛内。"; return; }
        switch (action)
        {
            case CommonAction.BunnyTreasure:
                StartBunnyTreasure(world, now);
                break;
            case CommonAction.Camp:
                if (IslandCrystalRoutes.CampDestination(world, player.Position) is not { } camp)
                { commonStatus = "没有已知水晶。"; return; }
                commonUsesNavigation = true;
                StartPointNavigation(camp, false, waitWithoutFate: false);
                break;
            case CommonAction.NearestCrystal:
                if (!EnsureTravelMount()) { commonStatus = "请先选择已解锁且可移动的坐骑。"; return; }
                usingFollow = commonUsesFollow = true;
                follow.StartCrystalStandby(world, player.Position, now);
                break;
            case CommonAction.Coordinate:
            case CommonAction.Fate:
                var goal = CurrentFlag(world);
                if (goal == null) { commonStatus = "请先在本岛地图上放置旗标。"; return; }
                commonUsesNavigation = true;
                StartPointNavigation(goal, action == CommonAction.Fate, requireFate: action == CommonAction.Fate);
                break;
            case CommonAction.EatFate:
                var eatGoal = target ?? CurrentFlag(world);
                var activeFates = ReadFates(includePreparing: target != null);
                var eatFate = eatGoal == null || eatGoal.Territory != world.Territory || eatGoal.Map != world.Map ? null
                    : eatGoal.FateId is { } id ? activeFates.FirstOrDefault(f => f.Id == id)
                    : NavigationRules.FindFate(eatGoal.Position, activeFates);
                if (eatFate == null)
                {
                    commonStatus = target == null ? "请在本岛正在进行的 FATE 上设置旗标。" : "选中的 FATE 当前不可参加。";
                    return;
                }
                StartEatingFate(world, eatFate, now);
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

    private bool StartEatingFate(WorldContext world, FateEntry fate, DateTimeOffset now)
    {
        usingFollow = commonUsesFollow = true;
        commonUsesNavigation = false;
        arrivalGame.Reset();
        return follow.StartEating(world, fate, now);
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
        if ((!autoInvite && !RemotePartyActive) || world != Context() || !IsEureka(world.Territory) || !SafeState()
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
        if ((!autoAccept && !RemotePartyActive) || !IsEureka(client.TerritoryType) || !SafeState() || now < nextAccept) return;
        nextAccept = now.AddSeconds(1);
        var proxy = InfoProxyPartyInvite.Instance();
        if (proxy == null || proxy->InviteTime == 0) return;
        var invite = (proxy->InviteTime, proxy->InviterName.ToString());
        if (invite == lastAccepted || string.IsNullOrWhiteSpace(invite.Item2)) return;
        lastAccepted = invite;
        SendGameCommand("/join");
    }
}
