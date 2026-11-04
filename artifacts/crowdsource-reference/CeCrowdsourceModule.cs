using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Modules;
using Ocelot.Windows;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BOCCHI.Modules.CeCrowdsource;

[OcelotModule(1007)]
public sealed class CeCrowdsourceModule(Plugin plugin, Config config) : Module(plugin, config)
{
    private const int DataCenterID = 101;
    private const int RetiredConnectionScopeLimit = 32;

    // Keep the pool scoped to the module instance.  A static client used to be
    // disposed when the plugin was reloaded, leaving every later instance
    // with an already-disposed HttpClient and no way to reconnect.

    private static HttpClient CreateClient()
    {
        // The bridge can be restarted independently of Dalamud. Do not keep a
        // dead keep-alive socket forever: recreating the plugin used to be the
        // only thing that flushed this pool after a server restart or a NAT
        // idle timeout.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromSeconds(45),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 4,
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        return http;
    }

    private readonly Panel panel = new();
    private readonly object sync = new();
    private readonly object requestSync = new();
    private readonly CancellationTokenSource cts = new();
    private readonly HttpClient client = CreateClient();
    private readonly SemaphoreSlim uploadGate = new(1, 1);
    private CancellationTokenSource connectionCts = new();
    private readonly List<CancellationTokenSource> retiredConnectionCts = [];

    private DateTime nextHeartbeatAt = DateTime.UtcNow;
    private DateTime nextPollAt = DateTime.UtcNow;
    private DateTime nextUploadAt = DateTime.UtcNow;
    private Task? activeFetch;
    private Task? activeHeartbeat;
    private bool frameworkTickRegistered;
    private bool wasEnabled;
    private long presenceRevision;
    private uint presenceTerritoryId;
    private CeCrowdsourcePresenceScope lastPresenceScope;
    private bool hasPresenceScope;
    private string lastHeartbeatPlayerName = "unknown";
    private string lastHeartbeatWorld = string.Empty;
    private DateTime? zoneServerIdWaitStartedAt;
    private bool zoneServerIdWaitWarned;
    private bool loggedScopeConnected;
    private string? lastWarnedError;

    public override CeCrowdsourceConfig Config
    {
        get => PluginConfig.CeCrowdsourceConfig;
    }

    public override bool ShouldUpdate => true;

    public override bool IsEnabled => Config.IsPropertyEnabled(nameof(Config.Enabled));

    public List<CeRecord> Records { get; private set; } = [];

    public int OnlineCount { get; private set; }

    public int IslandOnlineCount { get; private set; }

    /// <summary>
    /// True when the server has leased this client one of the island's upload
    /// slots. Only a handful of players per island upload; everyone else reads.
    /// </summary>
    public bool IsUploader { get; private set; }

    public int UploaderSlots { get; private set; } = 3;

    /// <summary>
    /// True once an island-scoped heartbeat has been answered for the current
    /// scope. Until then <see cref="IsUploader"/> is false only because nothing
    /// has been asked yet, not because the island's slots are taken.
    /// </summary>
    public bool UploaderLeaseKnown { get; private set; }

    /// <summary>
    /// True while the player is on the island but the zone server ID has not
    /// been read yet. No island-scoped request can be made in this state.
    /// </summary>
    public bool WaitingForZoneServerId { get; private set; }

    /// <summary>How long the server keeps records, in minutes (island lifetime).</summary>
    public int RetentionMinutes { get; private set; } = 180;

    public uint CurrentZoneServerId => CeZoneServerId.Current;

    public uint CurrentTerritoryId => Svc.ClientState.TerritoryType;

    public uint CurrentInstanceId => GetCurrentInstanceId();

    public int InstanceCount { get; private set; }

    public DateTime? LastSyncAt { get; private set; }

    public bool Connected { get; private set; }

    public string? LastError { get; private set; }

    private int uploadCountField;

    public int UploadCount => Volatile.Read(ref uploadCountField);

    private readonly ConcurrentDictionary<string, string> lastUploadedStates = new();

    // Server-driven pacing: the heartbeat reply carries the cadence, so load
    // can be shed centrally without shipping a new plugin build.
    private int serverPollIntervalSeconds = 30;
    private int serverHeartbeatIntervalSeconds = 30;

    // Cached ETag for the CE list. An unchanged island answers 304 with an
    // empty body, which is most of the bandwidth saving at scale.
    private string? ceETag;
    private string? ceETagScope;

