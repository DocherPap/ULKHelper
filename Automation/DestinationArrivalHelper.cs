using System.Numerics;

namespace ULKHelper.Automation;

// Explicit Start/Tick/Stop; no UI or combat skill execution. All calls on framework thread.
internal sealed class DestinationArrivalHelper(INavigationMesh mesh, IArrivalActions actions) : IDisposable
{
    private readonly DestinationNavigationHelper movement = new(mesh);
    private MapDestination? destination;
    private WorldContext context;
    private DateTimeOffset started, nextAction, nextMove;
    private (ushort Id, int Started)? identity;
    private bool pulled;
    private bool? previousSync;
    private int attempts;
    public bool Active { get; private set; }
    public string Status { get; private set; } = "尚未开始到达处理。";
    public MapDestination? Destination => destination;

    public void Start(MapDestination goal, WorldContext world, DateTimeOffset now)
    {
        Stop();
        if (goal.Territory != world.Territory || goal.Map != world.Map || !NavigationRules.Finite(goal.Position))
        { Status = "到达目的地范围无效。"; return; }
        destination = goal; context = world; started = now; nextAction = nextMove = now;
        identity = null; previousSync = null; pulled = false; attempts = 0; Active = true;
        Status = "等待目标 NM 开怪。";
    }

    public void Tick(ArrivalFrame frame, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (frame.World != context || !frame.Safe || !NavigationRules.Finite(frame.Player))
            { Stop("角色状态或地图变化，到达处理已停止。"); return; }
            if (now - started >= TimeSpan.FromMinutes(5)) { Stop("到达处理超过5分钟，已停止。"); return; }
            var fate = frame.Fate;
            if (fate == null)
            {
                if (identity != null) Stop("目标 NM 已结束或不再可见。");
                else Status = "等待目的地附近 NM 出现。";
                return;
            }
            if (NavigationRules.Distance(destination!.Position, fate.Centre) > 50 || !NavigationRules.Finite(fate.Centre))
            { Stop("目标 NM 不在目的地范围内。"); return; }
            var current = (fate.Id, fate.StartedAt);
            if (identity != null && identity.Value != current) { Stop("目标 NM 场次变化，已停止。"); return; }
            identity = current;
            pulled |= ArrivalRules.IsPulled(fate).Started;
            if (!pulled) { Status = "等待目标 NM 开怪。"; return; }
            if (fate.NeedsSync == null) { movement.Stop(); Status = "无法确认元素等级或同步状态，等待更新。"; return; }
            var needsSync = fate.NeedsSync.Value;
            if (previousSync != needsSync)
            { movement.Stop(); previousSync = needsSync; attempts = 0; nextAction = now; nextMove = now; }
            if (!movement.Active && (mesh.Busy || mesh.Route.Count > 0))
            { Stop("检测到其他导航任务，到达处理已停止。"); return; }

            // Never move while a mount cast is in progress; confirm state before approaching.
            if (frame.Casting) { movement.Stop(); Status = "等待当前施法结束。"; return; }
            if (needsSync && frame.Mounted)
            {
                movement.Stop(); TryAction(actions.Dismount, "下坐骑", now); return;
            }
            if (!needsSync && !frame.Mounted && !frame.InCombat && attempts < 3)
            {
                movement.Stop(); TryAction(actions.Mount, "上坐骑", now); return;
            }
            if (!needsSync && !frame.Mounted && !frame.InCombat && now < nextAction) return;
            var radius = needsSync ? 15 : 25;
            if (movement.Active)
            {
                movement.Tick(frame.World, frame.Player, true, [], now);
                if (!movement.Active && movement.TakeArrival() == null) { Stop(movement.Status); return; }
                Status = movement.Active ? movement.Status : $"保持距 NM 中心 {radius} 米。";
                return;
            }
            // A small tolerance prevents continuous path replanning around the radius.
            if (Math.Abs(NavigationRules.Distance(frame.Player, fate.Centre) - radius) <= 2
                && Math.Abs(frame.Player.Y - fate.Centre.Y) <= 5)
            { Status = $"保持距 NM 中心 {radius} 米。"; return; }
            if (now < nextMove) return;
            nextMove = now.AddSeconds(2);
            var target = ArrivalRules.Position(frame.Player, fate.Centre, radius);
            movement.Start(destination with { Position = target }, context, frame.Player, [], now, false);
            if (!movement.Active) Stop(movement.Status);
            else Status = $"前往 NM 中心 {radius} 米处。";
        }
        catch (Exception ex) { Stop("到达处理失败：" + ex.Message); }
    }

    private void TryAction(Func<bool> action, string label, DateTimeOffset now)
    {
        if (now < nextAction) return;
        if (attempts >= 3) { Stop(label + "失败，已停止到达处理。"); return; }
        attempts++; nextAction = now.AddSeconds(3);
        Status = action() ? $"已尝试{label}，等待状态确认。" : $"暂时无法{label}。";
    }

    public void Stop(string reason = "到达处理已停止。")
    { Active = false; movement.Stop(); destination = null; Status = reason; }
    public void Dispose() { Stop(); movement.Dispose(); }
}
