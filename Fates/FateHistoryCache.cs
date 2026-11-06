namespace ULKHelper.Fates;

internal readonly record struct FateHistoryKey(uint RoomId, uint Territory, uint DataCenterId);
internal readonly record struct FateHistoryEntry(ushort Id, DateTimeOffset Appeared, bool Manual = false, bool Crowdsourced = false);
internal sealed record FateRoomHistory(FateHistoryKey Key, FateHistoryEntry[] Entries);

// History stores appearance times only. Polls and imports do not extend a room's lifetime.
internal sealed class FateHistoryCache(IReadOnlyList<IslandMap> islands)
{
    private readonly Dictionary<FateHistoryKey, FateHistoryEntry[]> rooms = new();
    public static bool IsRecent(DateTimeOffset appeared, DateTimeOffset now) =>
        appeared <= now && appeared >= now.AddHours(-3);

    public void Load(IEnumerable<FateRoomHistory> saved, DateTimeOffset now)
    {
        foreach (var room in saved)
            if (room?.Entries != null) Store(room.Key, room.Entries, now);
    }

    public IReadOnlyList<FateHistoryEntry> Read(FateHistoryKey key, DateTimeOffset now) =>
        rooms.TryGetValue(key, out var entries) ? entries.Where(e => IsRecent(e.Appeared, now)).ToArray() : [];

    public void Store(FateHistoryKey key, IEnumerable<FateHistoryEntry> entries, DateTimeOffset now)
    {
        var island = islands.FirstOrDefault(i => i.Territory == key.Territory);
        if (key.RoomId == 0 || key.DataCenterId == 0 || island == null) return;
        var allowed = island.Fates.Select(f => f.Seed.Id).ToHashSet();
        var recent = entries.Where(e => allowed.Contains(e.Id) && IsRecent(e.Appeared, now))
            .GroupBy(e => e.Id).Select(g => g.MaxBy(e => e.Appeared)).ToArray();
        if (recent.Length == 0) rooms.Remove(key);
        else rooms[key] = recent;
    }

    public FateRoomHistory[] PruneAndSnapshot(DateTimeOffset now)
    {
        foreach (var (key, entries) in rooms.ToArray())
        {
            var recent = entries.Where(e => IsRecent(e.Appeared, now)).ToArray();
            if (recent.Length == 0) rooms.Remove(key);
            else rooms[key] = recent;
        }
        return rooms.Select(room => new FateRoomHistory(room.Key, room.Value)).ToArray();
    }
}