    public override void PostInitialize()
    {
        base.PostInitialize();
        wasEnabled = IsEnabled;
        presenceTerritoryId = Svc.ClientState.TerritoryType;
        Svc.Framework.Update += OnFrameworkTick;
        frameworkTickRegistered = true;
        if (TryGetModule<CriticalEncountersModule>(out var ceModule) && ceModule != null)
        {
            ceModule.Tracker.OnInactiveState += OnLocalCeInactive;
        }

        if (TryGetModule<FatesModule>(out var fatesModule) && fatesModule != null)
        {
            fatesModule.tracker.OnFateSpawned += OnLocalPotSpawned;
            fatesModule.tracker.OnFateDespawned += OnLocalPotDespawned;
        }
    }

    public DynamicEventState? GetLocalCeState(uint eventId, uint territoryId)
    {
        if (territoryId != Svc.ClientState.TerritoryType || !ZoneData.IsInOccultCrescent())
        {
            return null;
        }

        if (!TryGetModule<CriticalEncountersModule>(out var ceModule) || ceModule == null)
        {
            return null;
        }

        if (ceModule.CriticalEncounters.TryGetValue(eventId, out var snapshot))
        {
            return snapshot.State;
        }

        // 同区域但本地已不在事件列表：该 CE 已结束
        return DynamicEventState.Inactive;
    }

    public bool IsEffectivelyActive(CeRecord record)
    {
        if (!string.Equals(record.EventType, "CE", StringComparison.OrdinalIgnoreCase))
        {
            return record.IsActive;
        }

        var local = GetLocalCeState(record.EventID, record.TerritoryID);
        return local.HasValue ? local.Value != DynamicEventState.Inactive : record.IsActive;
    }

