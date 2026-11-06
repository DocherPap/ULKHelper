using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
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
    private readonly IPartyList party;
    private readonly ICondition conditions;
    private ulong partyPlayer, partyId;
    private bool wasInParty;
    private readonly FateLedger ledger;
    private readonly FateHistoryCache history;
    private readonly FateNotificationHelper notifications;
    private volatile FateNotificationSettings requestedNotifications = FateNotificationSettings.Default;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly string? error;
    private readonly ZoneServerIdReader roomReader;
    private readonly HttpClient http;
    private readonly EntryCrowdsourceLookup lookup;
    private DateTimeOffset enteredAt, candidateSince;
    private uint roomCandidate, roomId, roomDataCenter;
    private int loadedHistoryCount;
    private (uint Territory, uint Map, uint Instance, ulong Player) session;
    private DateTimeOffset nextUpdate, nextError;
    private volatile FateMapSnapshot snapshot = new(0, null, new Dictionary<ushort, FateRecord>(), "尚未进入优雷卡。");
    private bool disposed;
    private readonly SettingsStore? settings;
    private const string HistorySettingsKey = "map.fateRoomHistory.v1";
    private const string ElementalSettingsKey = "map.elementalSightings";
    private readonly List<ElementalMapPoint> elementalSightings = [];
    private DateTimeOffset nextElementalSave;
    private bool elementalDirty;
    // BNpcName rows from CN 2026.10.06, keyed by the four Eureka territories.
    private static readonly Dictionary<uint, uint> ElementalNames = new()
    {
        [732] = 7184, [763] = 7567, [795] = 7764, [827] = 8131,
    };
    public IReadOnlyList<IslandMap> Islands { get; }
    public FateMapSnapshot Snapshot => snapshot;

    public FateMapTracker(IDataManager data, IFramework framework, IClientState client, IObjectTable objects,
        IFateTable fates, IGameGui gui, IPluginLog log, ISigScanner scanner, IPlayerState playerState, IChatGui chat, IPartyList party, ICondition conditions, SettingsStore? settings = null)
    {
        this.framework = framework; this.client = client; this.objects = objects; this.fates = fates; this.gui = gui; this.log = log;
        this.playerState = playerState;
        this.chat = chat;
        this.party = party; this.conditions = conditions;
        this.settings = settings;
        requestedNotifications = settings?.Get("notifications", FateNotificationSettings.Default) ?? FateNotificationSettings.Default;
        if (!FateNotificationText.Valid(requestedNotifications)) requestedNotifications = FateNotificationSettings.Default;
        settings?.Put("notifications", requestedNotifications);
        try { Islands = FateCatalogLoader.Load(data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!, data.GetExcelSheet<Lumina.Excel.Sheets.Fate>()!); }
        catch (Exception ex) { Islands = []; error = "地图点位数据加载失败，请查看日志并重新加载插件。"; log.Error(ex, "加载优雷卡 FATE 目录失败"); }
        elementalSightings.AddRange((settings?.Get<ElementalMapPoint[]>(ElementalSettingsKey, []) ?? [])
            .Where(p => p != null && p.LastSeen != default && Islands.Any(i => i.Territory == p.Territory && i.MapId == p.MapId
                && ValidMapPosition(p.Position, i)))
            .GroupBy(p => p.Territory).SelectMany(g => g.OrderByDescending(p => p.LastSeen).Take(128))
            .Select(p => p with { Visible = false }));
        ledger = new(Islands);
        history = new(Islands);
        history.Load(settings?.Get<FateRoomHistory[]>(HistorySettingsKey, []) ?? [], DateTimeOffset.UtcNow);
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

    private void ObserveParty(ulong player)
    {
        if (player == 0)
        {
            if (!client.IsLoggedIn) partyPlayer = 0;
            return;
        }
        if (objects.LocalPlayer == null || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]) return;

        var currentId = (ulong)party.PartyId;
        var inParty = currentId != 0 || party.Length > 1;
        var changed = partyPlayer == player && (wasInParty != inParty
            || partyId != 0 && currentId != 0 && partyId != currentId);
        partyPlayer = player;
        partyId = currentId;
        wasInParty = inParty;
        if (!changed) return;

        // This automatic reset intentionally bypasses the manual channel-change disable rule.
        requestedNotifications = requestedNotifications with { Channel = FateNotificationChannel.Echo, Enabled = true };
        settings?.Put("notifications", requestedNotifications);
        settings?.Flush();
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
                SaveElementals();
                SaveHistory(now);
                ledger.Reset(); session = world; nextUpdate = default;
                notifications.Reset();
                lookup.Reset(); enteredAt = now; roomId = roomCandidate = roomDataCenter = 0; candidateSince = now;
                loadedHistoryCount = 0;
                while (commands.TryDequeue(out _)) { }
            }
            ObserveParty(world.Item4);
            var changed = false;
            if (notifications.Settings != requestedNotifications) { notifications.Configure(requestedNotifications); changed = true; }
            while (commands.TryDequeue(out var command)) { command(); changed = true; }
            if (now < nextUpdate && !changed) return;
            nextUpdate = now.AddMilliseconds(500);
            var island = Islands.FirstOrDefault(i => i.Territory == client.TerritoryType);
            var observations = new List<FateObservation>();
            if (island != null && world.Item4 != 0 && objects.LocalPlayer != null
                && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51])
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
                if (roomId == 0 && now - enteredAt >= TimeSpan.FromSeconds(2))
                {
                    var candidate = roomReader.Read(now);
                    if (candidate != roomCandidate) { roomCandidate = candidate; candidateSince = now; }
                    var dc = objects.LocalPlayer?.CurrentWorld.Value.DataCenter.RowId ?? 0;
                    if (candidate != 0 && dc != 0 && now - candidateSince >= TimeSpan.FromSeconds(1))
                    {
                        roomId = candidate;
                        roomDataCenter = dc;
                        var cached = history.Read(new(roomId, island.Territory, roomDataCenter), now);
                        loadedHistoryCount = cached.Count;
                        ledger.Import(cached, island.Territory, now);
                        ConfirmElementalInstance(island.Territory);
                    }
                }
                if (roomId != 0 && !lookup.Attempted) lookup.Start(new(roomDataCenter, roomId, island.Territory), now);
                if (lookup.TakeCompleted() is { } remote && remote.Scope == new CrowdScope(roomDataCenter, roomId, island.Territory))
                    ledger.Import(remote.Appearances, island.Territory, now);
            }
            var elementals = ObserveElementals(island, now);
            snapshot = new(client.TerritoryType, island != null && objects.LocalPlayer is { } player ? MapProjection.WorldToMap(player.Position, island) : null,
                ledger.Snapshot, error ?? (island == null ? "浏览模式：进岛后合并本地历史与众包记录。" : "本岛 FATE 观测与近 3 小时历史 · 每 0.5 秒刷新"),
                roomId, island == null ? null : roomId == 0 ? roomReader.Status : $"本地历史 {loadedHistoryCount} 条 · {lookup.Status}", notifications.Settings, notifications.Status,
                elementals, island == null ? null : enteredAt);
        }
        catch (Exception ex)
        {
            snapshot = snapshot with { Status = "FATE 状态读取失败，保留上次记录；活动数据将标为过期。",
                Elementals = [] };
            nextUpdate = now.AddSeconds(1);
            if (now >= nextError) { nextError = now.AddSeconds(10); log.Warning(ex, "优雷卡地图状态更新失败"); }
        }
    }

    private void SaveHistory(DateTimeOffset now)
    {
        if (!Islands.Any(island => island.Territory == session.Territory)) return;
        if (roomId != 0)
        {
            var entries = ledger.Snapshot.Values.Select(record => new FateHistoryEntry(record.Id, record.Appeared, record.Manual, record.Crowdsourced));
            history.Store(new(roomId, session.Territory, roomDataCenter), entries, now);
        }
        settings?.Put(HistorySettingsKey, history.PruneAndSnapshot(now));
        settings?.Flush(true);
    }

    private bool IsCurrentElemental(ElementalMapPoint point) =>
        roomId != 0 ? point.InstanceId == roomId && point.DataCenterId == roomDataCenter
            : point.InstanceId == 0 && point.LastSeen >= enteredAt;

    private void ConfirmElementalInstance(uint territory)
    {
        // Keep observations made after this entry even if the room id was not ready yet.
        for (var i = elementalSightings.Count - 1; i >= 0; i--)
        {
            var point = elementalSightings[i];
            if (point.Territory != territory || IsCurrentElemental(point)) continue;
            if (point.InstanceId == 0 && point.LastSeen >= enteredAt)
            {
                if (elementalSightings.Any(other => other.Territory == territory && IsCurrentElemental(other)
                    && Vector2.DistanceSquared(other.Position, point.Position) <= .01f))
                    elementalSightings.RemoveAt(i);
                else elementalSightings[i] = point with { InstanceId = roomId, DataCenterId = roomDataCenter };
            }
            else elementalSightings.RemoveAt(i);
            elementalDirty = true;
        }
        SaveElementals();
    }

    private static bool ValidMapPosition(Vector2 position, IslandMap island)
    {
        var uv = MapProjection.ToUv(position, island.SizeFactor);
        return uv.X is >= 0 and <= 1 && uv.Y is >= 0 and <= 1;
    }

    private ElementalMapPoint[] ObserveElementals(IslandMap? island, DateTimeOffset now)
    {
        var visible = new HashSet<ElementalMapPoint>();
        if (island != null && client.IsLoggedIn && objects.LocalPlayer != null
            && client.MapId == island.MapId && ElementalNames.TryGetValue(island.Territory, out var nameId))
        {
            foreach (var npc in objects.OfType<IBattleNpc>())
            {
                if (npc.NameId != nameId || npc.IsDead) continue;
                var position = MapProjection.WorldToMap(npc.Position, island);
                if (!ValidMapPosition(position, island)) continue;
                var index = elementalSightings.FindIndex(p => p.MapId == island.MapId && IsCurrentElemental(p)
                    && Vector2.DistanceSquared(p.Position, position) <= .01f);
                var point = new ElementalMapPoint(island.Territory, island.MapId, position, now,
                    InstanceId: roomId, DataCenterId: roomDataCenter);
                if (index < 0)
                {
                    var sameIsland = elementalSightings.Where(p => p.Territory == island.Territory).ToArray();
                    if (sameIsland.Length >= 128) elementalSightings.Remove(sameIsland.MinBy(p => p.LastSeen)!);
                    elementalSightings.Add(point);
                    nextElementalSave = default;
                }
                else elementalSightings[index] = point;
                visible.Add(point);
                elementalDirty = true;
            }
        }
        if (now >= nextElementalSave && elementalDirty)
        {
            SaveElementals();
            nextElementalSave = now.AddMinutes(1);
        }
        return island == null ? [] : elementalSightings.Where(p => p.Territory == island.Territory && IsCurrentElemental(p))
            .Select(p => visible.Contains(p) ? p with { Visible = true } : p).ToArray();
    }

    private void SaveElementals()
    {
        if (!elementalDirty || settings == null) return;
        settings.Put(ElementalSettingsKey, elementalSightings.ToArray());
        settings.Flush();
        elementalDirty = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; framework.Update -= Update;
        SaveElementals();
        SaveHistory(DateTimeOffset.UtcNow);
        lookup.Dispose(); http.Dispose();
        while (commands.TryDequeue(out _)) { }
    }
}
