using System;
using System.Text.Json.Serialization;

namespace BOCCHI.Modules.CeCrowdsource;

public sealed record CeRecord(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("dataCenterID")] int DataCenterID,
    [property: JsonPropertyName("zoneServerID")] int ZoneServerID,
    [property: JsonPropertyName("territoryID")] uint TerritoryID,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("eventID")] uint EventID,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("lastSpawnedAt")] long LastSpawnedAt,
    [property: JsonPropertyName("observedState")] string? ObservedState,
    [property: JsonPropertyName("sourceCount")] int SourceCount,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    // Note: there is deliberately no Uploader field. The server records who
    // reported an event but never returns it, so having no property here means
    // no future UI code can accidentally surface a player identity.
    [property: JsonPropertyName("instanceID")] string? InstanceID,
    [property: JsonPropertyName("isActive")] bool? ServerIsActive)
{
    [JsonIgnore]
    public bool IsActive =>
        ServerIsActive ?? ObservedState is "Register" or "Warmup" or "Battle" or "Running";

    public DateTime LastSpawnedLocal
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            // When the record was observed. This is the best available stand-in
            // for a spawn time, and unlike "now" it stays correct for records
            // that arrived a while ago.
            var observed = UpdatedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAt) : now;
            if (observed > now)
            {
                observed = now;
            }

            var time = LastSpawnedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(LastSpawnedAt) : observed;

            // 塔类事件、以及战斗中的 CE，其 StartTimestamp 是未来的倒计时截止
            // 时间，直接显示会变成"凌晨出现"。任何未来时间都退回到观测时间。
            // 服务端也会做同样的清洗，这里是对旧服务端/旧数据的兜底。
            if (time > now)
            {
                time = observed;
            }

            return time.ToLocalTime().DateTime;
        }
    }
}

public sealed record CeListResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("instanceID")] string? InstanceID,
    [property: JsonPropertyName("retentionMinutes")] int RetentionMinutes,
    [property: JsonPropertyName("events")] CeRecord[]? Events);

// The server reports aggregate counts only. It deliberately does not return a
// per-player list any more: uploader identities are recorded server-side and
// never sent back out.
public sealed record CeStatsResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("online")] int Online,
    [property: JsonPropertyName("islandOnline")] int IslandOnline,
    [property: JsonPropertyName("instances")] int Instances,
    [property: JsonPropertyName("events")] int Events,
    [property: JsonPropertyName("retentionMinutes")] int RetentionMinutes);

/// <summary>
/// Heartbeat reply. The server leases a small number of upload slots per
/// island and renews them here, so <see cref="IsUploader"/> is the client's
/// authority on whether it should be uploading at all.
/// </summary>
public sealed record CeHeartbeatResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("online")] int Online,
    [property: JsonPropertyName("islandOnline")] int IslandOnline,
    [property: JsonPropertyName("isUploader")] bool IsUploader,
    [property: JsonPropertyName("uploaderSlots")] int UploaderSlots,
    [property: JsonPropertyName("pollIntervalSeconds")] int PollIntervalSeconds,
    [property: JsonPropertyName("heartbeatIntervalSeconds")] int HeartbeatIntervalSeconds,
    [property: JsonPropertyName("retentionMinutes")] int RetentionMinutes);

public sealed record CeObserveResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("isUploader")] bool IsUploader,
    [property: JsonPropertyName("reason")] string? Reason);



