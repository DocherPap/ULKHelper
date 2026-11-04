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
Check(nmNav.TakeArrival() == goal && nmNav.TakeArrival() == null, "到达信号只消费一次，包含原始目的地");
Check(cancelNav.TakeArrival() == null && failedNav.TakeArrival() == null, "取消和失败不触发到达处理");
var crowd = Enumerable.Range(1, 8).Select(i => new ArrivalParticipant((ulong)i, fate.Position, true, i <= 2, false)).ToArray();
Check(ArrivalRules.NeedsSync(5, 4, false) == true, "元素等级大于FATE上限需要同步");
Check(ArrivalRules.NeedsSync(4, 4, false) == false && ArrivalRules.NeedsSync(3, 4, false) == false, "等于或低于上限不需要同步");
Check(ArrivalRules.NeedsSync(null, 4, false) == null && ArrivalRules.NeedsSync(0, 4, false) == null, "未知等级不使用职业等级代替");
Check(ArrivalRules.NeedsSync(4, 4, true) == true, "当前已同步该FATE仍保持15米策略");
var arrivalFate = new ArrivalFate(7, 100, fate.Position, 30, false, 1, crowd);
Check(!ArrivalRules.IsPulled(arrivalFate).Started, "仅有人在场不算开怪");
Check(!ArrivalRules.IsPulled(arrivalFate with { Participants = crowd.Select((p, i) => p with { Attacking = i == 0 }).ToArray() }).Started, "一人攻击不足八人四分之一");
Check(ArrivalRules.IsPulled(arrivalFate with { Participants = crowd.Select((p, i) => p with { Attacking = i < 2 }).ToArray() }).Started, "两人攻击达到八人四分之一");
Check(!ArrivalRules.IsPulled(arrivalFate with { Participants = [] }).Started, "零人不会以零分母判为开怪");
Check(!ArrivalRules.IsPulled(arrivalFate with { HpRatio = .8f }).Started, "血量恰好80%不算低于80%");
Check(ArrivalRules.IsPulled(arrivalFate with { HpRatio = .799f }).Started, "血量低于80%独立触发");
Check(!ArrivalRules.IsPulled(arrivalFate with { HpRatio = null }).Started, "未读到boss血量保持未知");
var largeCrowd = Enumerable.Range(1, 20).Select(i => new ArrivalParticipant((ulong)i, fate.Position, true, i <= 2, i <= 2)).ToArray();
Check(ArrivalRules.IsPulled(arrivalFate with { Participants = largeCrowd }).Started, "两名队友攻击独立触发，无需达到四分之一");
Check(!ArrivalRules.IsPulled(arrivalFate with { Participants = largeCrowd.Select(p => p with { Party = false }).ToArray() }).Started, "非队友两人不误触发二队友条件");
var filtered = crowd.Concat([crowd[0], new(99, new(999, 0, 0), true, true, true), new(100, fate.Position, false, true, true)]).ToArray();
Check(ArrivalRules.IsPulled(arrivalFate with { Participants = filtered }).Players == 8, "分母去重并排除死亡与远处玩家");
Check(ArrivalRules.Position(fate.Position, fate.Position, 25) == fate.Position + new Vector3(25, 0, 0), "中心重合可退出到25米");

var mountActions = new FakeArrivalActions();
mesh = new(); using var arrival = new DestinationArrivalHelper(mesh, mountActions);
var frame = new ArrivalFrame(world, new(180, 0, 0), true, false, false, false, arrivalFate);
arrival.Start(goal, world, now); arrival.Tick(frame, now);
Check(arrival.Active && mountActions.Mounts == 0 && mesh.PathRequests == 0, "未开怪不执行坐骑或移动");
var pulledFate = arrivalFate with { HpRatio = .7f };
arrival.Tick(frame with { Fate = pulledFate }, now.AddSeconds(1));
arrival.Tick(frame with { Fate = pulledFate }, now.AddSeconds(2));
Check(mountActions.Mounts == 1 && mesh.PathRequests == 0, "无需同步尝试上坐骑，等待确认且限制频率");
arrival.Tick(frame with { Mounted = true, Fate = pulledFate }, now.AddSeconds(3));
arrival.Tick(frame with { Mounted = true, Fate = pulledFate }, now.AddSeconds(4));
Check(mesh.PathRequests == 1 && mesh.LastTarget == new Vector3(185, 0, 0) && mountActions.Dismounts == 0, "已在坐骑上不操作坐骑，目标为25米");
mesh.Complete(); arrival.Stop(); arrival.Tick(frame, now.AddSeconds(5));
Check(mesh.Token.IsCancellationRequested && mesh.Moves == 0, "到达处理停止后晚到路径不移动");

mesh = new(); mountActions = new(); using var syncArrival = new DestinationArrivalHelper(mesh, mountActions);
var syncFrame = frame with { Mounted = true, Fate = pulledFate with { NeedsSync = true } };
syncArrival.Start(goal, world, now); syncArrival.Tick(syncFrame, now);
Check(mountActions.Dismounts == 1 && mesh.PathRequests == 0, "需要同步先下坐骑，未确认前不靠近");
syncArrival.Tick(syncFrame with { Mounted = false }, now.AddSeconds(1));
syncArrival.Tick(syncFrame with { Mounted = false }, now.AddSeconds(2));
Check(mesh.PathRequests == 1 && mesh.LastTarget == new Vector3(195, 0, 0), "确认下坐骑后前往15米");
syncArrival.Tick(syncFrame with { World = world with { Map = 9 } }, now.AddSeconds(3));
Check(!syncArrival.Active && mesh.Token.IsCancellationRequested, "切图取消到达处理及寻路");

