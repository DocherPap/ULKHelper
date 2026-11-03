using System.Text.Json;
using System.Text.Json.Serialization;

namespace ULKHelper.Inventory;

internal sealed class SourceSnapshot
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public Dictionary<uint, int> Items { get; set; } = new();
    [JsonIgnore] public bool ReadThisSession { get; set; }
}

internal sealed class InventoryProfile
{
    public int Version { get; set; } = 1;
    public ulong ContentId { get; set; }
    public Dictionary<string, SourceSnapshot> Sources { get; set; } = new();
}

internal sealed class OwnershipLedger
{
    internal static readonly (string Key, string Label)[] BaseSources =
    [
        ("bags", "背包"), ("equipped", "穿戴装备"), ("armoury", "兵装库"),
        ("saddle", "陆行鸟背包"), ("premium-saddle", "高级陆行鸟背包"),
        ("cabinet", "收藏柜"), ("dresser", "幻化柜"),
    ];
    public InventoryProfile Profile { get; private set; } = new();
    public long Revision { get; private set; }
    public bool Dirty { get; private set; }
    public bool HasData => Profile.ContentId != 0 && Profile.Sources.Values.Any(s => s.UpdatedAt != null);
    public IReadOnlyList<SourceSnapshot> Sources => Profile.Sources.Values.ToArray();
    public Dictionary<uint, int> Counts { get; private set; } = new();

    public OwnershipLedger() => Activate(0);

    public void Activate(ulong contentId, InventoryProfile? saved = null)
    {
        if (saved != null && (saved.Version != 1 || saved.ContentId != contentId))
            throw new InvalidDataException("库存缓存的角色或版本不匹配。");
        Profile = saved ?? new InventoryProfile { ContentId = contentId };
        foreach (var source in Profile.Sources.Values) source.ReadThisSession = false;
        foreach (var (key, label) in BaseSources) EnsureSource(key, label);
        Recount();
        Dirty = false;
        Revision++;
    }

    public void EnsureSource(string key, string label)
    {
        if (Profile.Sources.TryGetValue(key, out var source))
        {
            if (source.Label == label) return;
            source.Label = label;
        }
        else Profile.Sources[key] = new SourceSnapshot { Key = key, Label = label };
        Revision++;
        Dirty = true;
    }

    public void Replace(string key, string label, Dictionary<uint, int> items, bool touch = false)
    {
        if (Profile.ContentId == 0) return;
        EnsureSource(key, label);
        var source = Profile.Sources[key];
        if (source.ReadThisSession && !touch && Equal(source.Items, items)) return;
        source.Items = new(items);
        source.UpdatedAt = DateTimeOffset.UtcNow;
        source.ReadThisSession = true;
        Recount();
        Dirty = true;
        Revision++;
    }

    public void RemoveRetainersExcept(IReadOnlySet<string> activePrefixes)
    {
        var removed = Profile.Sources.Keys.Where(k => k.StartsWith("retainer:", StringComparison.Ordinal) &&
            !activePrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToArray();
        foreach (var key in removed) Profile.Sources.Remove(key);
        if (removed.Length == 0) return;
        Recount();
        Dirty = true;
        Revision++;
    }

    private void Recount()
    {
        Counts = new();
        foreach (var source in Profile.Sources.Values.Where(s => s.UpdatedAt != null))
            foreach (var (id, quantity) in source.Items)
                if (id > 0 && quantity > 0) Counts[id] = checked(Counts.GetValueOrDefault(id) + quantity);
    }

    public void MarkSaved() => Dirty = false;
    private static bool Equal(Dictionary<uint, int> a, Dictionary<uint, int> b)
        => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);

    public static InventoryProfile? Read(string path, ulong contentId)
    {
        if (!File.Exists(path)) return null;
        var saved = JsonSerializer.Deserialize<InventoryProfile>(File.ReadAllText(path))
            ?? throw new InvalidDataException("库存缓存内容为空。");
        if (saved.Version != 1 || saved.ContentId != contentId || saved.Sources == null ||
            saved.Sources.Any(p => p.Value == null || p.Value.Items == null || p.Key != p.Value.Key || p.Value.Items.Any(i => i.Key == 0 || i.Value < 0)))
            throw new InvalidDataException("库存缓存格式或角色不匹配。");
        try
        {
            foreach (var group in saved.Sources.Values.Where(s => s.UpdatedAt != null).SelectMany(s => s.Items).GroupBy(p => p.Key))
                _ = group.Sum(p => p.Value);
        }
        catch (OverflowException ex) { throw new InvalidDataException("库存缓存数量超出有效范围。", ex); }
        return saved;
    }

    public void Save(string path)
    {
        if (!Dirty || Profile.ContentId == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Profile));
        File.Move(temporary, path, true);
        MarkSaved();
    }
}
