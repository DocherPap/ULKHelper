namespace ULKHelper.Automation;

internal enum SoloDebugAction { Inspect, Mount, Dismount, CrystalTeleport }
internal sealed record DebugCrystal(uint PlaceNameId, string Name);
internal sealed record DebugTestLine(DateTimeOffset Time, string Message);
internal sealed record SoloDebugSnapshot(bool Active, string Status, string Diagnostics,
    IReadOnlyList<DebugCrystal> Crystals, IReadOnlyList<DebugTestLine> History)
{
    public static SoloDebugSnapshot Empty => new(false, "尚未测试。", "点击读取现场状态，不会移动或开循环。", [], []);
}
internal sealed record SoloDebugFrame(WorldContext World, System.Numerics.Vector3 Position, bool Safe, bool Loading,
    bool Mounted, bool Casting, bool InCombat);

// Single bounded action, driven on framework thread; separate menu ownership from navigation.
internal sealed class SoloDebugHelper(IIslandCrystalActions crystals, IArrivalActions mounts)
{
    private enum Phase { Mount, Dismount, CrystalDismount, Menu, Transfer }
    private Phase phase;
    private WorldContext world;
    private IslandCrystal? source, target;
    private DateTimeOffset began, phaseAt;
    private bool requested, opened;
    private readonly Queue<DebugTestLine> history = new();
    public bool Active { get; private set; }
    public bool Transferring => Active && phase == Phase.Transfer;
    public bool Interacting => Active && phase is Phase.Menu or Phase.CrystalDismount;
    public string Status { get; private set; } = "尚未测试。";
    public IReadOnlyList<DebugTestLine> History => history.Reverse().ToArray();
    public void Record(string text, DateTimeOffset now)
    {
        Status = text; history.Enqueue(new(now, text)); while (history.Count > 40) history.Dequeue();
    }
    public void Start(SoloDebugAction action, SoloDebugFrame frame, IslandCrystal? destination, DateTimeOffset now)
    {
        if (Active) { Record("请先停止当前单次测试。", now); return; }
        if (!frame.Safe || frame.InCombat || frame.Casting) { Record("测试未开始：请脱战、停止施法并关闭其他交互界面。", now); return; }
        if (action == SoloDebugAction.Inspect) return;
        world = frame.World; began = phaseAt = now; requested = opened = false;
        if (action == SoloDebugAction.CrystalTeleport)
        {
            source = IslandCrystalRoutes.Nearest(world.Territory, frame.Position); target = destination;
            if (source == null || target == null || target.Territory != world.Territory || target == source)
            { Record("测试未开始：请选择本岛另一座已解锁水晶。", now); return; }
            if (NavigationRules.Distance(frame.Position, source.Position) > 5)
            { Record("测试未开始：请手动走到源水晶5米内，再点击。不自动寻路。", now); return; }
            phase = Phase.CrystalDismount;
            Record($"开始单次水晶测试：{source.PlaceNameId} → {target.PlaceNameId}。", now);
        }
        else
        {
            phase = action == SoloDebugAction.Mount ? Phase.Mount : Phase.Dismount;
            Record(action == SoloDebugAction.Mount ? "开始单次上坐骑测试。" : "开始单次下坐骑测试。", now);
        }
        Active = true;
    }
    public void Tick(SoloDebugFrame frame, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (frame.World != world || frame.InCombat || !frame.Safe && !(Transferring && frame.Loading))
            { Stop("角色/地图/战斗状态变化，测试停止。", now); return; }
            if (now - began >= TimeSpan.FromSeconds(60)) { Stop("测试超过60秒，已停止。", now); return; }
            if (phase == Phase.Transfer)
            {
                if (now - phaseAt >= TimeSpan.FromSeconds(45)) { Stop("失败：45秒内未确认目标水晶落地。", now); return; }
                if (!frame.Loading && !frame.Casting && now - phaseAt >= TimeSpan.FromSeconds(1)
                    && NavigationRules.Distance(frame.Position, target!.Position) <= 15)
                    Stop("通过：已在目标水晶15米内落地，没有继续移动。", now);
                return;
            }
            if (frame.Casting) return;
            if (phase is Phase.Mount or Phase.Dismount or Phase.CrystalDismount)
            {
                var wantsMounted = phase == Phase.Mount;
                if (frame.Mounted == wantsMounted)
                {
                    if (phase != Phase.CrystalDismount) { Stop(wantsMounted ? "通过：已确认在坐骑上。" : "通过：已确认下坐骑。", now); return; }
                    phase = Phase.Menu; phaseAt = now;
                }
                else
                {
                    if (requested)
                    { if (now - phaseAt >= TimeSpan.FromSeconds(12)) Stop("失败：请求后12秒未确认坐骑状态。", now); return; }
                    requested = true;
                    var accepted = wantsMounted ? mounts.Mount() : mounts.Dismount();
                    Record(accepted ? "请求已提交，等待实际坐骑状态确认。" : "请求未被接受，将等待状态确认后超时。", now);
                    return;
                }
            }
            if (!opened)
            {
                opened = true;
                if (!crystals.Open(source!)) { Stop("失败：" + (crystals.Error ?? "无法与源水晶交互。"), now); return; }
                Record("已交互源水晶，等待可用菜单。", now);
            }
            var choices = crystals.Choices();
            if (choices == null)
            { if (now - phaseAt >= TimeSpan.FromSeconds(15)) Stop("失败：15秒内没有取得本插件打开的水晶菜单。", now); return; }
            Record("菜单可用目的地ID：" + string.Join(", ", choices), now);
            if (!choices.Contains(target!.PlaceNameId)) { Stop("未传送：所选目标不在可用菜单，可能未解锁。", now); return; }
            if (!crystals.Select(target)) { Stop("失败：未能选择目标菜单条目。", now); return; }
            phase = Phase.Transfer; phaseAt = now;
            Record("已选择目标水晶，等待落地；不会自动回程。", now);
        }
        catch (Exception ex) { Stop("测试异常：" + ex.Message, now); }
    }
    public void Stop(string reason, DateTimeOffset now)
    {
        Active = false;
        try { crystals.Cancel(); } catch { }
        Record(reason, now);
    }
}