mesh = new(); mountActions = new(); using var unknownArrival = new DestinationArrivalHelper(mesh, mountActions);
unknownArrival.Start(goal, world, now); unknownArrival.Tick(frame with { Fate = pulledFate with { NeedsSync = null } }, now);
Check(mesh.PathRequests == 0 && mountActions.Mounts == 0 && mountActions.Dismounts == 0, "同步未知不执行动作");
unknownArrival.Tick(frame with { Fate = null }, now.AddSeconds(1));
Check(!unknownArrival.Active, "绑定后目标FATE消失停止");
unknownArrival.Start(goal, world, now); unknownArrival.Tick(frame with { Safe = false }, now);
Check(!unknownArrival.Active, "死亡或其他禁止状态停止到达处理");
unknownArrival.Start(goal, world, now); unknownArrival.Tick(frame, now.AddMinutes(5));
Check(!unknownArrival.Active, "到达处理五分钟超时");
unknownArrival.Start(goal, world, now); unknownArrival.Tick(frame with { Fate = pulledFate }, now);
unknownArrival.Tick(frame with { Fate = pulledFate with { StartedAt = 200 } }, now.AddSeconds(1));
Check(!unknownArrival.Active, "不同场次不沿用已开怪状态");

mesh = new(); mountActions = new(); using var combatArrival = new DestinationArrivalHelper(mesh, mountActions);
var combatFrame = frame with { InCombat = true, Fate = pulledFate };
combatArrival.Start(goal, world, now); combatArrival.Tick(combatFrame, now); combatArrival.Tick(combatFrame, now.AddSeconds(1));
Check(mountActions.Mounts == 0 && mesh.PathRequests == 1, "战斗中无法上坐骑时仍调整至25米");
combatArrival.Stop();
mesh = new(); mountActions = new(); using var retryArrival = new DestinationArrivalHelper(mesh, mountActions);
retryArrival.Start(goal, world, now);
foreach (var second in new[] { 0, 3, 6, 7, 9, 10 }) retryArrival.Tick(frame with { Fate = pulledFate }, now.AddSeconds(second));
Check(mountActions.Mounts == 3 && mesh.PathRequests == 1, "上坐骑最多三次后继续调整距离");
mesh = new(); mountActions = new(); using var dismountFailure = new DestinationArrivalHelper(mesh, mountActions);
dismountFailure.Start(goal, world, now);
foreach (var second in new[] { 0, 3, 6, 9 }) dismountFailure.Tick(syncFrame, now.AddSeconds(second));
Check(mountActions.Dismounts == 3 && !dismountFailure.Active && mesh.PathRequests == 0, "下坐骑失败三次停止，不带坐骑靠近15米");
mesh = new(); mountActions = new(); using var castArrival = new DestinationArrivalHelper(mesh, mountActions);
castArrival.Start(goal, world, now); castArrival.Tick(frame with { Casting = true, Fate = pulledFate }, now);
Check(mesh.PathRequests == 0 && mountActions.Mounts == 0, "施法时不操作坐骑或移动");
mesh = new() { CurrentRoute = [new(99, 0, 0)] }; mountActions = new();
using var occupiedArrival = new DestinationArrivalHelper(mesh, mountActions);
occupiedArrival.Start(goal, world, now); occupiedArrival.Tick(frame with { Fate = pulledFate }, now);
Check(!occupiedArrival.Active && mountActions.Mounts == 0 && mesh.Stops == 0, "其他导航占用时不操作坐骑，也不停止他人路线");
var rosterSource = new FakeRosterSource();
var roster = new IslandRosterHelper(rosterSource);
var self = new PlayerIdentity("自己", 1);
var remote = new IslandMember(new("远处玩家", 2), "服务器", 77);
roster.Tick(732, true, now);
Check(rosterSource.Requests == 1 && roster.Snapshot.UpdatedAt == null, "名单首次入岛请求，未完成前不标已更新");
rosterSource.Result = [new(self, "自己服务器", 1), remote, remote];
roster.Tick(732, true, now.AddSeconds(1));
Check(roster.Snapshot.Members.Count == 2 && roster.OtherPlayers(self, now.AddSeconds(1)) == 1, "名单去重并在人数分母排除自己");
var mergedPlayers = roster.Merge([new(self, "自己服务器", player, 0)]);
Check(mergedPlayers.Count == 2 && mergedPlayers.Single(p => p.Identity == remote.Identity).IsLoaded == false, "名单包含远处未加载玩家，不伪造实时位置");
roster.Tick(732, true, now.AddSeconds(29));
Check(rosterSource.Requests == 1, "30秒前不自动重复请求名单");
roster.Tick(732, true, now.AddSeconds(30));
Check(rosterSource.Requests == 2, "满30秒自动刷新名单");
rosterSource.Result = [new(self, "自己服务器", 1), remote]; roster.Tick(732, true, now.AddSeconds(31));
roster.Refresh(); roster.Tick(732, true, now.AddSeconds(32));
Check(rosterSource.Requests == 2, "手动刷新也遵守5秒请求间隔");
roster.Tick(732, true, now.AddSeconds(35));
Check(rosterSource.Requests == 3, "手动刷新可提前于30秒执行");
roster.Tick(732, true, now.AddSeconds(51));
Check(roster.Snapshot.Members.Count == 2 && roster.Snapshot.Status.Contains("超时"), "名单请求超时保留旧缓存");
Check(roster.OtherPlayers(self, now.AddSeconds(122)) == null, "超过90秒缓存不用作比例分母");
roster.Reset(); roster.Tick(763, false, now.AddSeconds(60));
Check(roster.Snapshot.Members.Count == 0 && rosterSource.Requests == 3, "切岛清缓存，占用时不发名单请求");
roster.Tick(1, true, now.AddSeconds(61));
Check(rosterSource.Requests == 3, "非优雷卡区域不请求岛内名单");
Check(!ArrivalRules.IsPulled(arrivalFate with { Participants = largeCrowd.Take(2).Select(p => p with { Party = false }).ToArray(), IslandPlayerCount = 20 }).Started,
    "全岛20人时附近2人攻击不足四分之一");
