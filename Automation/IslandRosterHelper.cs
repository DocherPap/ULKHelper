namespace ULKHelper.Automation;

internal sealed record IslandMember(PlayerIdentity Identity, string WorldName, uint EntityId);
internal sealed record IslandRosterSnapshot(IReadOnlyList<IslandMember> Members, DateTimeOffset? UpdatedAt, string Status);
internal interface IIslandRosterSource
{
    bool Request();
    IReadOnlyList<IslandMember>? TakeCompleted(uint territory);
    void Reset() { }
}

internal sealed class IslandRosterHelper(IIslandRosterSource source)
{
    private DateTimeOffset nextRequest, requestedAt;
    private bool pending, manual;
    public IslandRosterSnapshot Snapshot { get; private set; } = new([], null, "岛内玩家名单尚未获取。");
    public void Refresh() => manual = true;
    public void Reset()
    {
        source.Reset();
        pending = manual = false;
        nextRequest = default;
        Snapshot = new([], null, "岛内玩家名单尚未获取。");
    }
    public void Tick(uint territory, bool safe, DateTimeOffset now)
    {
        if (!safe || territory is not (732 or 763 or 795 or 827)) return;
        IReadOnlyList<IslandMember>? members;
        try { members = source.TakeCompleted(territory); }
        catch { pending = false; Snapshot = Snapshot with { Status = "名单读取失败，保留上次缓存。" }; return; }
        if (members != null)
        {
            if (!pending) nextRequest = now.AddSeconds(30);
            pending = false;
            Snapshot = new(members.DistinctBy(p => p.Identity).ToArray(), now, "岛内玩家名单已更新。");
        }
        else if (pending && now - requestedAt >= TimeSpan.FromSeconds(15))
        { pending = false; Snapshot = Snapshot with { Status = "名单请求超时，保留上次缓存。" }; }
        if (pending || now < nextRequest && !(manual && now - requestedAt >= TimeSpan.FromSeconds(5))) return;
        manual = false; requestedAt = now; nextRequest = now.AddSeconds(30);
        try { pending = source.Request(); }
        catch { pending = false; }
        Snapshot = Snapshot with { Status = pending ? "正在获取岛内玩家名单。" : "名单请求未接受，保留上次缓存。" };
    }
    public int? OtherPlayers(PlayerIdentity self, DateTimeOffset now) => Snapshot.UpdatedAt is { } updated
        && now - updated <= TimeSpan.FromSeconds(90) ? Snapshot.Members.Count(p => p.Identity != self) : null;
    public IReadOnlyList<PlayerEntry> Merge(IReadOnlyList<PlayerEntry> loaded, IReadOnlyList<IslandMember>? social = null)
    {
        var positions = loaded.ToDictionary(p => p.Identity);
        var members = social is { Count: > 0 } ? social : Snapshot.Members;
        return members.Select(p => positions.GetValueOrDefault(p.Identity)
            ?? new PlayerEntry(p.Identity, p.WorldName, default, float.PositiveInfinity, false))
            .Concat(loaded).DistinctBy(p => p.Identity).OrderBy(p => p.Distance).ThenBy(p => p.Identity.Name).ToArray();
    }
}
