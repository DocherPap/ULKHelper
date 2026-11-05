using System.Numerics;

namespace ULKHelper.Automation;

internal interface INavigationMesh
{
    bool Ready { get; }
    bool SupportsRouteEstimates => false;
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
    private float nearbyGroundRadius;
    private Vector3 requestedGround;
    private Func<Vector3, bool>? groundFilter;
    private readonly NavigationEndpoint endpoint = new(mesh);
    private bool relaxedArrival;
    private List<Vector3> issued = [];
    private CancellationTokenSource? cancellation;
    private Task<List<Vector3>>? path;
    private MapDestination? arrival;
    public bool Active { get; private set; }
    public string Status { get; private set; } = "尚未开始导航。";
    public Vector3? Target => Active ? target : null;
    public bool OwnsMovement => Active && !mesh.Busy && NavigationRules.OwnsRoute(mesh.Route, issued);

    public MapDestination? TakeArrival() { var value = arrival; arrival = null; return value; }

    public void Start(MapDestination goal, WorldContext world, Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now, bool waitWithoutFate = true, float arrivalRadius = 3, bool unknownHeight = true, bool relaxedArrival = false, float nearbyGroundRadius = 25, Func<Vector3, bool>? groundFilter = null)
    {
        if (Active) { Status = "请先停止当前导航。"; return; }
        if (goal.Territory != world.Territory || goal.Map != world.Map) { Status = "目的地不在当前地图。"; return; }
        if (!NavigationRules.Finite(goal.Position) || !NavigationRules.Finite(player)) { Status = "坐标无效。"; return; }
        try
        {
            if (!mesh.Ready) { Status = "vnavmesh 未加载或当前地图网格未就绪。"; return; }
            if (mesh.Busy || mesh.Route.Count > 0) { Status = "vnavmesh 正被其他任务使用。"; return; }
            destination = goal; unknownDestinationHeight = unknownHeight;
            this.groundFilter = groundFilter;
            this.relaxedArrival = relaxedArrival;
            this.nearbyGroundRadius = Math.Max(nearbyGroundRadius, NavigationGround.SearchRadius);
            this.arrivalRadius = relaxedArrival ? 5 : Math.Clamp(arrivalRadius, .5f, 3);
            context = world;
            started = now;
            refined = false;
            issued = [];
            Active = true;
            var fate = NavigationRules.FindFate(goal.Position, fates);
            target = fate == null ? goal.Position : NavigationRules.StandOff(player, fate.Position, 25);
            arrival = null;
            departAt = fate == null && waitWithoutFate ? now.AddSeconds(2) : now;
            Status = fate != null ? $"目标：{fate.Name} 中心前方 25 米。"
                : waitWithoutFate ? "附近无 FATE，等待 2 秒后出发。" : "准备直接寻路。";
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
                endpoint.TakeDestination(out var result);
                path = null;
                cancellation?.Dispose(); cancellation = null;
                if (mesh.Busy || mesh.Route.Count > 0) { Stop("寻路期间出现其他导航任务，已停止。"); return; }
                if (result.Count == 0 || groundFilter?.Invoke(result[^1]) == false)
                                {
                    if (nearbyGroundRadius > 0 && TryNearbyGround(player)) return;
                    Stop(nearbyGroundRadius > 0 ? "目标附近未找到完整可达路径。" : "未找到到达终点的完整路径。");
                    return;
                }
                issued = result.ToList();
                mesh.Move(result.ToList());
                Status = nearbyGroundRadius > 0 && NavigationRules.Distance(requestedGround, target) > 0.5f
                    ? "正在前往目标附近的可达点。" : "正在前往目的地。";
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
            if (NavigationRules.Distance(player, target) <= arrivalRadius && (relaxedArrival || Math.Abs(player.Y - target.Y) <= 5)
                && groundFilter?.Invoke(player) != false)
            { Finish("已到达目的地。"); return; }
            if (issued.Count > 0 && route.Count == 0)
            {
                if (relaxedArrival) Finish("移动已结束，继续触发。");
                else Stop("移动已结束，尚未到达目的地。");
                return;
            }
            if (issued.Count == 0 && path == null) RequestPath(player, unknownDestinationHeight && destination!.Position == target);
        }
        catch (Exception ex) { Stop("导航失败：" + ex.Message); }
    }

