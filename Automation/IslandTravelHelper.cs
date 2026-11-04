using System.Numerics;

namespace ULKHelper.Automation;

internal sealed class IslandTravelHelper(INavigationMesh mesh, IIslandCrystalActions crystals, IArrivalActions mount) : IDisposable
{
    private readonly DestinationNavigationHelper walk = new(mesh);
    private MapDestination? goal, arrival;
    private CrystalRoute? route;
    private WorldContext context;
    private Vector3 origin;
    private DateTimeOffset started, phaseAt;
    private enum Phase { Mount, Source, Menu, Transfer, Final }
    private Phase phase;
    private int dismounts;
    private DateTimeOffset nextDismount;
    private bool requireMount, resumeFinal, useCrystals, waitWithoutFate;
    private int mountAttempts;
    private DateTimeOffset nextMount;
    public bool Active { get; private set; }
    public bool Transferring => Active && phase == Phase.Transfer;
    public bool Interacting => Active && phase == Phase.Menu;
    public string Status { get; private set; } = "尚未开始导航。";
    public string? CrystalDecision { get; private set; }
    public MapDestination? TakeArrival() { var result = arrival; arrival = null; return result; }
    public void Start(MapDestination destination, WorldContext world, Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now, bool summonMount = false, bool useCrystals = true, bool waitWithoutFate = true)
    {
        if (Active) { Status = "请先停止当前导航。"; return; }
        Stop(); CrystalDecision = null; goal = destination; context = world; origin = player; started = now;
        if (destination.Territory != world.Territory || destination.Map != world.Map || !NavigationRules.Finite(player))
        { Stop("目的地范围无效。"); return; }
        requireMount = summonMount; this.useCrystals = useCrystals; resumeFinal = false; mountAttempts = 0; nextMount = now;
        this.waitWithoutFate = waitWithoutFate;
        Active = true;
        if (requireMount) { phase = Phase.Mount; Status = "检查并召唤所选坐骑。"; return; }
        BeginJourney(player, fates, now);
    }
    private void BeginJourney(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        route = useCrystals ? IslandCrystalRoutes.Choose(context.Territory, player, goal!.Position) : null;
        CrystalDecision = route == null ? "水晶路线预计收益不足，直接前往。" : $"选择岛内水晶路线：{route.Source.PlaceNameId} → {route.Target.PlaceNameId}。";
        if (route == null) Final(player, fates, now);
        else
        {
            phase = Phase.Source;
            var sourceGoal = NavigationRules.Distance(player, route.Source.Position) <= 3 ? player
                : ArrivalRules.Position(player, route.Source.Position, 2);
            walk.Start(goal! with { Position = sourceGoal }, context, player, [], now, false, 1);
            if (!walk.Active)
            {
                if (mesh.Ready && !mesh.Busy && mesh.Route.Count == 0) Fallback(player, fates, now);
                else Stop(walk.Status);
            }
            else Status = "前往岛内水晶。";
        }
    }
    public void Tick(WorldContext world, Vector3 player, bool safe, bool transferScreen, bool mounted, bool casting,
        IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (world != context || !safe && !(Transferring && transferScreen)) { Stop("角色或地图状态变化，已停止导航。"); return; }
            if (now - started >= TimeSpan.FromMinutes(5)) { Stop("导航超过5分钟，已停止。"); return; }
            if (phase == Phase.Mount)
            {
                if (mesh.Busy || mesh.Route.Count > 0) { Stop("其他导航占用中，停止召唤坐骑。"); return; }
                if (casting) return;
                if (mounted) { if (resumeFinal) Final(player, fates, now); else BeginJourney(player, fates, now); return; }
                if (now < nextMount) return;
                if (mountAttempts++ >= 3) { Stop("无法召唤所选坐骑，已停止导航。"); return; }
                nextMount = now.AddSeconds(3); mount.Mount(); Status = "等待所选坐骑召唤完成。"; return;
            }
            if (phase == Phase.Transfer)
            {
                if (now - phaseAt >= TimeSpan.FromSeconds(45)) { Stop("未能确认岛内传送落地，已停止。"); return; }
                if (transferScreen || !safe || casting || NavigationRules.Distance(player, route!.Target.Position) > 15) return;
                if (now - phaseAt < TimeSpan.FromSeconds(1)) return;
                PrepareFinal(player, fates, now); return;
            }
            if (phase == Phase.Source || phase == Phase.Final)
            {
                walk.Tick(world, player, safe, phase == Phase.Source ? [] : fates, now);
                Status = phase == Phase.Source ? "前往岛内水晶：" + walk.Status : walk.Status;
                if (walk.Active) return;
                var completed = walk.TakeArrival();
                if (completed == null)
                {
                    if (phase == Phase.Source && mesh.Ready && !mesh.Busy && mesh.Route.Count == 0) Fallback(player, fates, now);
                    else Stop(walk.Status);
                    return;
                }
                if (phase == Phase.Final) { var reached = goal; Stop("已到达目的地。"); arrival = reached; return; }
                phase = Phase.Menu; phaseAt = now; dismounts = 0; nextDismount = now;
            }
            if (now - phaseAt >= TimeSpan.FromSeconds(15)) { Fallback(player, fates, now, "未在15秒内取得水晶传送菜单。"); return; }
            if (casting) return;
            if (mounted)
            {
                if (now < nextDismount) return;
                if (dismounts++ >= 3) { Stop("无法下坐骑进行水晶传送。"); return; }
                nextDismount = now.AddSeconds(3); mount.Dismount(); return;
            }
            var choices = crystals.Choices();
            if (choices == null)
            {
                // Only one interaction attempt: never reopen a dismissed or replaced menu.
                if (!opened)
                {
                    opened = true;
                    if (!crystals.Open(route!.Source)) Fallback(player, fates, now, crystals.Error ?? "无法与源水晶交互。");
                }
                return;
            }
            var availableRoute = IslandCrystalRoutes.Choose(world.Territory, origin, goal!.Position, route!.Source, choices);
            if (availableRoute == null) { Fallback(player, fates, now, "菜单中无已解锁且有收益的目标水晶。"); return; }
            route = availableRoute;
            if (!crystals.Select(route.Target)) { Fallback(player, fates, now, "未能唯一匹配并选择传送菜单条目。"); return; }
            phase = Phase.Transfer; phaseAt = now; Status = "等待岛内水晶传送落地。";
        }
        catch (Exception ex) { Stop("岛内导航失败：" + ex.Message); }
    }
    private bool opened;
    private void Fallback(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now, string reason = "源水晶路径不可达。")
    { CrystalDecision = reason + "回退直接前往。"; crystals.Cancel(); PrepareFinal(player, fates, now); }
    private void PrepareFinal(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        if (!requireMount) { Final(player, fates, now); return; }
        resumeFinal = true; mountAttempts = 0; nextMount = now; phase = Phase.Mount; Status = "传送后检查所选坐骑。";
    }
    private void Final(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        phase = Phase.Final;
        walk.Start(goal!, context, player, fates, now, waitWithoutFate);
        if (!walk.Active) Stop(walk.Status); else Status = walk.Status;
    }
    public void Stop(string reason = "已停止导航。")
    {
        Active = false; arrival = null; opened = false; walk.Stop();
        try { crystals.Cancel(); } catch { }
        Status = reason;
    }
    public void Dispose() { Stop(); walk.Dispose(); }
}
