using System.Numerics;
using ULKHelper.Automation;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
var now = DateTimeOffset.UtcNow;
var world = new WorldContext(1, 2, 0, 42);
var player = Vector3.Zero;
var goal = new MapDestination(1, 2, new(200, 0, 0), "测试坐标");
var fate = new FateEntry(7, "测试 FATE", new(210, 0, 0));
Check(NavigationRules.IsEurekaNm(1, 1) && !NavigationRules.IsEurekaNm(1, 4) && !NavigationRules.IsEurekaNm(2, 1), "仅识别优雷卡NM，排除幸福兔与其他区域事件");
Check(NavigationRules.Overlaps(player, new(25, 100, 0)), "25米边界按水平世界距离判断");
Check(!NavigationRules.Overlaps(player, new(25.01f, 0, 0)), "超过25米接受坐标");
Check(NavigationRules.FindFate(goal.Position, [fate, new(8, "far", new(251, 0, 0))]) == fate, "选取50米内最近FATE");
Check(NavigationRules.FindFate(goal.Position, [new(8, "edge", new(250, 0, 0))]) != null, "50米FATE边界包含");
Check(NavigationRules.StandOff(player, fate.Position, 25) == new Vector3(185, 0, 0), "初始终点位于近侧25米处");
Check(NavigationRules.StandOff(player, fate.Position, 30) == new Vector3(180, 0, 0), "接近终点位于近侧30米处");
Check(NavigationRules.Finite(NavigationRules.StandOff(player, player, 30)), "中心重合不产生NaN");

var listener = new LeaderListenerHelper();
var leader = new PlayerIdentity("车头", 100);
listener.Select(leader); listener.SetListening(true);
var marks = 0;
bool Receive(PlayerIdentity sender, MapDestination? link) => listener.Receive(sender, "Shout", "test", link, 1, 2, player, now, _ => { marks++; return true; });
Check(!Receive(new("车头", 101), goal) && marks == 0, "同名跨服玩家不会当成车头");
Receive(leader, goal with { Position = new(25, 0, 0) });
Check(marks == 0 && listener.Destination == null, "近距离坐标不设置目的地");
Receive(leader, goal with { Map = 3 });
Check(marks == 0, "其他地图坐标不标记");
Receive(leader, goal);
Check(marks == 1 && listener.Destination == goal, "车头有效坐标标记并保留");
for (var i = 0; i < 60; i++) Receive(leader, null);
Check(listener.History.Count == 50, "消息缓冲上限50条");
listener.SetListening(false); Check(!Receive(leader, goal), "停止监听后忽略消息");
listener.Reset(); Check(listener.Leader == null && listener.Destination == null, "切区清空车头与目的地");

var mesh = new FakeMesh();
using var nav = new DestinationNavigationHelper(mesh);
nav.Start(goal, world, player, [], now);
nav.Tick(world, player, true, [], now.AddSeconds(1.99));
Check(mesh.PathRequests == 0, "无FATE等待满2秒");
nav.Tick(world, player, true, [], now.AddSeconds(2));
Check(mesh.PathRequests == 1 && mesh.Moves == 0, "2秒后异步寻路");
mesh.Complete(); nav.Tick(world, player, true, [], now.AddSeconds(2.1));
Check(mesh.Moves == 1, "寻路完成才开始移动");
nav.Stop(); Check(!nav.Active && mesh.Stops == 1, "手动停止自己的移动");

mesh = new(); using var nmNav = new DestinationNavigationHelper(mesh);
nmNav.Start(goal, world, player, [fate], now);
Check(mesh.PathRequests == 1 && nmNav.Target == new Vector3(185, 0, 0), "有FATE立即计算25米路径");
mesh.Complete(); nmNav.Tick(world, player, true, [fate], now.AddMilliseconds(100));
nmNav.Tick(world, new(140, 0, 0), true, [fate], now.AddSeconds(1));
Check(mesh.PathRequests == 2 && nmNav.Target == new Vector3(180, 0, 0) && mesh.Stops == 1, "接近50米时重设30米终点");
mesh.Complete(); nmNav.Tick(world, new(140, 0, 0), true, [fate], now.AddSeconds(1.1));
nmNav.Tick(world, new(150, 0, 0), true, [fate], now.AddSeconds(1.2));
Check(mesh.PathRequests == 2, "二次终点只重算一次");
nmNav.Tick(world, new(180, 0, 0), true, [fate], now.AddSeconds(2));
Check(!nmNav.Active && mesh.Stops == 2, "到达30米终点停止");