Check(!ArrivalRules.IsPulled(arrivalFate with { Participants = crowd.Select(p => p with { Attacking = true, Party = false }).ToArray(), RequireIslandRoster = true }).Started,
    "全岛名单未知时不退回低人数比例误判开怪");
Check(ArrivalRules.IsPulled(arrivalFate with { HpRatio = .7f, RequireIslandRoster = true }).Started,
    "名单未知仍可通过低血量判定开怪");

var crystals = IslandCrystalRoutes.All.Where(c => c.Territory == 732).ToArray();
Check(IslandCrystalRoutes.All.Count == 14 && IslandCrystalRoutes.All.GroupBy(c => c.Territory).Select(g => g.Count()).SequenceEqual([3,4,4,3]), "四岛14个水晶点位范围正确");
var sourceCrystal = crystals[0]; var targetCrystal = crystals[1];
var crystalWorld = world with { Territory = 732, Map = 414 };
var crystalGoal = new MapDestination(732, 414, targetCrystal.Position + new Vector3(100, 0, 0), "水晶后的目标");
var crystalPlan = IslandCrystalRoutes.Choose(732, sourceCrystal.Position, crystalGoal.Position);
Check(crystalPlan?.Source == sourceCrystal && crystalPlan.Target == targetCrystal, "明显缩短路程时选择源与目标水晶");
Check(IslandCrystalRoutes.Choose(732, sourceCrystal.Position, sourceCrystal.Position + new Vector3(10,0,0)) == null, "短距离保持直接导航");
Check(IslandCrystalRoutes.Choose(732, sourceCrystal.Position, crystalGoal.Position, sourceCrystal, new HashSet<uint>()) == null, "未解锁的菜单目标不进入传送计划");
Check(IslandCrystalRoutes.Choose(1, sourceCrystal.Position, crystalGoal.Position) == null, "不跨岛选择水晶");
Check(IslandCrystalRoutes.Choose(732, new(float.NaN,0,0), crystalGoal.Position) == null, "拒绝非法规划坐标");
mesh = new(); var crystalActions = new FakeCrystalActions { Available = new HashSet<uint> { targetCrystal.PlaceNameId } };
mountActions = new(); using var travel = new IslandTravelHelper(mesh, crystalActions, mountActions);
travel.Start(crystalGoal, crystalWorld, sourceCrystal.Position, [], now);
travel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now);
Check(crystalActions.Opens == 1 && travel.TakeArrival() == null, "到源水晶只打开菜单，不误报最终到达");
travel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(1));
Check(travel.Transferring && crystalActions.Selections == 1 && crystalActions.Selected == targetCrystal, "验证可用菜单后只提交一次传送");
travel.Tick(crystalWorld, Vector3.Zero, false, true, false, false, [], now.AddSeconds(2));
Check(travel.Active && mesh.PathRequests == 0, "预期传送黑屏等待，不提前寻路");
travel.Tick(crystalWorld, targetCrystal.Position, true, false, false, false, [], now.AddSeconds(3));
Check(travel.Active && !travel.Transferring, "确认目标水晶附近落地后继续原目的地");
travel.Tick(crystalWorld, targetCrystal.Position, true, false, false, false, [], now.AddSeconds(5));
Check(mesh.PathRequests == 1, "落地后向原目标请求路径");
mesh.Complete(); travel.Tick(crystalWorld, targetCrystal.Position, true, false, false, false, [], now.AddSeconds(6));
travel.Tick(crystalWorld, crystalGoal.Position, true, false, false, false, [], now.AddSeconds(7));
Check(!travel.Active && travel.TakeArrival() == crystalGoal && travel.TakeArrival() == null, "仅真正完成最后一段时发一次到达信号");
mesh = new(); crystalActions = new() { Available = new HashSet<uint>() };
using var lockedTravel = new IslandTravelHelper(mesh, crystalActions, new FakeArrivalActions());
lockedTravel.Start(crystalGoal, crystalWorld, sourceCrystal.Position, [], now);
lockedTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now);
lockedTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(1));
Check(lockedTravel.Active && !lockedTravel.Transferring && crystalActions.Selections == 0, "目标未解锁回退直接导航，不盲选菜单");
mesh = new(); crystalActions = new() { Available = new HashSet<uint> { targetCrystal.PlaceNameId } };
using var cancelledTravel = new IslandTravelHelper(mesh, crystalActions, new FakeArrivalActions());
cancelledTravel.Start(crystalGoal, crystalWorld, sourceCrystal.Position, [], now);
cancelledTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now);
cancelledTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(1));
cancelledTravel.Stop(); cancelledTravel.Tick(crystalWorld, targetCrystal.Position, true, false, false, false, [], now.AddSeconds(3));
Check(!cancelledTravel.Active && mesh.PathRequests == 0 && cancelledTravel.TakeArrival() == null, "传送后手动停止不会落地自动恢复");
mesh = new(); crystalActions = new() { Available = new HashSet<uint> { targetCrystal.PlaceNameId } };
using var timeoutTravel = new IslandTravelHelper(mesh, crystalActions, new FakeArrivalActions());
timeoutTravel.Start(crystalGoal, crystalWorld, sourceCrystal.Position, [], now);
timeoutTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now);
timeoutTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(1));
timeoutTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(47));
Check(!timeoutTravel.Active && mesh.PathRequests == 0, "传送未落地超时停止，不在错误位置继续");
mesh = new(); crystalActions = new(); mountActions = new();
using var mountedTravel = new IslandTravelHelper(mesh, crystalActions, mountActions);
mountedTravel.Start(crystalGoal, crystalWorld, sourceCrystal.Position, [], now);
mountedTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, true, false, [], now);
Check(mountActions.Dismounts == 1 && crystalActions.Opens == 0, "源水晶先确认下坐骑再交互");
mountedTravel.Tick(crystalWorld with { Territory = 763 }, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(1));
Check(!mountedTravel.Active, "真正切岛停止传送任务");
mesh = new(); crystalActions = new();
using var sourceFailure = new IslandTravelHelper(mesh, crystalActions, new FakeArrivalActions());
var beforeSource = sourceCrystal.Position + new Vector3(50, 0, 0);
sourceFailure.Start(crystalGoal, crystalWorld, beforeSource, [], now);
sourceFailure.Tick(crystalWorld, beforeSource, true, false, false, false, [], now);
mesh.Pending!.SetResult([]);
sourceFailure.Tick(crystalWorld, beforeSource, true, false, false, false, [], now.AddSeconds(1));
sourceFailure.Tick(crystalWorld, beforeSource, true, false, false, false, [], now.AddSeconds(3));
Check(sourceFailure.Active && mesh.PathRequests == 2 && mesh.LastTarget == crystalGoal.Position && crystalActions.Opens == 0,
    "源水晶不可达时回退原目的地导航");