    private void RequestPath(Vector3 player, bool unknownHeight)
    {
        if (mesh.Busy || mesh.Route.Count > 0) { Stop("vnavmesh 正被其他任务使用。"); return; }
        requestedGround = target;
        endpoint.Start(target, unknownHeight, nearbyGroundRadius, groundFilter);
        if (!TryNearbyGround(player)) Stop($"目标及周围 {nearbyGroundRadius:0.#} 米内未找到可行走地面。");
    }

    private bool TryNearbyGround(Vector3 player)
    {
        cancellation?.Dispose(); cancellation = new();
        path = endpoint.Next(player, cancellation.Token);
        if (path == null) return false;
        target = endpoint.Point;
        Status = "正在检查终点的完整可达路径。";
        return true;
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
        endpoint.Reset();
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

internal static class NavigationGround
{
    public const float SearchRadius = 25;
    public static IEnumerable<Vector3> Candidates(INavigationMesh mesh, Vector3 target, bool unknownHeight, float radius = SearchRadius)
    {
        var centre = mesh.Ground(target, unknownHeight);
        if (centre is { } exact && Valid(exact) && NavigationRules.Distance(exact, target) < .1f)
        {
            yield return exact;
        }

        var candidates = new List<Vector3>();
        var yieldedCentre = centre is { } first && Valid(first) && NavigationRules.Distance(first, target) < .1f;
        if (centre is { } initial && Valid(initial) && !yieldedCentre) candidates.Add(initial);
        foreach (var sample in NavigationRules.NearbyGroundSamples(target, radius).Skip(1))
            if (mesh.Ground(sample, unknownHeight) is { } point && Valid(point)
                && (!yieldedCentre || Vector3.DistanceSquared(point, centre!.Value) >= .25f)
                && !candidates.Any(p => Vector3.DistanceSquared(p, point) < .25f)) candidates.Add(point);
        foreach (var point in candidates.OrderBy(p => NavigationRules.Distance(p, target))
            .ThenBy(p => Math.Abs(p.Y - target.Y))) yield return point;

        bool Valid(Vector3 point) => NavigationRules.Finite(point)
            && NavigationRules.Distance(point, target) <= radius + .001f
            && (unknownHeight || Math.Abs(point.Y - target.Y) <= 8);
    }
}

// Poll the returned task on the framework thread. This helper never starts movement.
// TakeDestination returns a coordinate only after a complete path has been confirmed.
internal sealed class NavigationEndpoint(INavigationMesh mesh)
{
    private IEnumerator<Vector3>? candidates;
    private Task<List<Vector3>>? pending;
    public Vector3 Point { get; private set; }
    public void Start(Vector3 destination, bool unknownHeight, float radius = NavigationGround.SearchRadius, Func<Vector3, bool>? filter = null)
    {
        Reset();
        candidates = NavigationGround.Candidates(mesh, destination, unknownHeight, Math.Max(radius, NavigationGround.SearchRadius))
            .Where(p => filter?.Invoke(p) != false).GetEnumerator();
    }
    public Task<List<Vector3>>? Next(Vector3 origin, CancellationToken token)
    {
        if (candidates?.MoveNext() != true) return null;
        Point = candidates.Current;
        return pending = mesh.FindPath(origin, Point, token);
    }
    public Vector3? TakeDestination(out List<Vector3> route)
    {
        var result = pending!.GetAwaiter().GetResult();
        pending = null;
        var valid = result.Count > 0 && result.All(NavigationRules.Finite) && Vector3.Distance(result[^1], Point) <= 3;
        route = valid ? result : [];
        return valid ? Point : null;
    }
    public void Reset() { candidates?.Dispose(); candidates = null; pending = null; }
}
