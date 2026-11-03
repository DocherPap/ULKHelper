namespace ULKHelper.Fates;

// UI planning only. No combat, movement or kill-count tracking.
internal sealed class TriggerSelection
{
    private readonly List<ushort> ids = [];
    public uint Territory { get; private set; }
    public IReadOnlyList<ushort> Ids => ids;
    public void SetIsland(uint territory)
    {
        if (Territory == territory) return;
        Territory = territory; ids.Clear();
    }
    public bool Add(FateDefinition fate)
    {
        if (fate.Seed.Territory != Territory || fate.Seed.Bunny || ids.Count >= 3 || ids.Contains(fate.Seed.Id)) return false;
        ids.Add(fate.Seed.Id); return true;
    }
    public void Remove(ushort id) => ids.Remove(id);
}