mesh = new(); crystalActions = new();
using var directTravel = new IslandTravelHelper(mesh, crystalActions, new FakeArrivalActions());
var nearbyGoal = crystalGoal with { Position = sourceCrystal.Position + new Vector3(10,0,0) };
directTravel.Start(nearbyGoal, crystalWorld, sourceCrystal.Position, [], now);
directTravel.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(2));
Check(directTravel.Active && mesh.PathRequests == 1 && crystalActions.Opens == 0, "短途导航保留原2秒等待，不使用水晶");
mesh = new(); crystalActions = new() { NeverReady = true };
using var missingMenu = new IslandTravelHelper(mesh, crystalActions, new FakeArrivalActions());
missingMenu.Start(crystalGoal, crystalWorld, sourceCrystal.Position, [], now);
missingMenu.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now);
missingMenu.Tick(crystalWorld, sourceCrystal.Position, true, false, false, false, [], now.AddSeconds(16));
Check(missingMenu.Active && crystalActions.Opens == 1 && crystalActions.Selections == 0 && !missingMenu.Transferring,
    "菜单未出现时超时回退，不重复交互");
using var sequence = new FollowFixture();
Check(sequence.Follow.Start(sequence.World, now), "跟随显式开始并验证战斗依赖");
sequence.Follow.Tick(sequence.Frame, false, [], new(null), now);
Check(sequence.Travel.Active == false && sequence.Combat.Enables == 0, "开始跟随不使用旧坐标自动移动或开循环");
sequence.Follow.Receive(sequence.Goal);
sequence.Follow.Tick(sequence.Frame with { Mounted = false }, false, [], new(null), now);
Check(sequence.Mount.Mounts == 1 && sequence.Mesh.PathRequests == 0, "新坐标先召唤所选坐骑，确认前不寻路");
sequence.Follow.Tick(sequence.Frame with { Mounted = false, Casting = true }, false, [], new(null), now.AddSeconds(1));
Check(sequence.Mount.Mounts == 1 && sequence.Mesh.PathRequests == 0, "坐骑施法中不重复召唤或移动");
sequence.Follow.Tick(sequence.Frame, false, [], new(null), now.AddSeconds(2));
sequence.Follow.Tick(sequence.Frame, false, [], new(null), now.AddSeconds(4));
Check(sequence.Mount.Dismounts == 1 && sequence.Combat.Enables == 0, "到达没有NM时也先下坐骑等待");
sequence.Follow.Tick(sequence.Frame with { Mounted = false }, false, [], new(sequence.Fate), now.AddSeconds(5));
Check(sequence.Combat.Enables == 1 && sequence.Combat.TargetId == 99, "NM已开怪且无需同步时锁定头目并启用战斗控制");
var nextGoal = sequence.Goal with { Position = new(200,0,0) };
sequence.Follow.Receive(nextGoal);
sequence.Follow.Tick(sequence.Frame with { Mounted = false, InCombat = true }, false, [], new(sequence.Fate), now.AddSeconds(6));
Check(sequence.Follow.Goal == sequence.Goal && !sequence.Travel.Active, "战斗中的新坐标只缓存，不转向或寻路");
sequence.Follow.Tick(sequence.Frame with { Mounted = false }, false, [], new(null, true), now.AddSeconds(7));
Check(!sequence.Combat.Enabled && sequence.Combat.Disables == 1 && sequence.Mount.Mounts == 2, "事件结束同一帧关闭自己控制并立即尝试上坐骑");
sequence.Follow.Tick(sequence.Frame with { Mounted = false }, false, [], new(null, true), now.AddSeconds(8));
Check(sequence.Mount.Mounts == 2, "脱战后立即尝试召唤所选坐骑");
sequence.Follow.Tick(sequence.Frame, false, [], new(null), now.AddSeconds(9));
sequence.Follow.Tick(sequence.Frame, false, [], new(null), now.AddSeconds(10));
Check(sequence.Follow.Goal == nextGoal && !sequence.Travel.Active && sequence.Follow.Status.Contains("水晶"), "战后坐标附近无新NM时改为最近水晶待机");
sequence.Follow.Tick(sequence.Frame, false, [new(1444,"新的NM",nextGoal.Position,200)], new(null), now.AddSeconds(10.5));
Check(sequence.Follow.Goal == nextGoal && sequence.Travel.Active, "待机期间车头坐标附近出现新NM后恢复前往该目标");
sequence.Follow.Stop(); sequence.Follow.Tick(sequence.Frame, false, [], new(sequence.Fate), now.AddSeconds(11));
Check(!sequence.Follow.Active && !sequence.Travel.Active && sequence.Combat.Enables == 1, "停止跟随后不会被新一帧恢复移动或循环");

