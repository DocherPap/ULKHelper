using System.Numerics;

namespace ULKHelper.Fates;

internal sealed record FateSeed(uint Territory, ushort Id, float X, float Y, string ShortName, double CooldownMinutes,
    uint WeatherId, bool Night, bool Bunny);
internal sealed record FateDefinition(FateSeed Seed, string Name, byte Level)
{
    public string Trigger => FateTriggers.All.GetValueOrDefault(Seed.Id, "触发条件待核实。");
}
internal sealed record IslandMap(uint Territory, uint MapId, string Name, string TexturePath, float SizeFactor,
    short OffsetX, short OffsetY, IReadOnlyList<FateDefinition> Fates);
internal enum FateActivity { Preparing, Running, Ended }
internal sealed record FateObservation(ushort Id, DateTimeOffset? Started, DateTimeOffset? Ends, byte Progress, FateActivity Activity,
    Vector2? MapPosition = null);
internal sealed record FateRecord(ushort Id, DateTimeOffset Appeared, DateTimeOffset? Ends, byte Progress,
    FateActivity Activity, DateTimeOffset LastSeen, bool Active, bool Manual, bool Crowdsourced = false);
internal sealed record ElementalMapPoint(uint Territory, uint MapId, Vector2 Position, DateTimeOffset LastSeen, bool Visible = false);
internal sealed record FateMapSnapshot(uint CurrentTerritory, Vector2? PlayerMapPosition, IReadOnlyDictionary<ushort, FateRecord> Records, string Status,
    uint RoomId = 0, string? CrowdsourceStatus = null, FateNotificationSettings? Notifications = null, string? NotificationStatus = null,
    IReadOnlyList<ElementalMapPoint>? Elementals = null);

internal interface IFateMapProvider
{
    IReadOnlyList<IslandMap> Islands { get; }
    FateMapSnapshot Snapshot { get; }
    void Mark(FateDefinition fate);
    void RecordAppearance(ushort id, double minutesAgo);
    void ClearRecord(ushort id);
    void ConfigureNotifications(FateNotificationSettings settings);
}

internal static class MapProjection
{
    public static Vector2 ToUv(Vector2 map, float sizeFactor) => (map - Vector2.One) * (sizeFactor / 100f / 41f);
    public static Vector2 ToMap(Vector2 uv, float sizeFactor) => uv * (41f / (sizeFactor / 100f)) + Vector2.One;
    public static Vector2 WorldToMap(Vector3 world, IslandMap map) => ToMap(
        (new Vector2(world.X + map.OffsetX, world.Z + map.OffsetY) * (map.SizeFactor / 100f) + new Vector2(1024)) / 2048, map.SizeFactor);
    public static Vector3 MapToWorld(Vector2 point, IslandMap map)
    {
        var world = (ToUv(point, map.SizeFactor) * 2048 - new Vector2(1024)) / (map.SizeFactor / 100f);
        return new(world.X - map.OffsetX, 0, world.Y - map.OffsetY);
    }
}
