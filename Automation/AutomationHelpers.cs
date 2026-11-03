using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ULKHelper.Automation;

// Game operations run on framework thread. Leader coordinates move only in an explicitly started session.
internal sealed class AutomationHelpers : IAutomationHelpers, IDisposable
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
    private readonly SoloDebugHelper debug;
    private readonly INavigationMesh mesh;
    private readonly IDataManager data;
    private string diagnostics = "尚未读取现场状态。";
    private volatile SoloDebugSnapshot debugSnapshot = SoloDebugSnapshot.Empty;
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
        IDataManager data, IGameInteropProvider interop, IPlayerState playerState, ICommandManager commandManager, ITargetManager targets, SettingsStore? settings = null)
    {
        this.framework = framework; this.client = client; this.objects = objects; this.fates = fates;
        this.conditions = conditions; this.chat = chat; this.gui = gui; this.log = log;
        this.playerState = playerState;
        this.targets = targets;
        this.settings = settings;
        mesh = new VnavmeshAdapter(plugin); this.data = data;
        mounts = new(objects, data);
        arrivalGame = new(objects, party, fates, mounts);
        navigation = new(mesh, new IslandCrystalGameAdapter(objects, gui, data), arrivalGame);
        combat = new(plugin, commandManager, objects, targets);
        follow = new(navigation, mesh, mounts, combat);
        kills = new(objects, fates, data);
        debug = new(new IslandCrystalGameAdapter(objects, gui, data), mounts);
        rosterSource = new(interop, data, objects);
        roster = new(rosterSource);
        framework.Update += Update;
        chat.ChatMessage += OnChat;
    }

    public AutomationSnapshot Snapshot => snapshot;
    public SoloDebugSnapshot DebugSnapshot => debugSnapshot;
    public void RunDebug(SoloDebugAction action, uint crystalId = 0) => commands.Enqueue(() =>
    {
        if (action == SoloDebugAction.Inspect)
        {
            try { ReadDiagnostics(); debug.Record("现场状态已读取，没有移动或开关循环。", DateTimeOffset.UtcNow); }
            catch (Exception ex) { debug.Record("读取现场失败：" + ex.Message, DateTimeOffset.UtcNow); }
            return;
        }
        if (follow.Active || navigation.Active || combat.CleanupPending || debug.Active)
        { debug.Record("测试未开始：请先停止当前导航、跟随或测试，等待控制恢复。", DateTimeOffset.UtcNow); return; }
        try { ReadDiagnostics(); } catch (Exception ex) { diagnostics = "自动读取现场失败：" + ex.Message; }
        debug.Start(action, DebugFrame(Context(), false), IslandCrystalRoutes.All.FirstOrDefault(c => c.PlaceNameId == crystalId), DateTimeOffset.UtcNow);
    });
    public void StopDebug() => commands.Enqueue(() => debug.Stop("已手动停止测试；已提交的传送无法撤回。", DateTimeOffset.UtcNow));
    private SoloDebugFrame DebugFrame(WorldContext world, bool loading) => new(world, objects.LocalPlayer?.Position ?? Vector3.Zero,
        SafeState(debug.Interacting || debug.Transferring), loading, conditions[ConditionFlag.Mounted], objects.LocalPlayer?.IsCasting ?? false, conditions[ConditionFlag.InCombat]);
    private void ReadDiagnostics()
    {
        var player = objects.LocalPlayer;
        string navigationInfo;
        try { navigationInfo = $"Ready={mesh.Ready} Busy={mesh.Busy} 路线点={mesh.Route.Count}"; }
        catch (Exception ex) { navigationInfo = "接口不可用：" + ex.Message; }
        var crystalObjects = player == null ? [] : objects.Where(o => o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte
            or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && Vector3.Distance(o.Position, player.Position) <= 15)
            .Select(o => $"{o.Name.TextValue} Kind={o.ObjectKind} BaseId={o.BaseId} 位置={o.Position} 3D距离={Vector3.Distance(o.Position, player.Position):0.0}m 2D距离={NavigationRules.Distance(o.Position, player.Position):0.0}m 可选中={o.IsTargetable}").ToArray();
        var selectedObject = targets.Target;
        var targetText = selectedObject == null ? "无" : $"{selectedObject.Name.TextValue} Kind={selectedObject.ObjectKind} BaseId={selectedObject.BaseId} 位置={selectedObject.Position} 可选中={selectedObject.IsTargetable}";
        var build = typeof(Plugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        diagnostics = $"读取时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n版本：{typeof(Plugin).Assembly.GetName().Version} / {build}\n区域={client.TerritoryType} 地图={client.MapId} 分线={client.Instance}\n"
            + $"位置={player?.Position} 骑乘={conditions[ConditionFlag.Mounted]} 战斗={conditions[ConditionFlag.InCombat]}\n"
            + $"vnavmesh {navigationInfo}\n"
            + combat.DescribeState() + "\n"
            + $"当前绑定NM={follow.FateId} 传送测试={debug.Transferring}\n"
            + $"当前目标：{targetText}\n"
            + $"水晶路线：{navigation.CrystalDecision ?? "未规划"}\n近处水晶/事件对象：\n" + string.Join("\n", crystalObjects);
    }
    public void SelectLeader(PlayerIdentity identity) => commands.Enqueue(() => SetLeader(identity));
    private void SetLeader(PlayerIdentity identity)
    {
        follow.Stop("车头已更换。"); listener.Select(identity);
        if (playerState.ContentId != 0) { settings?.Put("leader." + playerState.ContentId, identity); settings?.Flush(); }
    }
    public void SelectCurrentTargetAsLeader() => commands.Enqueue(() =>
    {
        if (targets.Target is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player || player.HomeWorld.RowId == 0) return;
        SetLeader(new(player.Name.TextValue, player.HomeWorld.RowId));
    });
    public void Listen(bool enabled) => commands.Enqueue(() => { if (!enabled) follow.Stop("监听已停止。"); listener.SetListening(enabled); });
    public void RefreshPlayers() => commands.Enqueue(roster.Refresh);
    public void SelectMount(uint id) => commands.Enqueue(() => { if (!follow.Active && !debug.Active) { mounts.Select(id); settings?.Put("mount", mounts.Selected); settings?.Flush(); } });
    public void NavigateTo(MapDestination destination) => commands.Enqueue(() => StartPointNavigation(destination));
    public void GoToEntrance() => commands.Enqueue(() =>
    {
        var crystal = IslandCrystalRoutes.Entrance(client.TerritoryType);
        if (crystal != null && objects.LocalPlayer is { } player)
            StartPointNavigation(new(client.TerritoryType, client.MapId, ArrivalRules.Position(player.Position, crystal.Position, 7), "当前岛入口营地"), false);
        else { usingFollow = false; navigation.Stop("请先进入优雷卡岛内。"); }
    });
    private void StartPointNavigation(MapDestination destination, bool useNm = true)
    {
        if (debug.Active) { debug.Record("请先停止单次调试测试。", DateTimeOffset.UtcNow); return; }
        follow.Stop(); navigation.Stop(); arrivalGame.Reset(); usingFollow = false;
        if (!CanMove()) { navigation.Stop("当前角色状态不允许移动，请脱战并结束交互后重试。"); return; }
        pointNm = useNm;
        navigation.Start(destination, Context(), objects.LocalPlayer!.Position, useNm ? ReadFates() : [], DateTimeOffset.UtcNow, mounts.Selected != 0);
    }
    public void Follow(bool enabled) => commands.Enqueue(() =>
    {
        if (!enabled) { follow.Stop(); listener.SetListening(false); return; }
        if (debug.Active) { debug.Record("请先停止单次调试测试。", DateTimeOffset.UtcNow); return; }
        usingFollow = true;
        if (listener.Leader == null) { follow.Stop("请先选择车头。"); return; }
        if (mounts.Selected == 0) { follow.Stop("请先选择已解锁且可移动的坐骑。"); return; }
        arrivalGame.Reset();
        if (follow.Start(Context(), DateTimeOffset.UtcNow)) listener.SetListening(true);
    });
    public void Stop() => commands.Enqueue(() => { follow.Stop(); navigation.Stop(); arrivalGame.Reset(); if (debug.Active) debug.Stop("已停止测试。", DateTimeOffset.UtcNow); });
    public void Navigate() => commands.Enqueue(() =>
    {
        if (debug.Active) { debug.Record("请先停止单次调试测试。", DateTimeOffset.UtcNow); return; }
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
            world = NavigationRules.TransferContext(world, previous, client.IsLoggedIn, navigation.Transferring || debug.Transferring, transferScreen);
            if (world != previous)
            {
                listener.Reset(); navigation.Stop("角色或地图变化，导航已停止。");
                follow.Stop("角色或地图变化，已停止跟随。"); arrivalGame.Reset(); usingFollow = false;
                roster.Reset();
                kills.Reset(); nextKills = default;
                if (debug.Active) debug.Stop("切区/角色变化，测试停止。", now);
                mounts.Reset(); nextMounts = default;
                while (commands.TryDequeue(out var ignored)) { }
                previous = world; nextPlayers = default;
                var savedLeader = settings?.Get<PlayerIdentity?>("leader." + world.PlayerId, null);
                if (world.PlayerId != 0 && savedLeader != null) listener.Select(savedLeader);
            }
            while (commands.TryDequeue(out var command)) command();
            if (!follow.Active && combat.CleanupPending && now >= nextCleanup)
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
                players = roster.Merge(PlayerListHelper.GetPlayers(objects));
                nextPlayers = now.AddSeconds(1);
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
                navigation.Tick(world, objects.LocalPlayer?.Position ?? Vector3.Zero, !conditions[ConditionFlag.InCombat]
                    && (CanMove() || (navigation.Interacting || navigation.Transferring) && SafeState(true)),
                    transferScreen, conditions[ConditionFlag.Mounted], objects.LocalPlayer?.IsCasting ?? false, pointNm ? ReadFates() : [], now);
            var debugWasActive = debug.Active;
            debug.Tick(DebugFrame(world, transferScreen), now);
            if (debugWasActive && !debug.Active)
                try { ReadDiagnostics(); } catch (Exception ex) { diagnostics += "\n结束时读取失败：" + ex.Message; }
            debugSnapshot = new(debug.Active, debug.Status, diagnostics,
                IslandCrystalRoutes.All.Where(c => c.Territory == client.TerritoryType)
                    .Select(c => new DebugCrystal(c.PlaceNameId, data.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>()!.GetRow(c.PlaceNameId).Name.ExtractText())).ToArray(), debug.History);
            snapshot = new(players, listener.Leader, listener.Listening, listener.Status, listener.Destination,
                listener.History, follow.Active || navigation.Active, usingFollow ? follow.Status : navigation.Status, roster.Snapshot,
                mounts.Owned, mounts.Selected, follow.Following, combat.CleanupPending && !follow.Active ? "等待恢复战斗插件状态。" : follow.Status, kills.Snapshot);
            if (navigation.CrystalDecision != reportedCrystalDecision)
            {
                reportedCrystalDecision = navigation.CrystalDecision;
                if (reportedCrystalDecision != null) log.Information("岛内水晶导航：{Decision}", reportedCrystalDecision);
            }
        }
        catch (Exception ex)
        {
            if (debug.Active) debug.Stop("读取现场异常，调试测试已停止。", DateTimeOffset.UtcNow);
            debugSnapshot = debugSnapshot with { Active = false, Status = "读取现场异常：" + ex.Message };
            navigation.Stop("游戏状态读取失败，导航已停止。");
            follow.Stop("游戏状态读取失败，已关闭战斗控制。"); arrivalGame.Reset();
            snapshot = snapshot with { Navigating = false, NavigationStatus = navigation.Status };
            log.Error(ex, "自动化 helper 更新失败");
        }
    }

    private void OnChat(IChatMessage message)
    {
        var current = snapshot;
        if (disposed || !current.Listening || message.LogKind is not (XivChatType.Say or XivChatType.Yell or XivChatType.Shout)) return;
        try
        {
            // Original payloads are unaffected by display-name or translation plugins.
            var sender = SeString.Parse(message.OriginalSender.Data.Span).Payloads.OfType<PlayerPayload>().FirstOrDefault();
            if (sender == null) return; // Never guess a world from a visible name.
            var identity = new PlayerIdentity(sender.PlayerName, sender.World.RowId);
            if (identity != current.Leader) return;
            var content = SeString.Parse(message.OriginalMessage.Data.Span);
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
        debug.Stop("插件卸载，测试已停止。", DateTimeOffset.UtcNow);
        navigation.Dispose();
        follow.Dispose();
        combat.Disable();
        rosterSource.Dispose();
        while (commands.TryDequeue(out _)) { }
    }
}