using var syncSequence = new FollowFixture();
syncSequence.Arrive(now);
var needsSyncFate = syncSequence.Fate with { NeedsSync = true, Synced = false };
syncSequence.Follow.Tick(syncSequence.Frame with { Mounted = false }, false, [], new(needsSyncFate), now.AddSeconds(3));
Check(syncSequence.Combat.Syncs == 1 && syncSequence.Combat.Enables == 0, "需要同步时先请求同步，不提前开启循环");
syncSequence.Follow.Tick(syncSequence.Frame with { Mounted = false }, false, [], new(needsSyncFate), now.AddSeconds(4));
Check(syncSequence.Combat.Syncs == 1 && syncSequence.Combat.Enables == 0, "等待游戏同步确认并限制请求频率");
syncSequence.Follow.Tick(syncSequence.Frame with { Mounted = false }, false, [], new(needsSyncFate with { Synced = true }), now.AddSeconds(5));
Check(syncSequence.Combat.Enables == 1 && syncSequence.Combat.TargetId == 99, "确认等级同步后才开启循环与BossMod");
syncSequence.Combat.TargetId = 77;
syncSequence.Follow.Tick(syncSequence.Frame with { Mounted = false, InCombat = true }, false, [], new(needsSyncFate with { Synced = true }), now.AddSeconds(6));
Check(syncSequence.Combat.TargetId == 99, "战斗过程中持续重新选中NM头目");
syncSequence.Follow.Tick(syncSequence.Frame with { Mounted = false, InCombat = true }, false, [], new(needsSyncFate), now.AddSeconds(7));
Check(!syncSequence.Combat.Enabled, "同步丢失立即关闭循环，重新进入准备阶段");
syncSequence.Follow.Tick(syncSequence.Frame with { Safe = false }, false, [], new(needsSyncFate), now.AddSeconds(8));
Check(!syncSequence.Follow.Active && !syncSequence.Combat.Enabled, "死亡或禁止状态清理战斗控制并停止跟随");

using var notPulled = new FollowFixture(); notPulled.Arrive(now);
notPulled.Follow.Tick(notPulled.Frame with { Mounted = false }, false, [], new(notPulled.Fate with { HpRatio = 1 }), now.AddSeconds(3));
Check(notPulled.Combat.Enables == 0 && notPulled.Combat.TargetId == 0, "未开怪时不主动开循环或攻击头目");
notPulled.Follow.Tick(notPulled.Frame with { Mounted = false }, false, [], new(notPulled.Fate with { NeedsSync = null }), now.AddSeconds(4));
Check(notPulled.Combat.Enables == 0 && notPulled.Combat.Syncs == 0, "同步需求未知时不盲目同步或开循环");

using var failedSync = new FollowFixture(); failedSync.Arrive(now);
foreach (var second in new[] { 3,6,9,12 }) failedSync.Follow.Tick(failedSync.Frame with { Mounted = false }, false, [],
    new(failedSync.Fate with { NeedsSync = true, Synced = false }), now.AddSeconds(second));
Check(failedSync.Combat.Syncs == 3 && failedSync.Combat.Enables == 0 && !failedSync.Follow.Active, "同步三次未确认后停止，不启动循环");

using var noBoss = new FollowFixture(); noBoss.Arrive(now);
noBoss.Follow.Tick(noBoss.Frame with { Mounted = false }, false, [], new(noBoss.Fate with { BossId = 0 }), now.AddSeconds(3));
Check(noBoss.Combat.Enables == 0 && noBoss.Combat.TargetId == 0, "未确认头目时不随机选取小怪或开启循环");

using var bossDeath = new FollowFixture(); bossDeath.Arrive(now);
bossDeath.Follow.Tick(bossDeath.Frame with { Mounted = false }, false, [], new(bossDeath.Fate), now.AddSeconds(3));
bossDeath.Follow.Tick(bossDeath.Frame with { Mounted = false, InCombat = true }, false, [], new(bossDeath.Fate with { BossAlive = false }), now.AddSeconds(4));
bossDeath.Follow.Tick(bossDeath.Frame with { Mounted = false, InCombat = true }, false, [], new(null), now.AddSeconds(5));
Check(!bossDeath.Combat.Enabled && bossDeath.Mount.Mounts == 0, "头目死亡即关闭控制，仍有仇恨时等待脱战再上坐骑");

using var oneShot = new FollowFixture(); oneShot.Arrive(now, false);
oneShot.Follow.Tick(oneShot.Frame with { Mounted = false }, false, [], new(oneShot.Fate), now.AddSeconds(3));
oneShot.Follow.Tick(oneShot.Frame with { Mounted = false }, false, [], new(null, true), now.AddSeconds(4));
oneShot.Follow.Tick(oneShot.Frame, false, [], new(null), now.AddSeconds(5));
Check(!oneShot.Follow.Active && !oneShot.Follow.Following && !oneShot.Combat.Enabled, "手动单次导航打完并上坐骑后结束，不进入持续跟随");

