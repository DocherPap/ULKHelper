using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ULKHelper.Crowdsource;

internal sealed record CrowdScope(uint DataCenterId, uint InstanceId, uint TerritoryId);
internal sealed record CrowdSnapshot(CrowdScope Scope, int Epoch, string? TrackId, IReadOnlyDictionary<ushort, DateTimeOffset> Appearances,
    DateTimeOffset RetrievedAt, DateTimeOffset UpdatedAt);

// Official public subscription + JSON snapshot protocol. No reports, credentials or WebSocket.
internal sealed class CrowdsourceClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 24 };
    public async Task<CrowdSnapshot> ReadOnce(CrowdScope scope, CancellationToken cancellation)
    {
        if (scope.DataCenterId == 0 || scope.InstanceId == 0 || scope.TerritoryId is not (732 or 763 or 795 or 827))
            throw new ArgumentException("众包查询缺少有效的大区、房间或岛屿编号。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ce-crowdsource.atmoomen.top/v1/subscriptions")
        {
            Content = JsonContent.Create(new { dataCenterID = scope.DataCenterId, instances = new[] { new { instanceID = scope.InstanceId, territoryID = scope.TerritoryId } } }),
        };
        using var subscribed = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        await EnsureSuccess(subscribed);
        var subscription = JsonSerializer.Deserialize<SubscriptionReply>(await ReadBounded(subscribed, cancellation), JsonOptions)
            ?? throw new InvalidDataException("订阅响应无效。");
        if (subscription.DataCenterID != scope.DataCenterId) throw new InvalidDataException("订阅大区不匹配。");
        var lease = subscription.Subscriptions?.SingleOrDefault(s => s.InstanceID == scope.InstanceId && s.TerritoryID == scope.TerritoryId)
            ?? throw new InvalidDataException("订阅未返回当前房间。");
        if (subscription.ServerTime <= 0 || lease.ExpiresAt <= subscription.ServerTime || lease.ExpiresAt - subscription.ServerTime > 3600)
            throw new InvalidDataException("订阅有效期无效。");
        var uri = ValidateSnapshotUri(lease.SnapshotURL, scope);
        var expiresLocally = DateTimeOffset.UtcNow.AddSeconds(lease.ExpiresAt - subscription.ServerTime);
        using var snapshotRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        snapshotRequest.Headers.CacheControl = new() { NoCache = true };
        using var response = await http.SendAsync(snapshotRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        await EnsureSuccess(response);
        var json = await ReadBounded(response, cancellation).ConfigureAwait(false);
        if (DateTimeOffset.UtcNow >= expiresLocally) throw new InvalidDataException("读取期间订阅已到期。");
        return ParseSnapshot(json, scope, DateTimeOffset.UtcNow);
    }

    internal static Uri ValidateSnapshotUri(string? address, CrowdScope scope)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || !uri.IdnHost.Equals("ce-data.atmoomen.top", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != $"/v1/events/{scope.DataCenterId}/{scope.InstanceId}/{scope.TerritoryId}.json.js")
            throw new InvalidDataException("快照地址不属于官方当前房间。");
        return uri;
    }

    internal static CrowdSnapshot ParseSnapshot(string json, CrowdScope scope, DateTimeOffset now)
    {
        var snapshot = JsonSerializer.Deserialize<SnapshotReply>(json, JsonOptions) ?? throw new InvalidDataException("快照为空。");
        if (snapshot.SchemaVersion != 1 || snapshot.DataCenterID != scope.DataCenterId || snapshot.InstanceID != scope.InstanceId
            || snapshot.TerritoryID != scope.TerritoryId || snapshot.InstanceEpoch < 0 || snapshot.Tracks == null || snapshot.Tracks.Length > 128)
            throw new InvalidDataException("快照格式或房间范围不匹配。");
        // Official histories are ordered by track ordinal, oldest first. Never mix candidates.
        var track = snapshot.Tracks.LastOrDefault();
        var appearances = new Dictionary<ushort, DateTimeOffset>();
        if (track?.Events is { } events)
        {
            if (events.Length > 2048) throw new InvalidDataException("快照事件过多。");
            foreach (var occurrence in events)
            {
                if (occurrence.EventType != "FATE" || occurrence.EventID is 0 or > ushort.MaxValue || occurrence.LastSpawnedAt is not { } seconds
                    || seconds <= 0 || seconds > now.ToUnixTimeSeconds() || seconds < now.AddHours(-24).ToUnixTimeSeconds()) continue;
                var id = (ushort)occurrence.EventID;
                var appeared = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (!appearances.TryGetValue(id, out var previous) || appeared > previous) appearances[id] = appeared;
            }
        }
        var updated = snapshot.UpdatedAt > 0 && snapshot.UpdatedAt <= now.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(snapshot.UpdatedAt) : now;
        return new(scope, snapshot.InstanceEpoch, track?.TrackID, appearances, now, updated);
    }

    private static Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => "当前房间尚无众包记录（404）。",
                HttpStatusCode.Forbidden => "众包服务拒绝访问（403）。",
                HttpStatusCode.TooManyRequests => "众包服务限流（429），本次入场不重复请求。",
                _ => $"众包服务请求失败（{(int)response.StatusCode}）。",
            });
        return Task.CompletedTask;
    }

    private static async Task<string> ReadBounded(HttpResponseMessage response, CancellationToken cancellation)
    {
        const int maximum = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("众包响应过大。");
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + length > maximum) throw new InvalidDataException("众包响应过大。");
            buffer.Write(chunk, 0, length);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private sealed record SubscriptionReply(uint DataCenterID, long ServerTime, Lease[]? Subscriptions);
    private sealed record Lease(uint InstanceID, uint TerritoryID, string? SnapshotURL, long ExpiresAt);
    private sealed record SnapshotReply(int SchemaVersion, uint DataCenterID, uint InstanceID, uint TerritoryID,
        int InstanceEpoch, Track[]? Tracks, long UpdatedAt);
    private sealed record Track(string? TrackID, Occurrence[]? Events);
    private sealed record Occurrence(string? EventType, uint EventID, long? LastSpawnedAt);
}