    public override void Update(UpdateContext context)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (Config.UploadObservations)
        {
            TryUploadObservations();
        }
    }

    private void OnFrameworkTick(IFramework framework)
    {
        if (!IsEnabled)
        {
            if (wasEnabled)
            {
                wasEnabled = false;
                Interlocked.Increment(ref presenceRevision);
                CancelConnectionRequests();
                activeHeartbeat = null;
                activeFetch = null;
                ceETag = null;
                ceETagScope = null;
                lastUploadedStates.Clear();
                lock (sync)
                {
                    Records = [];
                    IslandOnlineCount = 0;
                    InstanceCount = 0;
                    IsUploader = false;
                    UploaderLeaseKnown = false;
                    WaitingForZoneServerId = false;
                    Connected = false;
                    LastSyncAt = null;
                    LastError = null;
                }
            }

            return;
        }

        var now = DateTime.UtcNow;
        if (!wasEnabled)
        {
            wasEnabled = true;
            RestartConnection(now);
        }

        // Re-read the territory every tick. The cached value used to be set
        // only at load and on territory-change callbacks; a plugin loaded on
        // the title screen could keep 0 for the whole session, so the server
        // never saw the player on the island (no island count, no upload slot,
        // no island events).
        Volatile.Write(ref presenceTerritoryId, Svc.ClientState.TerritoryType);
        RefreshPresenceScope(now);

        if (Config.SendHeartbeat && now >= nextHeartbeatAt)
        {
            nextHeartbeatAt = now.AddSeconds(Math.Max(15, serverHeartbeatIntervalSeconds));
            if (activeHeartbeat is not { IsCompleted: false })
            {
                activeHeartbeat = SendHeartbeatAsync();
            }
        }

        if (now >= nextPollAt)
        {
            // The server's cadence wins when it asks for a slower one; the
            // user's setting can only be as aggressive as the server allows.
            var interval = Math.Max(Math.Max(5, Config.PollIntervalSeconds), serverPollIntervalSeconds);
            nextPollAt = now.AddSeconds(interval);
            if (activeFetch is not { IsCompleted: false })
            {
                activeFetch = FetchAsync();
            }
        }
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);
        return true;
    }

    public override void OnTerritoryChanged(uint id)
    {
        Volatile.Write(ref presenceTerritoryId, id);
        RestartConnection(DateTime.UtcNow);
    }

    public override void Dispose()
    {
        if (TryGetModule<CriticalEncountersModule>(out var ceModule) && ceModule != null)
        {
            ceModule.Tracker.OnInactiveState -= OnLocalCeInactive;
        }

        if (TryGetModule<FatesModule>(out var fatesModule) && fatesModule != null)
        {
            fatesModule.tracker.OnFateSpawned -= OnLocalPotSpawned;
            fatesModule.tracker.OnFateDespawned -= OnLocalPotDespawned;
        }

        if (frameworkTickRegistered)
        {
            Svc.Framework.Update -= OnFrameworkTick;
            frameworkTickRegistered = false;
        }

        cts.Cancel();
        CancelAndDisposeRequestScopes();
        client.Dispose();
        cts.Dispose();
        base.Dispose();
    }

    private void OnLocalCeInactive(CriticalEncounterSnapshot ev)
    {
        if (!IsEnabled || !IsUploader || !Config.UploadObservations || !ZoneData.IsInOccultCrescent())
        {
            return;
        }

        var territoryId = Svc.ClientState.TerritoryType;
        if (!CeCrowdsourceObservationPolicy.ShouldUploadCriticalEncounter(
                territoryId,
                ev.EventType,
                ev.DynamicEventId))
        {
            return;
        }

        var key = $"{territoryId}:CE:{ev.DynamicEventId}";
        lastUploadedStates[key] = "Inactive";
        _ = UploadObservationAsync(territoryId, ev.DynamicEventId, "Inactive", ev.StartTimestamp, ev.Name, "CE");
    }

    private void OnLocalPotSpawned(Fate fate)
    {
        if (!fate.IsPotFate())
        {
            return;
        }

        UploadPotObservation(fate, "Running");
    }

    private void OnLocalPotDespawned(Fate fate)
    {
        if (!fate.IsPotFate())
        {
            return;
        }

        UploadPotObservation(fate, "Inactive");
    }

    private void TryUploadPotObservations()
    {
        if (!TryGetModule<FatesModule>(out var fatesModule) || fatesModule == null)
        {
            return;
        }

        foreach (var fate in fatesModule.fates.Values.ToArray())
        {
            if (fate.IsPotFate())
            {
                UploadPotObservation(fate, "Running");
            }
        }
    }

    private void UploadPotObservation(Fate fate, string state)
    {
        if (!IsEnabled || !IsUploader || !Config.UploadObservations || !ZoneData.IsInOccultCrescent())
        {
            return;
        }

        var territoryId = Svc.ClientState.TerritoryType;
        var key = $"{territoryId}:FATE:{fate.Id}";
        if (lastUploadedStates.TryGetValue(key, out var last) && last == state)
        {
            return;
        }

        lastUploadedStates[key] = state;
        _ = UploadObservationAsync(territoryId, fate.Id, state, (int)Math.Min(fate.SpawnedAt, int.MaxValue), fate.Name, "FATE");
    }

    private async Task SendHeartbeatAsync()
    {
        var revision = Volatile.Read(ref presenceRevision);
        var requestToken = CaptureRequestToken();
        var presence = CapturePresenceScope();
        if (presence.IsIsland && presence.ZoneServerId == 0)
        {
            // A zero zone ID cannot identify the current island. Sending it
            // would make the server classify this client as outside and a
            // stats request with zone=0 would return the all-island aggregate.
            if (revision == Volatile.Read(ref presenceRevision))
            {
                nextHeartbeatAt = DateTime.UtcNow.AddSeconds(2);
            }
            return;
        }

        try
        {
            var (name, world) = CacheHeartbeatIdentity();
            var payload = new
            {
                name,
                world,
                dataCenterID = DataCenterID,
                zoneServerID = presence.ZoneServerId,
                territoryID = presence.TerritoryId,
                instanceID = FormatInstanceId(presence.InstanceId),
            };
            var json = JsonSerializer.Serialize(payload);
            var baseUrl = Config.ServerUrl.Trim().TrimEnd('/');
            using var response = await SendWithRetryAsync(
                async token =>
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(Config.ApiToken))
                    {
                        content.Headers.TryAddWithoutValidation("X-Auth-Token", Config.ApiToken);
                    }

                    return await client.PostAsync($"{baseUrl}/api/heartbeat", content, token);
                },
                requestToken,
                "heartbeat");

            if (!response.IsSuccessStatusCode)
            {
                HandleHttpFailure(
                    revision,
                    "heartbeat",
                    response.StatusCode,
                    isHeartbeat: true,
                    requestToken: requestToken);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(requestToken);
            var stats = JsonSerializer.Deserialize<CeHeartbeatResponse>(body, JsonOptions);
            if (stats == null)
            {
                HandleRequestFailure(
                    revision,
                    "heartbeat",
                    new JsonException("心跳响应为空"),
                    isHeartbeat: true,
                    requestToken: requestToken);
                return;
            }

            lock (sync)
            {
                if (revision != Volatile.Read(ref presenceRevision))
                {
                    return;
                }

                OnlineCount = stats.Online;
                if (CeCrowdsourcePresencePolicy.CanPublishIslandPresence(
                        presence.IsIsland, presence.ZoneServerId))
                {
                    IslandOnlineCount = stats.IslandOnline;
                }
                // The heartbeat renews the upload lease, so this flag is what
                // gates uploading until the next beat.
                var islandScoped = CeCrowdsourcePresencePolicy.CanPublishIslandPresence(
                    presence.IsIsland, presence.ZoneServerId);
                IsUploader = islandScoped && stats.IsUploader;
                UploaderLeaseKnown = islandScoped;
                if (stats.UploaderSlots > 0)
                {
                    UploaderSlots = stats.UploaderSlots;
                }

                if (stats.RetentionMinutes > 0)
                {
                    RetentionMinutes = stats.RetentionMinutes;
                }

                if (stats.PollIntervalSeconds > 0)
                {
                    serverPollIntervalSeconds = stats.PollIntervalSeconds;
                }

                if (stats.HeartbeatIntervalSeconds > 0)
                {
                    serverHeartbeatIntervalSeconds = stats.HeartbeatIntervalSeconds;
                }

                // A successful heartbeat is a valid server connection even
                // while the event list is waiting for its next poll.
                Connected = true;
                LastError = null;
            }
        }
        catch (OperationCanceledException) when (
            requestToken.IsCancellationRequested || cts.IsCancellationRequested)
        {
            // Scope changes and plugin disposal deliberately cancel old
            // requests. They are not connection failures for the new scope.
        }
        catch (Exception ex)
        {
            HandleRequestFailure(
                revision,
                "heartbeat",
                ex,
                isHeartbeat: true,
                requestToken: requestToken);
        }
    }

    private async Task FetchAsync()
    {
        var revision = Volatile.Read(ref presenceRevision);
        var requestToken = CaptureRequestToken();
        var presence = CapturePresenceScope();
        if (!presence.IsIsland)
        {
            // Island event history is scoped data. Do not query /api/ce with
            // zone=0 from outside the island, which would return every record.
            lock (sync)
            {
                if (revision != Volatile.Read(ref presenceRevision))
                {
                    return;
                }

                Records = [];
                IslandOnlineCount = 0;
                InstanceCount = 0;
                Connected = false;
                LastSyncAt = null;
            }

            if (revision == Volatile.Read(ref presenceRevision))
            {
                nextPollAt = DateTime.UtcNow.AddSeconds(5);
            }
            return;
        }

        if (presence.IsIsland && presence.ZoneServerId == 0)
        {
            // Wait until the zone server identity is available before making
            // an island-scoped request. zone=0 is an aggregate query on the
            // bridge server, not a valid current-island scope.
            if (revision == Volatile.Read(ref presenceRevision))
            {
                nextPollAt = DateTime.UtcNow.AddSeconds(2);
            }
            return;
        }

        try
        {
            var baseUrl = Config.ServerUrl.Trim().TrimEnd('/');
            var territory = presence.IsIsland ? presence.TerritoryId : 0;
            var zone = presence.IsIsland ? presence.ZoneServerId : 0;
            var instance = presence.IsIsland ? FormatInstanceId(presence.InstanceId) : string.Empty;
            // Scope the query to this exact island. The server answers with
            // that island's records only, already limited to the retention
            // window, so no client-side instance filtering is needed.
            var scope = $"{zone}:{instance}:{territory}";
            var ceUrl = $"{baseUrl}/api/ce?dc={DataCenterID}&zone={zone}&instance={instance}&territory={territory}";
            var statsUrl = $"{baseUrl}/api/stats?dc={DataCenterID}&zone={zone}&instance={instance}";

            using var ceResponse = await SendWithRetryAsync(
                async token =>
                {
                    // A retry needs a fresh HttpRequestMessage; HttpClient
                    // does not allow sending the same request instance twice.
                    using var request = new HttpRequestMessage(HttpMethod.Get, ceUrl);
                    if (ceETag != null && ceETagScope == scope)
                    {
                        request.Headers.TryAddWithoutValidation("If-None-Match", ceETag);
                    }

                    return await client.SendAsync(request, token);
                },
                requestToken,
                "CE");

            if (ceResponse.StatusCode == HttpStatusCode.NotModified)
            {
                // Nothing changed on this island; keep the records we have.
                lock (sync)
                {
                    if (revision != Volatile.Read(ref presenceRevision))
                    {
                        return;
                    }

                    LastSyncAt = DateTime.Now;
                    Connected = true;
                    LastError = null;
                }
                await FetchStatsAsync(statsUrl, revision, presence.IsIsland, requestToken);
                return;
            }

            if (!ceResponse.IsSuccessStatusCode)
            {
                HandleHttpFailure(
                    revision,
                    "CE",
                    ceResponse.StatusCode,
                    isHeartbeat: false,
                    requestToken: requestToken);
                return;
            }

            var ceBody = await ceResponse.Content.ReadAsStringAsync(requestToken);
            var ceList = JsonSerializer.Deserialize<CeListResponse>(ceBody, JsonOptions);
            if (ceList == null)
            {
                HandleRequestFailure(
                    revision,
                    "CE",
                    new JsonException("CE 响应解析失败"),
                    isHeartbeat: false,
                    requestToken: requestToken);
                return;
            }

            if (revision != Volatile.Read(ref presenceRevision))
            {
                return;
            }

            ceETag = ceResponse.Headers.ETag?.Tag;
            ceETagScope = scope;

            await FetchStatsAsync(statsUrl, revision, presence.IsIsland, requestToken);

            lock (sync)
            {
                if (revision != Volatile.Read(ref presenceRevision))
                {
                    return;
                }

                if (ceList.RetentionMinutes > 0)
                {
                    RetentionMinutes = ceList.RetentionMinutes;
                }

                Records = (ceList.Events ?? [])
                    .Where(r => r.DataCenterID == DataCenterID)
                    .Where(CeCrowdsourceDisplayPolicy.ShouldDisplayRecord)
                    .OrderBy(r => r.TerritoryID)
                    .ThenBy(r => r.EventID)
                    .ToList();
                LastSyncAt = DateTime.Now;
                Connected = true;
                LastError = null;
            }

            if (!loggedScopeConnected)
            {
                loggedScopeConnected = true;
                Svc.Log.Info(
                    $"[CeCrowdsource] synced island zone={zone} instance={instance} territory={territory}: "
                    + $"{ceList.Events?.Length ?? 0} records from server, {Records.Count} shown");
            }
        }
        catch (OperationCanceledException) when (
            requestToken.IsCancellationRequested || cts.IsCancellationRequested)
        {
            // Scope changes and plugin disposal deliberately cancel old
            // requests. They are not connection failures for the new scope.
        }
        catch (Exception ex)
        {
            HandleRequestFailure(
                revision,
                "CE",
                ex,
                isHeartbeat: false,
                requestToken: requestToken);
        }
    }

    private async Task FetchStatsAsync(
        string statsUrl,
        long revision,
        bool isIslandScope,
        CancellationToken requestToken)
    {
        try
        {
            using var statsResponse = await SendWithRetryAsync(
                token => client.GetAsync(statsUrl, token),
                requestToken,
                "stats");
            if (!statsResponse.IsSuccessStatusCode)
            {
                Svc.Log.Debug($"[CeCrowdsource] stats failed: {statsResponse.StatusCode}");
                return;
            }

            var statsBody = await statsResponse.Content.ReadAsStringAsync(requestToken);
            var stats = JsonSerializer.Deserialize<CeStatsResponse>(statsBody, JsonOptions);
            if (stats == null)
            {
                Svc.Log.Debug("[CeCrowdsource] stats response was empty");
                return;
            }

            lock (sync)
            {
                if (revision != Volatile.Read(ref presenceRevision))
                {
                    return;
                }

                OnlineCount = stats.Online;
                IslandOnlineCount = isIslandScope ? stats.IslandOnline : 0;
                InstanceCount = isIslandScope ? stats.Instances : 0;
                if (stats.RetentionMinutes > 0)
                {
                    RetentionMinutes = stats.RetentionMinutes;
                }
            }
        }
        catch (OperationCanceledException) when (
            requestToken.IsCancellationRequested || cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Stats are auxiliary telemetry. A transient stats failure must
            // not discard an otherwise valid CE response or turn the panel
            // into a disconnected/empty state.
            Svc.Log.Debug($"[CeCrowdsource] stats error (CE data retained): {DescribeException(ex)}");
        }
    }

    private static string GetInstanceIdString()
    {
        return FormatInstanceId(GetCurrentInstanceId());
    }

    private static string FormatInstanceId(uint id)
    {
        return id > 0 ? id.ToString() : string.Empty;
    }

    private CeCrowdsourcePresenceScope CapturePresenceScope()
    {
        var territoryId = Volatile.Read(ref presenceTerritoryId);
        if (!ZoneData.IsOccultCrescentTerritory(territoryId))
        {
            return new CeCrowdsourcePresenceScope(false, territoryId, 0, 0);
        }

        // Territory membership is authoritative. Nearby player objects are
        // streamed and may disappear briefly; they must never clear presence.
        return new CeCrowdsourcePresenceScope(
            true,
            territoryId,
            CeZoneServerId.Current,
            GetCurrentInstanceId());
    }

    private void RefreshPresenceScope(DateTime now)
    {
        var current = CapturePresenceScope();
        TrackZoneServerIdWait(current, now);
        if (!hasPresenceScope)
        {
            lastPresenceScope = current;
            hasPresenceScope = true;
            return;
        }

        if (!CeCrowdsourcePresencePolicy.ShouldRestartConnection(lastPresenceScope, current))
        {
            return;
        }

        lastPresenceScope = current;
        RestartConnection(now);
    }

    /// <summary>
    /// On the island every request is skipped until the zone server ID is
    /// known. That used to be silent: the panel just said "connecting" forever
    /// and the uploader line claimed the island was full. Surface the wait, and
    /// leave a log line if it never resolves so it can be diagnosed.
    /// </summary>
    private void TrackZoneServerIdWait(CeCrowdsourcePresenceScope current, DateTime now)
    {
        var waiting = current.IsIsland && current.ZoneServerId == 0;
        WaitingForZoneServerId = waiting;
        if (!waiting)
        {
            zoneServerIdWaitStartedAt = null;
            return;
        }

        zoneServerIdWaitStartedAt ??= now;
        if (!zoneServerIdWaitWarned && now - zoneServerIdWaitStartedAt.Value > TimeSpan.FromSeconds(30))
        {
            zoneServerIdWaitWarned = true;
            Svc.Log.Warning(
                $"[CeCrowdsource] zone server ID still 0 after 30s on territory {current.TerritoryId}; "
                + "crowdsource requests are paused until it resolves");
        }
    }

    private void RestartConnection(DateTime now)
    {
        Interlocked.Increment(ref presenceRevision);

        // A scope change (entering/leaving the island or resolving a new
        // zone/instance) must actively retire in-flight requests.  Merely
        // dropping their Task references allowed old requests to occupy the
        // four connection-pool slots until the eight-second timeout, while a
        // new request was started for the new scope.
        CancelConnectionRequests();

        // Old requests are revision-guarded, so release their scheduling slots
        // immediately. This lets a newly resolved island scope connect without
        // waiting for the previous request's timeout or regular cadence.
        activeHeartbeat = null;
        activeFetch = null;
        nextHeartbeatAt = now;
        nextPollAt = now;
        nextUploadAt = now;
        ceETag = null;
        ceETagScope = null;
        lastUploadedStates.Clear();

        lock (sync)
        {
            IslandOnlineCount = 0;
            InstanceCount = 0;
            IsUploader = false;
            UploaderLeaseKnown = false;
            Records = [];
            Connected = false;
            LastSyncAt = null;
            LastError = null;
        }

        zoneServerIdWaitStartedAt = null;
        zoneServerIdWaitWarned = false;
        loggedScopeConnected = false;
        lastWarnedError = null;
    }

    private CancellationToken CaptureRequestToken()
    {
        lock (requestSync)
        {
            return connectionCts.Token;
        }
    }

    private void CancelConnectionRequests()
    {
        CancellationTokenSource previous;
        lock (requestSync)
        {
            previous = connectionCts;
            connectionCts = new CancellationTokenSource();
            // Keep the cancelled source alive until module disposal. An
            // in-flight HttpClient operation may still be unwinding and can
            // legally inspect its token after cancellation.
            retiredConnectionCts.Add(previous);
            if (retiredConnectionCts.Count > RetiredConnectionScopeLimit)
            {
                // Do not dispose an evicted source while a late task might
                // still be inspecting its token. Dropping our reference lets
                // it be collected once that task finishes.
                retiredConnectionCts.RemoveAt(0);
            }
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent dispose already retired this source.
        }
    }

    private void CancelAndDisposeRequestScopes()
    {
        CancellationTokenSource[] scopes;
        lock (requestSync)
        {
            scopes = [connectionCts, .. retiredConnectionCts];
            retiredConnectionCts.Clear();
        }

        foreach (var scope in scopes)
        {
            try
            {
                scope.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Nothing else is required during teardown.
            }

            scope.Dispose();
        }
    }

    private (string Name, string World) CacheHeartbeatIdentity()
    {
        try
        {
            var player = Svc.Objects?.LocalPlayer;
            if (player == null)
            {
                return (lastHeartbeatPlayerName, lastHeartbeatWorld);
            }

            try
            {
                var name = player.Name.TextValue;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    lastHeartbeatPlayerName = name;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Debug($"[CeCrowdsource] player identity not ready: {ex.Message}");
            }

            try
            {
                var world = player.HomeWorld.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(world))
                {
                    lastHeartbeatWorld = world;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Debug($"[CeCrowdsource] player world not ready: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            // ObjectTable itself can be unavailable while Dalamud is still
            // constructing the plugin. Identity is optional; loading BOCCHI is
            // not, so keep the defaults and retry on the next heartbeat.
            Svc.Log.Debug($"[CeCrowdsource] identity cache failed: {ex.Message}");
        }

        return (lastHeartbeatPlayerName, lastHeartbeatWorld);
    }

    private static unsafe uint GetCurrentInstanceId()
    {
        try
        {
            return FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance()->PublicInstance.InstanceId;
        }
        catch (Exception ex)
        {
            Svc.Log.Debug($"[CeCrowdsource] instance read failed: {ex.Message}");
            return 0;
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken requestToken,
        string operation)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var response = await send(requestToken);
                if (attempt == 0 && IsTransientStatus(response.StatusCode))
                {
                    response.Dispose();
                    Svc.Log.Debug($"[CeCrowdsource] {operation} returned {response.StatusCode}; retrying once");
                    await Task.Delay(TimeSpan.FromMilliseconds(250), requestToken);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (
                IsTransientTransportException(ex)
                && !requestToken.IsCancellationRequested
                && !cts.IsCancellationRequested)
            {
                lastException = ex;
                if (attempt == 0)
                {
                    Svc.Log.Debug(
                        $"[CeCrowdsource] {operation} transport failed; retrying once: {DescribeException(ex)}");
                    await Task.Delay(TimeSpan.FromMilliseconds(250), requestToken);
                }
            }
        }

        if (lastException != null)
        {
            throw lastException;
        }

        throw new HttpRequestException($"{operation} 请求失败");
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        return status is >= 500 and <= 599;
    }

    private static bool IsTransientTransportException(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or SocketException
            or OperationCanceledException;
    }

    private void HandleHttpFailure(
        long revision,
        string operation,
        HttpStatusCode statusCode,
        bool isHeartbeat,
        CancellationToken requestToken)
    {
        if (requestToken.IsCancellationRequested || cts.IsCancellationRequested)
        {
            return;
        }

        var message = $"{operation} 接口 {statusCode}";
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            message += "（鉴权失败，请检查服务端令牌）";
        }
        else if (statusCode == HttpStatusCode.TooManyRequests)
        {
            message += "（请求频率受限，稍后自动重试）";
        }

        if (!TrySetError(revision, message, out var errorChanged))
        {
            return;
        }

        var retrySeconds = statusCode == HttpStatusCode.TooManyRequests ? 15 : 5;
        if (isHeartbeat)
        {
            nextHeartbeatAt = DateTime.UtcNow.AddSeconds(retrySeconds);
        }
        else
        {
            nextPollAt = DateTime.UtcNow.AddSeconds(retrySeconds);
        }

        // Repeated identical failures stay at debug level; the first one (and
        // any change of cause) reaches the normal log so a stuck connection
        // can be diagnosed from dalamud.log without debug logging enabled.
        if (errorChanged)
        {
            Svc.Log.Warning($"[CeCrowdsource] {operation} failed; retrying: {message}");
        }
        else
        {
            Svc.Log.Debug($"[CeCrowdsource] {operation} failed; retrying: {message}");
        }
    }

    private void HandleRequestFailure(
        long revision,
        string operation,
        Exception exception,
        bool isHeartbeat,
        CancellationToken requestToken)
    {
        if (requestToken.IsCancellationRequested || cts.IsCancellationRequested)
        {
            return;
        }

        var message = DescribeException(exception);
        if (!TrySetError(revision, message, out var errorChanged))
        {
            return;
        }

        if (isHeartbeat)
        {
            nextHeartbeatAt = DateTime.UtcNow.AddSeconds(5);
        }
        else
        {
            nextPollAt = DateTime.UtcNow.AddSeconds(5);
        }

        // Repeated identical failures stay at debug level; the first one (and
        // any change of cause) reaches the normal log so a stuck connection
        // can be diagnosed from dalamud.log without debug logging enabled.
        if (errorChanged)
        {
            Svc.Log.Warning($"[CeCrowdsource] {operation} failed; retrying: {message}");
        }
        else
        {
            Svc.Log.Debug($"[CeCrowdsource] {operation} failed; retrying: {message}");
        }
    }

    private static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        var current = exception;
        for (var depth = 0; current != null && depth < 3; depth++, current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        if (messages.Count == 0)
        {
            return exception.GetType().Name;
        }

        var text = string.Join(" / ", messages);
        return text.Length <= 240 ? text : text[..240] + "…";
    }

    private bool TrySetError(long revision, string message, out bool changed)
    {
        lock (sync)
        {
            changed = false;
            if (revision != Volatile.Read(ref presenceRevision))
            {
                return false;
            }

            changed = !string.Equals(lastWarnedError, message, StringComparison.Ordinal);
            lastWarnedError = message;
            Connected = false;
            LastError = message;
            return true;
        }
    }

    private void TryUploadObservations()
    {
        // Only lease holders upload. On a busy island most clients are pure
        // readers, which is what keeps write traffic flat as the population
        // grows instead of scaling with it.
        if (!IsUploader || !ZoneData.IsInOccultCrescent() || DateTime.UtcNow < nextUploadAt)
        {
            return;
        }

        nextUploadAt = DateTime.UtcNow.AddSeconds(10);
        var territoryId = Svc.ClientState.TerritoryType;

        try
        {
            if (!TryGetModule<CriticalEncountersModule>(out var ceModule) || ceModule == null)
            {
                return;
            }

            var snapshots = ceModule.CriticalEncounters.Values.ToArray();
            foreach (var ev in snapshots)
            {
                if (ev.State == DynamicEventState.Inactive)
                {
                    continue;
                }

                // Ordinary CEs and known Forked Tower events share the CE
                // history feed. Other high-type dynamic events stay excluded.
                if (!CeCrowdsourceObservationPolicy.ShouldUploadCriticalEncounter(
                        territoryId,
                        ev.EventType,
                        ev.DynamicEventId))
                {
                    continue;
                }

                var key = $"{territoryId}:CE:{ev.DynamicEventId}";
                var stateName = ev.State.ToString();
                if (lastUploadedStates.TryGetValue(key, out var last) && last == stateName)
                {
                    continue;
                }

                lastUploadedStates[key] = stateName;
                _ = UploadObservationAsync(territoryId, ev.DynamicEventId, stateName, ev.StartTimestamp, ev.Name, "CE");
            }

            TryUploadPotObservations();
        }
        catch (Exception ex)
        {
            Svc.Log.Debug($"[CeCrowdsource] upload scan failed: {ex.Message}");
        }
    }

    private async Task UploadObservationAsync(
        uint territoryId,
        uint eventId,
        string state,
        int startTimestamp,
        string name,
        string eventType)
    {
        var observationKey = $"{territoryId}:{eventType}:{eventId}";
        var requestToken = CaptureRequestToken();
        var enteredUploadGate = false;
        try
        {
            // Upload observations one at a time so a burst of locally visible
            // events cannot consume every pooled connection and starve the
            // heartbeat/event reader that keeps the panel connected.
            await uploadGate.WaitAsync(requestToken);
            enteredUploadGate = true;

            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Once a CE is in Battle, StartTimestamp holds the countdown
            // deadline rather than the spawn time, so it points into the
            // future. Never report a spawn that hasn't happened yet.
            var spawnedAt = startTimestamp > 0 && startTimestamp <= nowSec ? startTimestamp : nowSec;
            var payload = new
            {
                dataCenterID = DataCenterID,
                zoneServerID = CeZoneServerId.Current,
                territoryID = territoryId,
                eventType,
                eventID = eventId,
                name,
                lastSpawnedAt = spawnedAt,
                observedState = state,
                instanceID = GetInstanceIdString(),
                playerName = CacheHeartbeatIdentity().Name,
            };
            var json = JsonSerializer.Serialize(payload);
            var baseUrl = Config.ServerUrl.Trim().TrimEnd('/');
            using var response = await SendWithRetryAsync(
                async token =>
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(Config.ApiToken))
                    {
                        content.Headers.TryAddWithoutValidation("X-Auth-Token", Config.ApiToken);
                    }

                    return await client.PostAsync($"{baseUrl}/api/ce/observe", content, token);
                },
                requestToken,
                "upload");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(requestToken);
                var result = JsonSerializer.Deserialize<CeObserveResponse>(body, JsonOptions);
                if (result is { IsUploader: false })
                {
                    // The island already has enough reporters. Stand down now
                    // rather than retrying until the next heartbeat renews.
                    lock (sync)
                    {
                        IsUploader = false;
                    }

                    // Forget the state we just recorded so the observation is
                    // re-sent if this client later regains a slot.
                    lastUploadedStates.TryRemove(observationKey, out _);
                    return;
                }

                Interlocked.Increment(ref uploadCountField);
                Svc.Log.Info($"[CeCrowdsource] uploaded {eventType} {eventId} state {state}");
            }
            else
            {
                lastUploadedStates.TryRemove(observationKey, out _);
                Svc.Log.Debug($"[CeCrowdsource] upload failed: {response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (
            requestToken.IsCancellationRequested || cts.IsCancellationRequested)
        {
            lastUploadedStates.TryRemove(observationKey, out _);
        }
        catch (Exception ex)
        {
            lastUploadedStates.TryRemove(observationKey, out _);
            Svc.Log.Debug($"[CeCrowdsource] upload error: {DescribeException(ex)}");
        }
        finally
        {
            if (enteredUploadGate)
            {
                uploadGate.Release();
            }
        }
    }


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

}