using var unavailable = new FollowFixture(); unavailable.Combat.Error = "依赖不可用";
Check(!unavailable.Follow.Start(unavailable.World, now) && unavailable.Mesh.PathRequests == 0, "依赖不可用时拒绝启动，不执行移动或技能");
Check(!unavailable.Follow.Start(world, now), "岛外不启动跟随伐木");
using var bounded = new FollowFixture(); bounded.Follow.Start(bounded.World, now);
bounded.Follow.Tick(bounded.Frame, false, [], new(null), now.AddHours(2));
Check(!bounded.Follow.Active, "跟随会话达到2小时上限停止");
using var failedEnable = new FollowFixture(); failedEnable.Combat.EnableResult = false; failedEnable.Arrive(now);
failedEnable.Follow.Tick(failedEnable.Frame with { Mounted = false }, false, [], new(failedEnable.Fate), now.AddSeconds(3));
Check(!failedEnable.Follow.Active && !failedEnable.Combat.Enabled, "战斗控制启动失败停止整个流程");
using var cancelledMount = new FollowFixture(); cancelledMount.Follow.Start(cancelledMount.World, now); cancelledMount.Follow.Receive(cancelledMount.Goal);
cancelledMount.Follow.Tick(cancelledMount.Frame with { Mounted = false }, false, [], new(null), now);
cancelledMount.Follow.Stop(); cancelledMount.Follow.Tick(cancelledMount.Frame, false, [], new(null), now.AddSeconds(5));
Check(cancelledMount.Mesh.PathRequests == 0 && !cancelledMount.Follow.Active, "上坐骑等待期间停止，不在召唤完成后启动导航");
Check(NavigationRules.TransferContext(new(0,0,0,0), crystalWorld, true,true,true) == crystalWorld, "预期岛内传送保留黑屏期间暂时缺少的会话字段");
Check(NavigationRules.TransferContext(crystalWorld with { PlayerId = 99 }, crystalWorld,true,true,true).PlayerId == 99, "换角色不得沿用传送会话");
Check(NavigationRules.TransferContext(crystalWorld with { Territory = 763 }, crystalWorld,true,true,true).Territory == 763, "真实换岛不得沿用传送会话");
Check(NavigationRules.TransferContext(new(0,0,0,0), crystalWorld,false,true,true).PlayerId == 0, "登出不保留旧会话");
Check(NavigationRules.TransferContext(new(0,0,0,0), crystalWorld,true,false,true).Territory == 0, "非本任务传送不掩盖切区变化");
Check(IslandCrystalRoutes.Nearest(732, Vector3.Zero)?.PlaceNameId == 2436, "水晶待机选择玩家当前位置最近的本岛水晶");
Check(IslandCrystalRoutes.Nearest(1, Vector3.Zero) == null && IslandCrystalRoutes.Nearest(732,new(float.NaN,0,0)) == null,
    "水晶待机拒绝其他区域或非法坐标");
using var standby = new FollowFixture(); standby.Arrive(now);
standby.Follow.Tick(standby.Frame with { Mounted = false }, false, [], new(standby.Fate), now.AddSeconds(3));
standby.Follow.Tick(standby.Frame with { Mounted = false }, false, [], new(null,true), now.AddSeconds(4));
var oldFate = new FateEntry(standby.Fate.Id,"已完成NM", standby.Goal.Position, standby.Fate.StartedAt);
standby.Follow.Tick(standby.Frame, false, [oldFate], new(null), now.AddSeconds(5));
standby.Follow.Tick(standby.Frame, false, [oldFate], new(null), now.AddSeconds(6));
var crystalPosition = IslandCrystalRoutes.Nearest(732, standby.Frame.Player)!.Position;
Check(!standby.Travel.Active && standby.Follow.Status.Contains("水晶") && Math.Abs(NavigationRules.Distance(standby.Mesh.LastTarget, crystalPosition)-7) < .01f,
    "已完成NM即使仍在表中也不重复前往，待机终点位于最近水晶旁7米");
standby.Mesh.Complete();
standby.Follow.Tick(standby.Frame, false, [oldFate], new(null), now.AddSeconds(7));
standby.Follow.Tick(standby.Frame with { Player = standby.Mesh.LastTarget }, false, [oldFate], new(null), now.AddSeconds(8));
Check(standby.Follow.Active && standby.Follow.Status.Contains("已在最近水晶附近待机") && !standby.Combat.Enabled,
    "抵达水晶后保持跟随会话但不开战斗控制");
var newest = standby.Goal with { Position = new(500,0,500) };
standby.Follow.Receive(newest);
standby.Follow.Tick(standby.Frame with { Player = standby.Mesh.LastTarget }, false,
    [new(1444,"旧坐标的新NM",standby.Goal.Position,200)], new(null), now.AddSeconds(9));
Check(!standby.Travel.Active, "待机中新坐标优先，不被旧监视点新出现的NM覆盖");
standby.Follow.Tick(standby.Frame with { Player = standby.Mesh.LastTarget }, false,
    [new(1445,"新坐标的NM",newest.Position,201)], new(null), now.AddSeconds(10));
Check(standby.Travel.Active && standby.Follow.Goal == newest, "待机中最新车头坐标附近出现NM后恢复导航");
using var respawn = new FollowFixture(); respawn.Arrive(now);
respawn.Follow.Tick(respawn.Frame with { Mounted = false }, false, [], new(respawn.Fate), now.AddSeconds(3));
respawn.Follow.Tick(respawn.Frame with { Mounted = false }, false, [], new(null,true), now.AddSeconds(4));
var newSpawn = new FateEntry(respawn.Fate.Id,"同ID新一场NM",Vector3.Zero,respawn.Fate.StartedAt+100);
respawn.Follow.Tick(respawn.Frame, false, [newSpawn], new(null), now.AddSeconds(5));
respawn.Follow.Tick(respawn.Frame, false, [newSpawn], new(null), now.AddSeconds(6));
Check(respawn.Travel.Active, "同ID不同开始时间的新场次仍可作为新目标");
using var staleFate = new FollowFixture(); staleFate.Arrive(now);
staleFate.Follow.Tick(staleFate.Frame with { Mounted = false }, false, [], new(staleFate.Fate), now.AddSeconds(3));
staleFate.Follow.Tick(staleFate.Frame with { Mounted = false }, false, [], new(null,true), now.AddSeconds(4));
staleFate.Follow.Tick(staleFate.Frame, false, [new(staleFate.Fate.Id,"更早的旧场次",Vector3.Zero,staleFate.Fate.StartedAt-1)], new(null), now.AddSeconds(5));
Check(!staleFate.Travel.Active && staleFate.Follow.Status.Contains("水晶"), "较早的旧发生记录不被当作新NM");
using var alreadyRunning = new FollowFixture(); alreadyRunning.Combat.Preexisting = true;
Check(alreadyRunning.Follow.Start(alreadyRunning.World, now) && !alreadyRunning.Combat.Preexisting
    && alreadyRunning.Combat.Prepares == 1 && alreadyRunning.Combat.Enables == 0,
    "开始伐木时先关闭已开启的控制并继续等待坐标，不以已开启报错");
