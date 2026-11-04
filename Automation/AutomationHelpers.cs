using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ULKHelper.Weather;

namespace ULKHelper.Automation;

// Game operations run on framework thread. Leader coordinates move only in an explicitly started session.
internal sealed partial class AutomationHelpers : IAutomationHelpers, IDisposable
{
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly IFateTable fates;
    private readonly ICondition conditions;
    private readonly IChatGui chat;
    private readonly IGameGui gui;
    private readonly IPluginLog log;
    private readonly ITargetManager targets;
    private readonly SettingsStore? settings;
    private bool pointNm;
    private string? reportedCrystalDecision;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly LeaderListenerHelper listener = new();
    private readonly IslandTravelHelper navigation;
    private readonly IslandRosterGameSource rosterSource;
    private readonly IslandRosterHelper roster;
    private readonly IPlayerState playerState;
    private readonly ArrivalGameAdapter arrivalGame;
    private readonly MountGameAdapter mounts;
    private readonly CombatAutomationAdapter combat;
    private readonly FellingFollowHelper follow;
    private readonly TriggerKillGameTracker kills;
    private readonly AutoTriggerHelper trigger;
    private readonly TriggerGameReader triggerReader;
    private readonly Dictionary<ushort, TriggerRouteSet> triggerRoutes = new();
    private volatile AutoTriggerSnapshot triggerSnapshot = AutoTriggerSnapshot.Empty;
    private DateTimeOffset nextTrigger;
    private readonly INavigationMesh mesh;
    private readonly IDataManager data;
    private DateTimeOffset nextFollow, nextMounts, nextCleanup, nextKills;
    private bool usingFollow;
    private IReadOnlyList<PlayerEntry> players = [];
    private WorldContext previous;
    private DateTimeOffset nextPlayers;
    private volatile AutomationSnapshot snapshot = new([], null, false, "请选择车头。", null, [], false, "尚未开始导航。");
    private bool disposed;
    private static readonly Regex Coordinates = new(@"[（(]\s*(?<x>\d{1,2}(?:\.\d+)?)\s*[,，]\s*(?<y>\d{1,2}(?:\.\d+)?)\s*[）)]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    public AutomationHelpers(IDalamudPluginInterface plugin, IFramework framework, IClientState client,
        IObjectTable objects, IFateTable fates, ICondition conditions, IChatGui chat, IGameGui gui, IPluginLog log, IPartyList party,
        IDataManager data, IGameInteropProvider interop, IPlayerState playerState, ICommandManager commandManager, ITargetManager targets,
        WeatherZone? pagosWeather, SettingsStore? settings = null)
    {
        this.framework = framework; this.client = client; this.objects = objects; this.fates = fates;
        this.conditions = conditions; this.chat = chat; this.gui = gui; this.log = log;
        this.playerState = playerState;
        this.targets = targets; commonParty = party;
        this.settings = settings;
        mesh = new VnavmeshAdapter(plugin); this.data = data;
        mounts = new(objects, data);
        arrivalGame = new(objects, party, fates, mounts);
        navigation = new(mesh, new IslandCrystalGameAdapter(objects, gui, data), arrivalGame);
        surveyTravel = new(mesh, new IslandCrystalGameAdapter(objects, gui, data), arrivalGame);
        bunnyTravel = new(mesh, new IslandCrystalGameAdapter(objects, gui, data), arrivalGame);
        quickElementalSurvey = settings?.Get("common.quickElementalSurvey", false) ?? false;
        combat = new(plugin, commandManager, objects, targets);
        follow = new(navigation, mesh, mounts, combat);
        kills = new(objects, fates, data);
        trigger = new(mesh, mounts, combat, new IslandCrystalGameAdapter(objects, gui, data)); triggerReader = new(objects, data);
        commonJourney = new(plugin, mesh, client, objects, gui, data, conditions, arrivalGame, pagosWeather, message => chat.Print(message));
        commonJourney.CheckPagosRing = settings?.Get("common.checkPagosRing", false) ?? false;
        autoInvite = settings?.Get("common.autoInvite", false) ?? false;
        autoAccept = settings?.Get("common.autoAccept", false) ?? false;
        walkNearPagosDragon = settings?.Get("common.walkNearPagosDragon", false) ?? false;
        var savedRoutes = settings?.Get<Dictionary<ushort, TriggerRouteSet>?>("trigger.routeSets", null);
        if (savedRoutes == null)
        {
            foreach (var (id, points) in settings?.Get("trigger.routes", new Dictionary<ushort, Vector3[]>()) ?? new())
                if (ValidTriggerRoute(points)) triggerRoutes[id] = new(0, [points]);
        }
        else
        {
            foreach (var (id, set) in savedRoutes)
            {
                if (set?.Paths is not { Length: > 0 } paths) continue;
                var valid = paths.Select((path, index) => (path, index)).Where(p => ValidTriggerRoute(p.path))
                    .Take(TriggerRouteSet.MaxRoutes).ToArray();
                if (valid.Length == 0) continue;
                var selected = Array.FindIndex(valid, p => p.index == set.Selected);
                triggerRoutes[id] = new(Math.Max(0, selected), valid.Select(p => p.path).ToArray(),
                    valid.Select(p => set.Names?.ElementAtOrDefault(p.index) ?? "").ToArray());
            }
        }
        rosterSource = new(interop, data, objects);
        roster = new(rosterSource);
        framework.Update += Update;
        chat.ChatMessage += OnChat;
    }

