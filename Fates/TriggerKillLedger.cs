using System.Text.RegularExpressions;

namespace ULKHelper.Fates;

internal sealed record TriggerMobRule(ushort FateId, uint Territory, string FateName, string MobName, byte Level, IReadOnlySet<uint> NameIds);
internal sealed record TriggerKillCount(ushort FateId, string FateName, string MobName, int Count);
internal sealed record TriggerNpcLife(ulong Id, uint NameId, byte Level, bool Dead);
internal sealed record TriggerKillEvent(DateTimeOffset Time, string Text, string Result);

internal static class TriggerMobCatalog
{
    public static IReadOnlyList<TriggerMobRule> Resolve(IEnumerable<(uint Id, string Name)> names)
    {
        var byName = names.Where(n => n.Id != 0 && n.Name.Length > 0).GroupBy(n => n.Name.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<uint>)g.Select(n => n.Id).ToHashSet(), StringComparer.Ordinal);
        var pattern = new Regex(@"击杀\s+(?<level>\d+)\s+级\s+(?<name>.+?)(?:\s+后|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var result = new List<TriggerMobRule>();
        foreach (var fate in FateSeeds.All.Where(f => !f.Bunny))
        {
            var description = FateTriggers.All.GetValueOrDefault(fate.Id, "");
            var match = pattern.Match(description);
            if (!match.Success || !byte.TryParse(match.Groups["level"].Value, out var level)) continue;
            var name = match.Groups["name"].Value.Trim();
            if (byName.TryGetValue(name, out var ids)) result.Add(new(fate.Id, fate.Territory, fate.ShortName, name, level, ids));
        }
        return result;
    }
}

// Counts locally witnessed life-to-death transitions only, not despawns, kill credit or server prep progress.
internal sealed class TriggerKillLedger(IReadOnlyList<TriggerMobRule> rules)
{
    private readonly Dictionary<ulong, (TriggerNpcLife Life, DateTimeOffset Seen)> previous = new();
    private readonly Dictionary<ushort, int> counts = new();
    private HashSet<ushort> active = [];
    private uint territory;
    public IReadOnlyList<TriggerKillCount> Snapshot => rules.Where(r => r.Territory == territory && !active.Contains(r.FateId) && counts.GetValueOrDefault(r.FateId) > 0)
        .Select(r => new TriggerKillCount(r.FateId, r.FateName, r.MobName, counts[r.FateId])).ToArray();
    public void Reset() { previous.Clear(); counts.Clear(); active.Clear(); territory = 0; }
    public IReadOnlyList<TriggerKillEvent> Observe(uint zone, IReadOnlyList<TriggerNpcLife> npcs, IReadOnlySet<ushort> activeFates, DateTimeOffset now)
    {
        if (zone != territory) { Reset(); territory = zone; }
        var events = new List<TriggerKillEvent>();
        var increments = new Dictionary<ushort, int>();
        var current = npcs.Where(n => n.Id != 0 && n.Id != 0xE0000000 && n.NameId != 0).DistinctBy(n => n.Id).ToArray();
        foreach (var npc in current)
        {
            if (!previous.TryGetValue(npc.Id, out var old) || old.Life.Dead || !npc.Dead
                || old.Life.NameId != npc.NameId || old.Life.Level != npc.Level || now - old.Seen > TimeSpan.FromSeconds(2)) continue;
            foreach (var rule in rules.Where(r => r.Territory == zone && !active.Contains(r.FateId)
                && r.Level == npc.Level && r.NameIds.Contains(npc.NameId)))
            {
                counts[rule.FateId] = counts.GetValueOrDefault(rule.FateId) + 1;
                increments[rule.FateId] = increments.GetValueOrDefault(rule.FateId) + 1;
            }
        }
        foreach (var delta in increments)
        {
            var rule = rules.First(r => r.FateId == delta.Key);
            events.Add(new(now, $"{rule.FateName} · {rule.MobName} +{delta.Value}", $"本轮本地观测累计 {counts[delta.Key]} 只"));
        }
        foreach (var id in activeFates.Where(id => !active.Contains(id)))
        {
            if (counts.Remove(id, out var total) && total > 0)
            {
                var rule = rules.First(r => r.FateId == id);
                events.Add(new(now, $"{rule.FateName} 已触发", $"本轮本地观测 {rule.MobName} 击杀 {total} 只，开始新一轮计数。"));
            }
        }
        previous.Clear();
        foreach (var npc in current) previous[npc.Id] = (npc, now);
        active = activeFates.ToHashSet();
        return events;
    }
}