var activityListener = new LeaderListenerHelper();
activityListener.RecordActivity(now,"小怪击杀","仙人掌 · 仙人花 +2","本轮累计2只");
Check(activityListener.History.Single().Channel == "小怪击杀" && activityListener.History.Single().Text.Contains("+2"),
    "小怪击杀可记录到车头历史中，不要求收到聊天消息");
using var noApproach = new FollowFixture(); noApproach.Arrive(now);
var beforeRequests = noApproach.Mesh.PathRequests;
noApproach.Follow.Tick(noApproach.Frame with { Mounted = false }, false, [], new(noApproach.Fate with { Centre = new(100,0,0) }), now.AddSeconds(3));
Check(noApproach.Combat.Enabled && noApproach.Mesh.PathRequests == beforeRequests, "开怪后只启用循环和AI，不另设15或25米移动目的地");
Check(new uint[] {732,763,795,827}.Select(t => IslandCrystalRoutes.Entrance(t)?.PlaceNameId).SequenceEqual(new uint?[] {2415,2463,2531,2876}), "四岛入口使用已核实营地水晶，不混用兵武塔入口");
mesh = new(); crystalActions = new() { Available = new HashSet<uint> { targetCrystal.PlaceNameId } };
using var closeCrystal = new IslandTravelHelper(mesh,crystalActions,new FakeArrivalActions());
var farFromCrystal = sourceCrystal.Position + new Vector3(50,0,0);
closeCrystal.Start(crystalGoal,crystalWorld,farFromCrystal,[],now);
closeCrystal.Tick(crystalWorld,farFromCrystal,true,false,false,false,[],now);
mesh.Complete(); closeCrystal.Tick(crystalWorld,farFromCrystal,true,false,false,false,[],now.AddSeconds(1));
closeCrystal.Tick(crystalWorld,sourceCrystal.Position+new Vector3(5,0,0),true,false,false,false,[],now.AddSeconds(2));
Check(crystalActions.Opens == 0, "前往水晶不能以普通3米到达容差过早结束");
closeCrystal.Tick(crystalWorld,sourceCrystal.Position+new Vector3(2,0,0),true,false,false,false,[],now.AddSeconds(3));
Check(crystalActions.Opens == 1, "到源水晶2米接近点后才打开传送菜单");
var debugActions = new FakeArrivalActions();
var debugCrystals = new FakeCrystalActions { Available = new HashSet<uint> { targetCrystal.PlaceNameId } };
var debugTest = new SoloDebugHelper(debugCrystals, debugActions);
var debugFrame = new SoloDebugFrame(crystalWorld,sourceCrystal.Position,true,false,false,false,false);
debugTest.Start(SoloDebugAction.Mount,debugFrame,null,now);
debugTest.Tick(debugFrame,now); debugTest.Tick(debugFrame,now.AddSeconds(2));
Check(debugActions.Mounts == 1 && debugTest.Active, "单人骑乘测试仅发一次请求，等待真实状态");
debugTest.Tick(debugFrame with { Mounted = true },now.AddSeconds(3));
Check(!debugTest.Active && debugTest.Status.Contains("通过"), "单人骑乘测试确认上坐骑才通过");
debugTest.Start(SoloDebugAction.Dismount,debugFrame with { Mounted = true },null,now);
debugTest.Tick(debugFrame with { Mounted = true },now);
debugTest.Tick(debugFrame,now.AddSeconds(1));
Check(debugActions.Dismounts == 1 && !debugTest.Active, "单人下坐骑测试确认实际变化");
debugTest.Start(SoloDebugAction.Mount,debugFrame,null,now); debugTest.Tick(debugFrame,now);
debugTest.Tick(debugFrame,now.AddSeconds(12));
Check(!debugTest.Active && debugTest.Status.Contains("12秒"), "单人骑乘无结果12秒超时，不反复重试");
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame,targetCrystal,now);
debugTest.Tick(debugFrame,now);
Check(debugTest.Transferring && debugCrystals.Selections == 1, "单人水晶测试直接交互菜单选择一次，无需导航插件或队友");
debugTest.Tick(debugFrame with { Safe = false, Loading = true },now.AddSeconds(2));
Check(debugTest.Active, "单人水晶测试等待预期传送黑屏");
debugTest.Tick(debugFrame with { Position = targetCrystal.Position },now.AddSeconds(3));
Check(!debugTest.Active && debugTest.Status.Contains("落地"), "单人水晶测试确认落地结束，不自动回程");
debugCrystals.Available = new HashSet<uint>();
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame,targetCrystal,now); debugTest.Tick(debugFrame,now);
Check(!debugTest.Active && debugCrystals.Selections == 1 && debugTest.Status.Contains("不在可用菜单"), "未解锁的调试目标不提交传送");
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame,sourceCrystal,now);
Check(!debugTest.Active && debugTest.Status.Contains("另一座"), "单人水晶测试拒绝源目标相同");
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame with { Position = Vector3.Zero },targetCrystal,now);
Check(!debugTest.Active && debugTest.Status.Contains("5米"), "单人水晶测试要求先手动接近，不擅自走远");
debugTest.Start(SoloDebugAction.Mount,debugFrame with { InCombat = true },null,now);
Check(!debugTest.Active && debugTest.Status.Contains("脱战"), "战斗中不开始单人骑乘测试");
debugCrystals.Available = new HashSet<uint> { targetCrystal.PlaceNameId };
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame,targetCrystal,now); debugTest.Tick(debugFrame,now);
debugTest.Stop("手动停止",now.AddSeconds(1));
debugTest.Tick(debugFrame with { Position = targetCrystal.Position },now.AddSeconds(3));
Check(!debugTest.Active && debugTest.Status == "手动停止", "单人测试停止后晚到落地不恢复任务");
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame,targetCrystal,now); debugTest.Tick(debugFrame,now);
debugTest.Tick(debugFrame,now.AddSeconds(45));
Check(!debugTest.Active && debugTest.Status.Contains("45秒"), "单人传送45秒未落地停止并记录原因");
debugCrystals.NeverReady = true;
debugTest.Start(SoloDebugAction.CrystalTeleport,debugFrame,targetCrystal,now); debugTest.Tick(debugFrame,now);
debugTest.Tick(debugFrame,now.AddSeconds(15));
Check(!debugTest.Active && debugTest.Status.Contains("15秒"), "单人水晶菜单15秒超时停止");
debugTest.Start(SoloDebugAction.Mount,debugFrame,null,now);
debugTest.Tick(debugFrame with { World = crystalWorld with { Territory = 763 } },now);
Check(!debugTest.Active, "单人测试真实切岛取消，不沿用原岛目标");
for (var index = 0; index < 100; index++) debugTest.Record("历史"+index,now);
Check(debugTest.History.Count == 40, "单人测试历史最多保留40条");
AutoTriggerChecks.Run(Check);
var recordingDirectory = Path.Combine(AppContext.BaseDirectory, "recording-test-" + Guid.NewGuid().ToString("N"));
var recording = new DebugRecording(recordingDirectory);
var recordTime = DateTimeOffset.UtcNow;
recording.Start(recordTime, "offline");
recording.Change(recordTime, "目标", "无仇恨");
recording.Change(recordTime, "目标", "无仇恨");
recording.Change(recordTime.AddSeconds(1), "目标", "已有仇恨");
recording.Finish(recordTime.AddSeconds(2), "测试结束");
Check(SpinWait.SpinUntil(() => !recording.Snapshot.Saving, TimeSpan.FromSeconds(5))
    && recording.Snapshot.Path is { } savedPath && File.Exists(savedPath), "调试记录：结束后写入文件");
