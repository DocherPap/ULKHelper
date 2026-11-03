namespace ULKHelper.Fates;

// Scoped to one continuous visit. No persistence: a public instance number is not a Eureka room ID.
internal sealed class FateLedger(IReadOnlyList<IslandMap> islands)
{
    private readonly Dictionary<ushort, FateRecord> records = new();
    private readonly HashSet<ushort> ids = islands.SelectMany(i => i.Fates).Select(f => f.Seed.Id).ToHashSet();
    public IReadOnlyDictionary<ushort, FateRecord> Snapshot => new Dictionary<ushort, FateRecord>(records);
    public void Reset() => records.Clear();
    public void Clear(ushort id) => records.Remove(id);
    public void Import(IReadOnlyDictionary<ushort, DateTimeOffset> appearances, uint territory, DateTimeOffset now)
    {
        var allowed = islands.Where(i => i.Territory == territory).SelectMany(i => i.Fates).Select(f => f.Seed.Id).ToHashSet();
        foreach (var (id, appeared) in appearances)
        {
            if (!allowed.Contains(id) || appeared > now || appeared < now.AddHours(-24)) continue;
            if (records.TryGetValue(id, out var existing) && (existing.Active || existing.Appeared >= appeared)) continue;
            records[id] = new(id, appeared, null, 0, FateActivity.Ended, now, false, false, true);
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
            var appeared = observation.Started is { } start && start <= now && now - start < TimeSpan.FromHours(2)
                ? start : previous != null && (previous.Active || observation.Activity == FateActivity.Ended) ? previous.Appeared : now;
            records[observation.Id] = new(observation.Id, appeared, observation.Ends, observation.Progress,
                observation.Activity, now, observation.Activity != FateActivity.Ended, false);
        }
        foreach (var (id, value) in records.ToArray())
            // A missing entry can mean incomplete client data. Keep it stale until its known end,
            // or until an explicit terminal state is observed; do not invent a kill event.
            if (value.Active && !seen.Contains(id) && value.Ends is { } end && now >= end)
                records[id] = value with { Active = false };
    }
}
