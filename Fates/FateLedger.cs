namespace ULKHelper.Fates;

// Current visit's live state plus the latest historical appearance of each FATE.
internal sealed class FateLedger(IReadOnlyList<IslandMap> islands)
{
    private readonly Dictionary<ushort, FateRecord> records = new();
    private readonly HashSet<ushort> ids = islands.SelectMany(i => i.Fates).Select(f => f.Seed.Id).ToHashSet();
    private readonly HashSet<ushort> bunnyIds = islands.SelectMany(i => i.Fates).Where(f => f.Seed.Bunny).Select(f => f.Seed.Id).ToHashSet();
    public IReadOnlyDictionary<ushort, FateRecord> Snapshot => new Dictionary<ushort, FateRecord>(records);
    public void Reset() => records.Clear();
    public void Clear(ushort id) => records.Remove(id);
    public void Import(IReadOnlyDictionary<ushort, DateTimeOffset> appearances, uint territory, DateTimeOffset now)
        => Import(appearances.Select(p => new FateHistoryEntry(p.Key, p.Value, Crowdsourced: true)), territory, now);

    public void Import(IEnumerable<FateHistoryEntry> appearances, uint territory, DateTimeOffset now)
    {
        var allowed = islands.Where(i => i.Territory == territory).SelectMany(i => i.Fates).Select(f => f.Seed.Id).ToHashSet();
        foreach (var entry in appearances)
        {
            if (!allowed.Contains(entry.Id) || !FateHistoryCache.IsRecent(entry.Appeared, now)) continue;
            records.TryGetValue(entry.Id, out var existing);
            if (existing != null && existing.Appeared >= entry.Appeared) continue;
            records[entry.Id] = existing is { Active: true }
                ? existing with { Appeared = entry.Appeared, Manual = entry.Manual, Crowdsourced = entry.Crowdsourced }
                : new(entry.Id, entry.Appeared, null, 0, FateActivity.Ended, entry.Appeared, false, entry.Manual, entry.Crowdsourced);
        }
    }
    public void Record(ushort id, double minutesAgo, DateTimeOffset now)
    {
        if (!ids.Contains(id) || !double.IsFinite(minutesAgo) || minutesAgo is < 0 or > 240) return;
        if (records.TryGetValue(id, out var existing) && existing.Active) return;
        records[id] = new(id, now.AddMinutes(-minutesAgo), null, 0, FateActivity.Running, now, false, true);
    }

    public void Observe(IReadOnlyList<FateObservation> observations, DateTimeOffset now)
    {
        var seen = new HashSet<ushort>();
        foreach (var observation in observations)
        {
            if (!ids.Contains(observation.Id)) continue;
            seen.Add(observation.Id);
            records.TryGetValue(observation.Id, out var previous);
            var validStart = observation.Started is { } start && FateHistoryCache.IsRecent(start, now);
            if (!validStart && observation.Activity == FateActivity.Ended && previous == null) continue;
            var usePrevious = !validStart && previous != null && (previous.Active || observation.Activity == FateActivity.Ended);
            var appeared = validStart ? observation.Started!.Value : usePrevious ? previous!.Appeared : now;
            var ends = observation.Ends;
            if (bunnyIds.Contains(observation.Id))
            {
                if (ends == null && previous is { Active: true } && previous.Appeared >= appeared
                    && observation.Activity != FateActivity.Ended) ends = previous.Ends;
            }
            var keepHistory = previous != null && (previous.Appeared > appeared || usePrevious);
            records[observation.Id] = new(observation.Id, keepHistory ? previous!.Appeared : appeared, ends, observation.Progress,
                observation.Activity, now, observation.Activity != FateActivity.Ended,
                keepHistory && previous!.Manual, keepHistory && previous!.Crowdsourced);
        }
        foreach (var (id, value) in records.ToArray())
            // A missing entry can mean incomplete client data. Keep it stale until its known end,
            // or until an explicit terminal state is observed; do not invent a kill event.
            if (value.Active && !seen.Contains(id) && value.Ends is { } end && now >= end)
                records[id] = value with { Active = false };
    }
}