    public AutoTriggerSnapshot TriggerSnapshot => triggerSnapshot;
    private static bool ValidTriggerRoute(Vector3[]? points) =>
        points is { Length: > 0 and <= 128 } && points.All(NavigationRules.Finite);

    public void RecordTriggerPoint(ushort fateId, bool newRoute = false) => commands.Enqueue(() =>
    {
        if (trigger.Active || !SafeState() || objects.LocalPlayer is not { } player
            || !triggerReader.Rules.Any(r => r.FateId == fateId && r.Territory == client.TerritoryType)) return;
        if (!NavigationRules.Finite(player.Position)) return;
        var set = triggerRoutes.GetValueOrDefault(fateId);
        if (newRoute)
        {
            var paths = set?.Paths ?? [];
            if (paths.Length >= TriggerRouteSet.MaxRoutes) return;
            triggerRoutes[fateId] = new(paths.Length, [.. paths, new[] { player.Position }], set?.Names);
        }
        else
        {
            if (set == null || set.Current.Length is 0 or >= 128) return;
            var paths = set.Paths.ToArray();
            paths[set.Selected] = [.. set.Current, player.Position];
            triggerRoutes[fateId] = set with { Paths = paths };
        }
        SaveTriggerRoutes();
    });
    public void SelectTriggerRoute(ushort fateId, int index) => commands.Enqueue(() =>
    {
        if (trigger.Active || !triggerRoutes.TryGetValue(fateId, out var set)
            || index < 0 || index >= set.Paths.Length || index == set.Selected) return;
        triggerRoutes[fateId] = set with { Selected = index };
        SaveTriggerRoutes();
    });
    public void ClearTriggerRoute(ushort fateId) => commands.Enqueue(() =>
    {
        if (trigger.Active || !triggerRoutes.TryGetValue(fateId, out var set)) return;
        var paths = set.Paths.Where((_, index) => index != set.Selected).ToArray();
        if (paths.Length == 0) triggerRoutes.Remove(fateId);
        else triggerRoutes[fateId] = new(Math.Min(set.Selected, paths.Length - 1), paths,
            Enumerable.Range(0, set.Paths.Length).Where(index => index != set.Selected)
                .Select(index => set.Names?.ElementAtOrDefault(index) ?? "").ToArray());
        SaveTriggerRoutes();
    });
    public void RenameTriggerRoute(ushort fateId, int index, string name) => commands.Enqueue(() =>
    {
        if (trigger.Active || !triggerRoutes.TryGetValue(fateId, out var set) || index < 0 || index >= set.Paths.Length) return;
        name = name.Trim();
        if (name.Length > 64) return;
        var names = Enumerable.Range(0, set.Paths.Length).Select(i => set.Names?.ElementAtOrDefault(i) ?? "").ToArray();
        names[index] = name;
        triggerRoutes[fateId] = set with { Names = names };
        SaveTriggerRoutes();
    });
    private void SaveTriggerRoutes()
    {
        settings?.Put("trigger.routeSets", triggerRoutes);
        settings?.Flush();
        triggerSnapshot = triggerSnapshot with { Routes = new Dictionary<ushort, TriggerRouteSet>(triggerRoutes) };
    }
    public void StartTrigger(IReadOnlyList<TriggerPlan> plans, int batch, int maxKills, bool useRotation = true, bool useBmr = true)
    {
        var copy = plans.Select(p => p with { Route = p.Route.ToArray() }).ToArray();
        commands.Enqueue(() =>
        {
            if (trigger.Active) return;
            if (BunnyAutomationActive || SurveyActive || commonJourney.Active || follow.Active || navigation.Active || combat.CleanupPending) { trigger.Stop("请先停止当前任务，等待控制恢复。"); return; }
            if (copy.Any(p => !triggerReader.Rules.Any(r => r.FateId == p.FateId && r.Territory == client.TerritoryType))) { trigger.Stop("所选FATE没有已核实的本岛击杀规则。"); return; }
            if (copy.Any(p => p.Mode == TriggerMode.Recorded) && mounts.Selected == 0) { trigger.Stop("特殊触发请先选择坐骑。"); return; }
            var verified = copy.Select(p => p with { Level = data.GetExcelSheet<Lumina.Excel.Sheets.Fate>()!.GetRow(p.FateId).ClassJobLevel,
                Route = triggerRoutes.GetValueOrDefault(p.FateId)?.Current.ToArray() ?? [] }).ToArray();
            trigger.Start(verified, ReadTriggerFrame(Context()), batch, maxKills, DateTimeOffset.UtcNow, useRotation, useBmr);
        });
    }
    private TriggerFrame ReadTriggerFrame(WorldContext world) => new(world, objects.LocalPlayer?.Position ?? Vector3.Zero,
        SafeState(trigger.Interacting || trigger.Transferring), conditions[ConditionFlag.Mounted], objects.LocalPlayer?.IsCasting ?? false, conditions[ConditionFlag.InCombat],
        TriggerGameReader.Level(), triggerReader.Read(world.Territory), fates.Where(f => f != null && f.TerritoryType.RowId == world.Territory
            && f.State is FateState.Preparing or FateState.Running or FateState.Ending).Select(f => f.FateId).ToHashSet(),
        CanMountForTravel: mounts.Selected != 0,
        Loading: conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] || objects.LocalPlayer == null);
    public AutomationSnapshot Snapshot => snapshot;
    public void SelectLeader(PlayerIdentity identity) => commands.Enqueue(() => SetLeader(identity));
    private void SetLeader(PlayerIdentity identity)
    {
        if (trigger.Active) return;
        follow.Stop("车头已更换。"); listener.Select(identity);
        if (playerState.ContentId != 0) { settings?.Put("leader." + playerState.ContentId, identity); settings?.Flush(); }
    }
    public void SelectCurrentTargetAsLeader() => commands.Enqueue(() =>
    {
        if (targets.Target is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player || player.HomeWorld.RowId == 0) return;
        SetLeader(new(player.Name.TextValue, player.HomeWorld.RowId));
    });
    public void Listen(bool enabled) => commands.Enqueue(() => { if (!enabled && !trigger.Active) follow.Stop("监听已停止。"); listener.SetListening(enabled); });
    public void RefreshPlayers() => commands.Enqueue(roster.Refresh);
    public void SelectMount(uint id) => commands.Enqueue(() => { if (!trigger.Active && !follow.Active) { mounts.Select(id); settings?.Put("mount", mounts.Selected); settings?.Flush(); } });
    public void NavigateTo(MapDestination destination) => commands.Enqueue(() => StartPointNavigation(destination));
    public void GoToEntrance() => commands.Enqueue(() =>
    {
        var crystal = IslandCrystalRoutes.Entrance(client.TerritoryType);
        if (crystal != null && objects.LocalPlayer is { } player)
            StartPointNavigation(new(client.TerritoryType, client.MapId, ArrivalRules.Position(player.Position, crystal.Position, 7), "当前岛入口营地"), false, waitWithoutFate: false);
        else { usingFollow = false; navigation.Stop("请先进入优雷卡岛内。"); }
    });
    private void StartPointNavigation(MapDestination destination, bool useNm = true, bool useCrystals = true, bool waitWithoutFate = true)
    {
        if (BunnyAutomationActive || SurveyActive || commonJourney.Active || trigger.Active) return;
        follow.Stop(); navigation.Stop(); arrivalGame.Reset(); usingFollow = false;
        if (!SafeState()) { navigation.Stop("当前角色状态不允许移动，请结束交互后重试。"); return; }
        pointNm = useNm;
        navigation.Start(destination, Context(), objects.LocalPlayer!.Position, useNm ? ReadFates() : [], DateTimeOffset.UtcNow, mounts.Selected != 0, useCrystals, waitWithoutFate);
    }
    public void Follow(bool enabled) => commands.Enqueue(() =>
    {
        if (!enabled) { if (trigger.Active) return; follow.Stop(); listener.SetListening(false); return; }
        if (BunnyAutomationActive || SurveyActive || commonJourney.Active || trigger.Active) return;
        usingFollow = true;
        if (listener.Leader == null) { follow.Stop("请先选择车头。"); return; }
        if (mounts.Selected == 0) { follow.Stop("请先选择已解锁且可移动的坐骑。"); return; }
        arrivalGame.Reset();
        if (follow.Start(Context(), DateTimeOffset.UtcNow)) listener.SetListening(true);
    });
    public void Stop() => commands.Enqueue(StopTasks);
    public void Navigate() => commands.Enqueue(() =>
    {
        if (BunnyAutomationActive || SurveyActive || commonJourney.Active || trigger.Active) return;
        usingFollow = true; follow.Stop(); arrivalGame.Reset();
        if (listener.Destination is { } goal && objects.LocalPlayer is { } player && CanMove())
        {
            if (mounts.Selected == 0) { follow.Stop("请先选择已解锁的坐骑。"); return; }
            if (follow.Start(Context(), DateTimeOffset.UtcNow, false)) follow.Receive(goal);
        }
        else follow.Stop("没有当前地图的目的地，或玩家状态不允许移动。");
    });

    private WorldContext Context() => new(client.TerritoryType, client.MapId, client.Instance, client.IsLoggedIn ? playerState.ContentId : 0);
    private bool CanMove() => SafeState() && !conditions[ConditionFlag.InCombat];
    private bool SafeState(bool ownCrystalMenu = false) => client.IsLoggedIn && !client.IsPvP && objects.LocalPlayer is { IsDead: false }
        && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51]
        && !conditions[ConditionFlag.WatchingCutscene] && !conditions[ConditionFlag.OccupiedInCutSceneEvent]
        && (!conditions[ConditionFlag.OccupiedInQuestEvent] || ownCrystalMenu) && !conditions[ConditionFlag.OccupiedSummoningBell]
        && (!conditions[ConditionFlag.Occupied] || ownCrystalMenu);

    private IReadOnlyList<FateEntry> ReadFates() => fates
        .Where(f => f != null && f.State == FateState.Running && f.TerritoryType.RowId == client.TerritoryType
            && NavigationRules.IsEurekaNm(f.GameData.Value.EurekaFate, f.GameData.Value.Rule))
        .Select(f => new FateEntry(f.FateId, f.Name.TextValue, f.Position, f.StartTimeEpoch)).ToArray();

    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            settings?.Flush();
            var world = Context();
            var transferScreen = conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] || objects.LocalPlayer == null;
            world = NavigationRules.TransferContext(world, previous, client.IsLoggedIn,
                navigation.Transferring || surveyTravel.Transferring || trigger.Transferring || commonJourney.Transferring
                || bunnyTravel.Transferring, transferScreen);
            if (world != previous)
            {
                wasNearPagosDragon = false;
                nextDragonCheck = default;
                listener.Reset(); navigation.Stop("角色或地图变化，导航已停止。");
                follow.Stop("角色或地图变化，已停止跟随。"); arrivalGame.Reset(); usingFollow = false;
                trigger.Stop("切区或角色变化，触发已停止。"); triggerReader.Reset();
                roster.Reset(); invited.Clear(); lastAccepted = default;
                kills.Reset(); nextKills = default;
                mounts.Reset(); nextMounts = default;
                while (commands.TryDequeue(out var ignored)) { }
                previous = world; nextPlayers = default;
                var savedLeader = settings?.Get<PlayerIdentity?>("leader." + world.PlayerId, null);
                if (world.PlayerId != 0 && savedLeader != null) listener.Select(savedLeader);
            }
            while (commands.TryDequeue(out var command)) command();
            var journeyWasActive = commonJourney.Active;
            commonJourney.Tick(Context(), now);
            if (journeyWasActive && !commonJourney.Active) commonStatus = commonJourney.Status;
            TickSurvey(world, transferScreen, now);
            TickBunnyAutomation(world, transferScreen, now);
            TickPagosDragon(world, transferScreen, now);
            TickCommonParty(now);
            if (!trigger.Active && !follow.Active && !bunnyCombatOwned && combat.CleanupPending && now >= nextCleanup)
            { nextCleanup = now.AddSeconds(1); combat.Disable(); }
            if (SafeState() && playerState.ContentId != 0 && now >= nextMounts)
            { var restoreMount = mounts.Selected == 0; mounts.Refresh(); if (restoreMount) mounts.Select(settings?.Get("mount", 0u) ?? 0); if (mounts.Selected != 0) settings?.Put("mount", mounts.Selected); nextMounts = now.AddSeconds(30); }
            roster.Tick(client.TerritoryType, SafeState(), now);
            if (client.IsLoggedIn && objects.LocalPlayer != null && !transferScreen && now >= nextKills)
            {
                nextKills = now.AddMilliseconds(250);
                foreach (var activity in kills.Update(client.TerritoryType, now))
                    listener.RecordActivity(activity.Time, "小怪击杀", activity.Text, activity.Result);
            }
            if (now >= nextPlayers)
            {
                if (!transferScreen) rosterSource.ReadChangedList(client.TerritoryType);
                players = roster.Merge(PlayerListHelper.GetPlayers(objects), rosterSource.SocialMembers);
                nextPlayers = now.AddSeconds(1);
            }
            if (now >= nextTrigger)
            {
                nextTrigger = now.AddMilliseconds(250);
                var frame = ReadTriggerFrame(world);
                trigger.Tick(frame, now);
                triggerSnapshot = new(trigger.Active, trigger.Status, frame.Level, triggerReader.Areas,
                    new Dictionary<ushort, TriggerRouteSet>(triggerRoutes));
            }
            if (follow.Active && now >= nextFollow)
            {
                nextFollow = now.AddMilliseconds(250);
                var local = objects.LocalPlayer;
                var observation = follow.Goal is { } destination && local != null
                    ? arrivalGame.ReadEncounter(destination, now, roster.OtherPlayers(new(local.Name.TextValue, local.HomeWorld.RowId), now), follow.FateId)
                    : new EncounterObservation(null);
                follow.Tick(new(world, local?.Position ?? Vector3.Zero, SafeState(navigation.Interacting || navigation.Transferring),
                    conditions[ConditionFlag.Mounted], local?.IsCasting ?? false, conditions[ConditionFlag.InCombat], observation.Fate),
                    transferScreen && client.IsLoggedIn && local?.IsDead != true && !conditions[ConditionFlag.InCombat]
                        && !conditions[ConditionFlag.WatchingCutscene], ReadFates(), observation, now);
            }
            else if (!follow.Active && navigation.Active)
                navigation.Tick(world, objects.LocalPlayer?.Position ?? Vector3.Zero, SafeState(navigation.Interacting || navigation.Transferring),
                    transferScreen, conditions[ConditionFlag.Mounted], objects.LocalPlayer?.IsCasting ?? false,
                    pointNm ? ReadFates() : [], now, conditions[ConditionFlag.InCombat]);
            snapshot = new(players, listener.Leader, listener.Listening, listener.Status, listener.Destination,
                listener.History, BunnyAutomationActive || commonJourney.Active || trigger.Active || follow.Active || navigation.Active,
                BunnyAutomationActive ? BunnyAutomationStatus : commonJourney.Active ? commonJourney.Status : trigger.Active ? trigger.Status : usingFollow ? follow.Status : navigation.Status, roster.Snapshot,
                mounts.Owned, mounts.Selected, follow.Following, combat.CleanupPending && !follow.Active && !trigger.Active ? "等待恢复战斗插件状态。" : follow.Status, kills.Snapshot);
            if (navigation.CrystalDecision != reportedCrystalDecision)
            {
                reportedCrystalDecision = navigation.CrystalDecision;
                if (reportedCrystalDecision != null) log.Information("岛内水晶导航：{Decision}", reportedCrystalDecision);
            }
        }
        catch (Exception ex)
        {
            trigger.Stop("游戏状态读取失败，触发已停止。");
            commonJourney.Stop("游戏状态读取失败，常用功能已停止。");
            if (SurveyActive) StopSurvey("游戏状态读取失败，元灵巡查已停止。");
            StopBunnyAutomation("游戏状态读取失败，已停止自动挖宝。");
            triggerSnapshot = triggerSnapshot with { Active = false, Status = trigger.Status };
            navigation.Stop("游戏状态读取失败，导航已停止。");
            follow.Stop("游戏状态读取失败，已关闭战斗控制。"); arrivalGame.Reset();
            snapshot = snapshot with { Navigating = false, NavigationStatus = navigation.Status };
            log.Error(ex, "自动化 helper 更新失败");
        }
    }


    private void OnChat(IChatMessage message)
    {
        var current = snapshot;
        var invite = autoInvite && message.LogKind == XivChatType.Shout;
        if (disposed || !invite && !current.Listening || message.LogKind is not (XivChatType.Say or XivChatType.Yell or XivChatType.Shout)) return;
        try
        {
            // Original payloads are unaffected by display-name or translation plugins.
            var sender = SeString.Parse(message.OriginalSender.Data.Span).Payloads.OfType<PlayerPayload>().FirstOrDefault();
            if (sender == null) return; // Never guess a world from a visible name.
            var identity = new PlayerIdentity(sender.PlayerName, sender.World.RowId);
            var content = SeString.Parse(message.OriginalMessage.Data.Span);
            if (invite && IsPartyRequest(content.TextValue))
            {
                var receivedWorld = previous;
                commands.Enqueue(() => InviteFromShout(identity, receivedWorld));
            }
            if (!current.Listening) return;
            if (identity != current.Leader) return;
            var text = content.TextValue;
            var channel = message.LogKind.ToString();
            var receivedAt = DateTimeOffset.UtcNow;
            var link = content.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
            if (link == null)
            {
                var match = Coordinates.Match(text);
                if (match.Success && float.TryParse(match.Groups["x"].Value, CultureInfo.InvariantCulture, out var x)
                    && float.TryParse(match.Groups["y"].Value, CultureInfo.InvariantCulture, out var y) && x >= 1 && y >= 1)
                    link = new MapLinkPayload(client.TerritoryType, client.MapId, x, y);
            }
            MapDestination? goal = link == null ? null : new(link.TerritoryType.RowId, link.Map.RowId,
                new(link.RawX / 1000f, 0, link.RawY / 1000f), $"{link.PlaceName} {link.CoordinateString}");
            // Chat callbacks only enqueue managed copies; all map/game operations happen in Update.
            commands.Enqueue(() =>
            {
                if (objects.LocalPlayer is not { } player) return;
                var before = listener.Destination;
                listener.Receive(identity, channel, text, goal, client.TerritoryType, client.MapId,
                    player.Position, receivedAt,
                    destination => gui.OpenMapWithMapLink(new MapLinkPayload(destination.Territory, destination.Map,
                        (int)MathF.Round(destination.Position.X * 1000), (int)MathF.Round(destination.Position.Z * 1000))));
                if (follow.Following && listener.Destination is { } accepted && !ReferenceEquals(before, accepted)) follow.Receive(accepted);
            });
        }
        catch (Exception ex) { log.Warning(ex, "解析车头消息失败"); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chat.ChatMessage -= OnChat; framework.Update -= Update;
        trigger.Stop("插件卸载，触发已停止。");
        commonJourney.Dispose();
        surveyTravel.Dispose();
        StopBunnyAutomation("插件已卸载。");
        bunnyTravel.Dispose();
        navigation.Dispose();
        follow.Dispose();
        combat.Disable();
        rosterSource.Dispose();
        while (commands.TryDequeue(out _)) { }
    }
}