mesh = new(); using var cancelNav = new DestinationNavigationHelper(mesh);
cancelNav.Start(goal, world, player, [fate], now); cancelNav.Stop(); mesh.Complete();
cancelNav.Tick(world, player, true, [fate], now.AddSeconds(1));
Check(mesh.Token.IsCancellationRequested && mesh.Moves == 0, "停止后异步结果不会重新启动移动");
mesh = new(); using var zoneNav = new DestinationNavigationHelper(mesh);
zoneNav.Start(goal, world, player, [], now); zoneNav.Tick(world with { Instance = 1 }, player, true, [], now.AddSeconds(1));
Check(!zoneNav.Active && mesh.PathRequests == 0, "实例变化取消等待");
mesh = new(); using var invalidNav = new DestinationNavigationHelper(mesh);
invalidNav.Start(goal with { Map = 99 }, world, player, [], now);
Check(!invalidNav.Active, "拒绝跨地图导航");
mesh = new(); using var timeoutNav = new DestinationNavigationHelper(mesh);
timeoutNav.Start(goal, world, player, [], now); timeoutNav.Tick(world, player, true, [], now.AddMinutes(5));
Check(!timeoutNav.Active, "5分钟导航超时");
mesh = new(); using var otherNav = new DestinationNavigationHelper(mesh);
otherNav.Start(goal, world, player, [fate], now); mesh.Complete(); otherNav.Tick(world, player, true, [fate], now);
mesh.CurrentRoute = [new(300, 0, 0)]; otherNav.Stop();
Check(mesh.Stops == 0 && mesh.CurrentRoute.Count == 1, "不停止已被其他任务替换的路线");
mesh = new(); using var failedNav = new DestinationNavigationHelper(mesh);
failedNav.Start(goal, world, player, [fate], now); mesh.Pending!.SetResult([]); failedNav.Tick(world, player, true, [fate], now);
Check(!failedNav.Active && mesh.Moves == 0, "空路径不会开始移动");
mesh = new(); using var faultNav = new DestinationNavigationHelper(mesh);
faultNav.Start(goal, world, player, [fate], now); mesh.Pending!.SetException(new InvalidOperationException("test"));
faultNav.Tick(world, player, true, [fate], now);
Check(!faultNav.Active && mesh.Moves == 0, "寻路任务异常不会开始移动");
mesh = new(); using var partialNav = new DestinationNavigationHelper(mesh);
partialNav.Start(goal, world, player, [fate], now); mesh.Pending!.SetResult([new(185, 20, 0)]);
partialNav.Tick(world, player, true, [fate], now);
Check(!partialNav.Active && mesh.Moves == 0, "错误高度的不完整路径被拒绝");
mesh = new() { IsBusy = true }; using var busyNav = new DestinationNavigationHelper(mesh);
busyNav.Start(goal, world, player, [fate], now);
Check(!busyNav.Active && mesh.PathRequests == 0, "已有其他寻路时不抢占");
mesh = new(); using var stateNav = new DestinationNavigationHelper(mesh);
stateNav.Start(goal, world, player, [fate], now); mesh.Complete(); stateNav.Tick(world, player, true, [fate], now);
stateNav.Tick(world, player, false, [fate], now.AddSeconds(1));
Check(!stateNav.Active && mesh.Stops == 1, "玩家状态不允许移动时停止");
Check(NavigationRules.OwnsRoute([new(2, 0, 0)], [new(1, 0, 0), new(2, 0, 0)]), "路线前端消耗后仍能识别归属");
Console.WriteLine($"{count} automation checks passed.");

sealed class FakeMesh : INavigationMesh
{
    public bool Ready => true;
    public bool IsBusy;
    public bool Busy => IsBusy;
    public List<Vector3> CurrentRoute = [];
    public IReadOnlyList<Vector3> Route => CurrentRoute;
    public int Moves, Stops, PathRequests;
    public TaskCompletionSource<List<Vector3>>? Pending;
    public CancellationToken Token;
    private Vector3 target;
    public Vector3? Ground(Vector3 point, bool unknownHeight) => point;
    public Task<List<Vector3>> FindPath(Vector3 from, Vector3 to, CancellationToken token)
    { PathRequests++; Token = token; target = to; Pending = new(); return Pending.Task; }
    public void Complete() => Pending!.SetResult([target]);
    public void Move(List<Vector3> route) { CurrentRoute = route; Moves++; }
    public void Stop() { CurrentRoute = []; Stops++; }
}
