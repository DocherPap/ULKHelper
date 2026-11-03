using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ULKHelper.Crowdsource;
using ULKHelper.GameInterop;

namespace ULKHelper.Fates;

internal sealed class FateMapTracker : IFateMapProvider, IDisposable
{
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly IFateTable fates;
    private readonly IGameGui gui;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly FateLedger ledger;
    private readonly FateNotificationHelper notifications;
    private volatile FateNotificationSettings requestedNotifications = FateNotificationSettings.Default;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly string? error;
    private readonly ZoneServerIdReader roomReader;
    private readonly HttpClient http;
    private readonly EntryCrowdsourceLookup lookup;
    private DateTimeOffset enteredAt, candidateSince;
    private uint roomCandidate, roomId;
    private (uint Territory, uint Map, uint Instance, ulong Player) session;
    private DateTimeOffset nextUpdate, nextError;
    private volatile FateMapSnapshot snapshot = new(0, null, new Dictionary<ushort, FateRecord>(), "尚未进入优雷卡。");
    private bool disposed;
    private readonly SettingsStore? settings;
    public IReadOnlyList<IslandMap> Islands { get; }
    public FateMapSnapshot Snapshot => snapshot;

    public FateMapTracker(IDataManager data, IFramework framework, IClientState client, IObjectTable objects,
        IFateTable fates, IGameGui gui, IPluginLog log, ISigScanner scanner, IPlayerState playerState, IChatGui chat, SettingsStore? settings = null)
    {
        this.framework = framework; this.client = client; this.objects = objects; this.fates = fates; this.gui = gui; this.log = log;
        this.playerState = playerState;
        this.chat = chat;
        this.settings = settings;
        requestedNotifications = settings?.Get("notifications", FateNotificationSettings.Default) ?? FateNotificationSettings.Default;
        if (!FateNotificationText.Valid(requestedNotifications)) requestedNotifications = FateNotificationSettings.Default;
        settings?.Put("notifications", requestedNotifications);
        try { Islands = FateCatalogLoader.Load(data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!, data.GetExcelSheet<Lumina.Excel.Sheets.Fate>()!); }
        catch (Exception ex) { Islands = []; error = "地图点位数据加载失败，请查看日志并重新加载插件。"; log.Error(ex, "加载优雷卡 FATE 目录失败"); }
        ledger = new(Islands);
        notifications = new(Islands);
        notifications.Configure(requestedNotifications); notifications.Reset();
        snapshot = snapshot with { Notifications = requestedNotifications, NotificationStatus = notifications.Status };
        roomReader = new(scanner);
        http = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ULKHelper/0.1");
        var crowdsource = new CrowdsourceClient(http);
        lookup = new(crowdsource.ReadOnce);
        framework.Update += Update;
    }

    public void Mark(FateDefinition fate) => commands.Enqueue(() =>
    {
        var island = Islands.FirstOrDefault(i => i.Territory == fate.Seed.Territory);
        if (island == null) return;
        var point = MapProjection.MapToWorld(new(fate.Seed.X, fate.Seed.Y), island);
        gui.OpenMapWithMapLink(new MapLinkPayload(island.Territory, island.MapId, (int)(point.X * 1000), (int)(point.Z * 1000)));
    });
    public void RecordAppearance(ushort id, double minutesAgo) => commands.Enqueue(() =>
    {
        if (Islands.Any(i => i.Territory == client.TerritoryType && i.Fates.Any(f => f.Seed.Id == id)))
            ledger.Record(id, minutesAgo, DateTimeOffset.UtcNow);
    });
    public void ClearRecord(ushort id) => commands.Enqueue(() => ledger.Clear(id));
    public void ConfigureNotifications(FateNotificationSettings value)
    {
        value = value.ChangeFrom(requestedNotifications);
        requestedNotifications = value;
        settings?.Put("notifications", value); settings?.Flush();
    }

    private void Update(IFramework frame)
    {
        if (disposed) return;
        var now = DateTimeOffset.UtcNow;
        try
        {
            var world = (client.TerritoryType, client.MapId, client.Instance, client.IsLoggedIn ? playerState.ContentId : 0);
            if (world != session)
            {
                ledger.Reset(); session = world; nextUpdate = default;
                notifications.Reset();
                lookup.Reset(); enteredAt = now; roomId = roomCandidate = 0; candidateSince = now;
                while (commands.TryDequeue(out _)) { }
            }
            var changed = false;
            if (notifications.Settings != requestedNotifications) { notifications.Configure(requestedNotifications); changed = true; }
            while (commands.TryDequeue(out var command)) { command(); changed = true; }
            if (now < nextUpdate && !changed) return;
            nextUpdate = now.AddMilliseconds(500);
            var island = Islands.FirstOrDefault(i => i.Territory == client.TerritoryType);
            var observations = new List<FateObservation>();
            if (island != null && client.IsLoggedIn)
            {
                foreach (var fate in fates)
                {
                    if (fate == null || fate.TerritoryType.RowId != island.Territory
                        || fate.State is not (FateState.Running or FateState.Preparing or FateState.Ending or FateState.Ended or FateState.Failed)) continue;
                    var start = fate.StartTimeEpoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(fate.StartTimeEpoch) : (DateTimeOffset?)null;
                    var end = fate.TimeRemaining > 0 ? now.AddSeconds(fate.TimeRemaining) : (DateTimeOffset?)null;
                    observations.Add(new(fate.FateId, start, end, fate.Progress,
                        fate.State == FateState.Preparing ? FateActivity.Preparing
                            : fate.State == FateState.Running ? FateActivity.Running : FateActivity.Ended, MapProjection.WorldToMap(fate.Position, island)));
                }
                ledger.Observe(observations, now);
                notifications.Observe(island.Territory, observations, now);
                notifications.SendNext(now, (notification, settings) => FateChatSender.Send(island, notification, settings, chat));
                if (!lookup.Attempted && now - enteredAt >= TimeSpan.FromSeconds(2))
                {
                    var candidate = roomReader.Read(now);
                    if (candidate != roomCandidate) { roomCandidate = candidate; candidateSince = now; }
                    var dc = objects.LocalPlayer?.CurrentWorld.Value.DataCenter.RowId ?? 0;
                    if (candidate != 0 && dc != 0 && now - candidateSince >= TimeSpan.FromSeconds(1))
                    { roomId = candidate; lookup.Start(new(dc, roomId, island.Territory), now); }
                }
                if (lookup.TakeCompleted() is { } remote && remote.Scope.InstanceId == roomId && remote.Scope.TerritoryId == island.Territory)
                    ledger.Import(remote.Appearances, island.Territory, now);
            }
            snapshot = new(client.TerritoryType, island != null && objects.LocalPlayer is { } player ? MapProjection.WorldToMap(player.Position, island) : null,
                ledger.Snapshot, error ?? (island == null ? "浏览模式：进入对应岛屿后读取一次众包记录。" : "本次实例本地观察 · 每 0.5 秒刷新 · 离岛后清空"),
                roomId, island == null ? null : lookup.Attempted ? lookup.Status : roomReader.Status, notifications.Settings, notifications.Status);
        }
        catch (Exception ex)
        {
            snapshot = snapshot with { Status = "FATE 状态读取失败，保留上次记录；活动数据将标为过期。" };
            nextUpdate = now.AddSeconds(1);
            if (now >= nextError) { nextError = now.AddSeconds(10); log.Warning(ex, "优雷卡地图状态更新失败"); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; framework.Update -= Update;
        lookup.Dispose(); http.Dispose();
        while (commands.TryDequeue(out _)) { }
    }
}