var reopened = new DebugRecording(recordingDirectory);
reopened.LoadLatest();
Check(reopened.Snapshot.Text is { } savedText
    && savedText.Split("目标 | 无仇恨").Length == 2
    && savedText.Contains("目标 | 已有仇恨")
    && savedText.Contains("结束记录"), "调试记录：重开后读取最近文件并保留有序状态变化");
File.Delete(recording.Snapshot.Path!);
Directory.Delete(recordingDirectory);
Console.WriteLine($"{count} automation checks passed.");

sealed class FollowFixture : IDisposable
{
    public readonly FakeMesh Mesh = new();
    public readonly FakeArrivalActions Mount = new();
    public readonly FakeCombat Combat = new();
    public readonly IslandTravelHelper Travel;
    public readonly FellingFollowHelper Follow;
    public WorldContext World = new(732,414,0,42);
    public MapDestination Goal = new(732,414,Vector3.Zero,"测试NM坐标");
    public ArrivalFrame Frame => new(World, Vector3.Zero, true, true, false, false, null);
    public ArrivalFate Fate = new(1332,100,Vector3.Zero,50,false,.7f,[],0,true,99,true,true,false);
    public FollowFixture() { Travel = new(Mesh,new FakeCrystalActions(),Mount); Follow = new(Travel,Mesh,Mount,Combat); }
    public void Arrive(DateTimeOffset now, bool repeat = true)
    {
        Follow.Start(World,now,repeat); Follow.Receive(Goal);
        Follow.Tick(Frame,false,[],new(null),now);
        Follow.Tick(Frame,false,[],new(null),now.AddSeconds(2));
    }
    public void Dispose() { Follow.Dispose(); Travel.Dispose(); }
}
sealed class FakeCombat : IFellingCombat
{
    public string? Error;
    public bool Preexisting;
    public int Prepares;
    public bool EnableResult = true, Enabled;
    public int Enables, Disables, Syncs;
    public ulong TargetId;
    public string? CheckReady() => Error ?? (Preexisting ? "已有控制开启" : null);
    public string? PrepareStart() { Prepares++; Preexisting = false; return CheckReady(); }
    public bool Target(ulong bossId) { TargetId = bossId; return true; }
    public bool Sync(ushort fateId) { Syncs++; return true; }
    public bool Enable() { Enables++; Enabled = EnableResult; return Enabled; }
    public bool Disable() { if (Enabled) Disables++; Enabled = false; return true; }
    public bool Healthy => Enabled;
}

sealed class FakeRosterSource : IIslandRosterSource
{
    public int Requests;
    public IReadOnlyList<IslandMember>? Result;
    public bool Request() { Requests++; return true; }
    public IReadOnlyList<IslandMember>? TakeCompleted(uint territory) { var value = Result; Result = null; return value; }
}
sealed class FakeCrystalActions : IIslandCrystalActions
{
    public int Opens, Selections;
    public bool NeverReady;
    public IReadOnlySet<uint> Available = new HashSet<uint>();
    public IslandCrystal? Selected;
    private bool open;
    public bool Open(IslandCrystal source) { Opens++; open = true; return true; }
    public IReadOnlySet<uint>? Choices() => open && !NeverReady ? Available : null;
    public bool Select(IslandCrystal target) { Selections++; Selected = target; open = false; return true; }
    public void Cancel() => open = false;
}

sealed class FakeArrivalActions : IArrivalActions
{
    public int Mounts, Dismounts;
    public bool Mount() { Mounts++; return true; }
    public bool Dismount() { Dismounts++; return true; }
}

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
    public Vector3 LastTarget => target;
    public Vector3? Ground(Vector3 point, bool unknownHeight) => point;
    public Task<List<Vector3>> FindPath(Vector3 from, Vector3 to, CancellationToken token)
    { PathRequests++; Token = token; target = to; Pending = new(); return Pending.Task; }
    public void Complete() => Pending!.SetResult([target]);
    public void Move(List<Vector3> route) { CurrentRoute = route; Moves++; }
    public void Stop() { CurrentRoute = []; Stops++; }
}
