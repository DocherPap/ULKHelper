using System.Numerics;

namespace ULKHelper.Automation;

internal interface INavigationMesh
{
    bool Ready { get; }
    bool Busy { get; }
    IReadOnlyList<Vector3> Route { get; }
    Vector3? Ground(Vector3 position, bool unknownHeight);
    Task<List<Vector3>> FindPath(Vector3 from, Vector3 to, CancellationToken cancellation);
    void Move(List<Vector3> route);
    void Stop();
}

internal sealed class DestinationNavigationHelper(INavigationMesh mesh) : IDisposable
{
    private MapDestination? destination;
    private WorldContext context;
    private DateTimeOffset started, departAt;
    private bool refined, unknownDestinationHeight = true;
    private Vector3 target;
    private float arrivalRadius = 3;
    private List<Vector3> issued = [];
    private CancellationTokenSource? cancellation;
    private Task<List<Vector3>>? path;
    private MapDestination? arrival;
    public bool Active { get; private set; }
    public string Status { get; private set; } = "尚未开始导航。";
    public Vector3? Target => Active ? target : null;

    public MapDestination? TakeArrival() { var value = arrival; arrival = null; return value; }

    public void Start(MapDestination goal, WorldContext world, Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now, bool waitWithoutFate = true, float arrivalRadius = 3, bool unknownHeight = true)
    {
        if (Active) { Status = "请先停止当前导航。"; return; }
        if (goal.Territory != world.Territory || goal.Map != world.Map) { Status = "目的地不在当前地图。"; return; }
        if (!NavigationRules.Finite(goal.Position) || !NavigationRules.Finite(player)) { Status = "坐标无效。"; return; }
        try
        {
            if (!mesh.Ready) { Status = "vnavmesh 未加载或当前地图网格未就绪。"; return; }
            if (mesh.Busy || mesh.Route.Count > 0) { Status = "vnavmesh 正被其他任务使用。"; return; }
            destination = goal; unknownDestinationHeight = unknownHeight;
            this.arrivalRadius = Math.Clamp(arrivalRadius, .5f, 3);
            context = world;
            started = now;
            refined = false;
            issued = [];
            Active = true;
            var fate = NavigationRules.FindFate(goal.Position, fates);
            target = fate == null ? goal.Position : NavigationRules.StandOff(player, fate.Position, 25);
            arrival = null;
            departAt = fate == null && waitWithoutFate ? now.AddSeconds(2) : now;
            Status = fate == null ? "附近无 FATE，等待 2 秒后出发。" : $"目标：{fate.Name} 中心前方 25 米。";
            if (fate != null) RequestPath(player, false);
        }
        catch (Exception ex) { Stop("无法开始导航：" + ex.Message); }
    }

    public void Tick(WorldContext world, Vector3 player, bool canMove, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (world != context || !canMove) { Stop("角色状态或地图变化，导航已停止。"); return; }
            if (now - started >= TimeSpan.FromMinutes(5)) { Stop("导航超过 5 分钟，已停止。"); return; }
            if (!mesh.Ready) { Stop("导航网格失效，已停止。"); return; }
            var route = mesh.Route;
            if (issued.Count > 0 && route.Count > 0 && !NavigationRules.OwnsRoute(route, issued))
            { Stop("检测到其他导航路线，已释放控制。"); return; }
            if (issued.Count > 0 && mesh.Busy)
            { Stop("检测到其他寻路请求，已停止本次导航。"); return; }
            if (path != null)
            {
                if (!path.IsCompleted) return;
                var result = path.GetAwaiter().GetResult();
                path = null;
                cancellation?.Dispose(); cancellation = null;
                if (mesh.Busy || mesh.Route.Count > 0) { Stop("寻路期间出现其他导航任务，已停止。"); return; }
                if (result.Count == 0 || result.Any(p => !NavigationRules.Finite(p)) || Vector3.Distance(result[^1], target) > 5)
                { Stop("未找到到达终点的完整路径。"); return; }
                issued = result.ToList();
                mesh.Move(result.ToList());
                Status = "正在前往目的地。";
                route = mesh.Route;
            }
            if (now < departAt) return;
            if (!refined && NavigationRules.Distance(player, target) <= 50)
            {
                refined = true;
                var fate = NavigationRules.FindFate(destination!.Position, fates);
                if (fate != null)
                {
                    StopOwnRoute();
                    if (NavigationRules.Distance(player, fate.Position) <= 30)
                    { Finish("已在 FATE 中心 30 米内。"); return; }
                    target = NavigationRules.StandOff(player, fate.Position, 30);
                    Status = $"接近目的地，改为 {fate.Name} 中心前方 30 米。";
                    RequestPath(player, false);
                    return;
                }
            }
            if (NavigationRules.Distance(player, target) <= arrivalRadius && Math.Abs(player.Y - target.Y) <= 5)
            { Finish("已到达目的地。"); return; }
            if (issued.Count > 0 && route.Count == 0) { Stop("移动已结束，尚未到达目的地。"); return; }
            if (issued.Count == 0 && path == null) RequestPath(player, unknownDestinationHeight && destination!.Position == target);
        }
        catch (Exception ex) { Stop("导航失败：" + ex.Message); }
    }

    private void RequestPath(Vector3 player, bool unknownHeight)
    {
        if (mesh.Busy || mesh.Route.Count > 0) { Stop("vnavmesh 正被其他任务使用。"); return; }
        var grounded = mesh.Ground(target, unknownHeight);
        if (grounded == null || !NavigationRules.Finite(grounded.Value) || NavigationRules.Distance(target, grounded.Value) > 5)
        { Stop($"无法在目的地找到可行走地面：目标={target}，查询结果={grounded}，未知高度={unknownHeight}。"); return; }
        target = grounded.Value;
        cancellation = new();
        path = mesh.FindPath(player, target, cancellation.Token);
        Status = "正在计算路径。";
    }

    private void StopOwnRoute()
    {
        var route = mesh.Route;
        if (NavigationRules.OwnsRoute(route, issued)) mesh.Stop();
        issued = [];
    }

    public void Stop(string reason = "已手动停止导航。")
    {
        arrival = null;
        Active = false;
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        if (path != null)
            _ = path.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        path = null;
        try { StopOwnRoute(); }
        catch { issued = []; } // The optional IPC provider can disappear during unload.
        Status = reason;
    }

    private void Finish(string reason)
    {
        var goal = destination;
        Stop(reason);
        arrival = goal;
    }

    public void Dispose() => Stop("导航模块已卸载。");
}
