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

// Hosts independent helpers. Leader coordinates never implicitly start navigation.
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
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly LeaderListenerHelper listener = new();
    private readonly DestinationNavigationHelper navigation;
    private IReadOnlyList<PlayerEntry> players = [];
    private WorldContext previous;
    private DateTimeOffset nextPlayers;
    private volatile AutomationSnapshot snapshot = new([], null, false, "请选择车头。", null, [], false, "尚未开始导航。");
    private bool disposed;
    private static readonly Regex Coordinates = new(@"[（(]\s*(?<x>\d{1,2}(?:\.\d+)?)\s*[,，]\s*(?<y>\d{1,2}(?:\.\d+)?)\s*[）)]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    public AutomationHelpers(IDalamudPluginInterface plugin, IFramework framework, IClientState client,
        IObjectTable objects, IFateTable fates, ICondition conditions, IChatGui chat, IGameGui gui, IPluginLog log)
    {
        this.framework = framework; this.client = client; this.objects = objects; this.fates = fates;
        this.conditions = conditions; this.chat = chat; this.gui = gui; this.log = log;
        navigation = new(new VnavmeshAdapter(plugin));
        framework.Update += Update;
        chat.ChatMessage += OnChat;
    }

    public AutomationSnapshot Snapshot => snapshot;
    public void SelectLeader(PlayerIdentity identity) => commands.Enqueue(() => listener.Select(identity));
    public void Listen(bool enabled) => commands.Enqueue(() => listener.SetListening(enabled));
    public void Stop() => commands.Enqueue(() => navigation.Stop());
    public void Navigate() => commands.Enqueue(() =>
    {
        if (listener.Destination is { } goal && objects.LocalPlayer is { } player && CanMove())
            navigation.Start(goal, Context(), player.Position, ReadFates(), DateTimeOffset.UtcNow);
        else navigation.Stop("没有当前地图的目的地，或玩家状态不允许移动。");
    });

    private WorldContext Context() => new(client.TerritoryType, client.MapId, client.Instance, objects.LocalPlayer?.GameObjectId ?? 0);
    private bool CanMove() => client.IsLoggedIn && !client.IsPvP && objects.LocalPlayer is { IsDead: false }
        && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51]
        && !conditions[ConditionFlag.WatchingCutscene] && !conditions[ConditionFlag.OccupiedInCutSceneEvent]
        && !conditions[ConditionFlag.OccupiedInQuestEvent] && !conditions[ConditionFlag.OccupiedSummoningBell]
        && !conditions[ConditionFlag.Occupied] && !conditions[ConditionFlag.InCombat];

    private IReadOnlyList<FateEntry> ReadFates() => fates
        .Where(f => f != null && f.State == FateState.Running && f.TerritoryType.RowId == client.TerritoryType
            && NavigationRules.IsEurekaNm(f.GameData.Value.EurekaFate, f.GameData.Value.Rule))
        .Select(f => new FateEntry(f.FateId, f.Name.TextValue, f.Position)).ToArray();

    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var world = Context();
            if (world != previous)
            {
                listener.Reset(); navigation.Stop("角色或地图变化，导航已停止。");
                while (commands.TryDequeue(out var ignored)) { }
                previous = world; nextPlayers = default;
            }
            while (commands.TryDequeue(out var command)) command();
            if (now >= nextPlayers)
            {
                players = PlayerListHelper.GetPlayers(objects);
                nextPlayers = now.AddSeconds(1);
            }
            if (navigation.Active)
                navigation.Tick(world, objects.LocalPlayer?.Position ?? Vector3.Zero, CanMove(), ReadFates(), now);
            snapshot = new(players, listener.Leader, listener.Listening, listener.Status, listener.Destination,
                listener.History, navigation.Active, navigation.Status);
        }
        catch (Exception ex)
        {
            navigation.Stop("游戏状态读取失败，导航已停止。");
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
                listener.Receive(identity, channel, text, goal, client.TerritoryType, client.MapId,
                    player.Position, receivedAt,
                    destination => gui.OpenMapWithMapLink(new MapLinkPayload(destination.Territory, destination.Map,
                        (int)MathF.Round(destination.Position.X * 1000), (int)MathF.Round(destination.Position.Z * 1000))));
            });
        }
        catch (Exception ex) { log.Warning(ex, "解析车头消息失败"); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chat.ChatMessage -= OnChat; framework.Update -= Update;
        navigation.Dispose();
        while (commands.TryDequeue(out _)) { }
    }
}
