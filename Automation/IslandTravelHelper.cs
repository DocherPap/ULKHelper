using System.Numerics;

namespace ULKHelper.Automation;

internal interface ICrystalCombat
{
    string? BeginCrystalCombat(object owner);
    bool EndCrystalCombat(object owner);
}

internal sealed class IslandTravelHelper(INavigationMesh mesh, IIslandCrystalActions crystals, IArrivalActions mount,
    ICrystalCombat? combat = null) : IDisposable
{
    private readonly DestinationNavigationHelper walk = new(mesh);
    private MapDestination? goal, arrival;
    private CrystalRoute? route;
    private CrystalPathPlanner? planner;
    private IReadOnlyList<CrystalRoute>? measuredRoutes;
    private WorldContext context;
    private Vector3 origin;
    private Vector3? sourceAnchor;
    private DateTimeOffset started, phaseAt;
    private DateTimeOffset? crystalCombatAt;
    private enum Phase { Plan, Mount, Source, Menu, Transfer, Final }
    private Phase phase;
    private int dismounts;
    private DateTimeOffset nextDismount;
    private bool requireMount, resumeFinal, useCrystals, waitWithoutFate;
    private float finalArrivalRadius;
    private bool finalUnknownHeight, preferCrystals;
    private float? verifiedFinalEndpointTolerance;
    private float finalNearbyGroundRadius;
    private Func<Vector3, bool>? finalGroundFilter;
    private IReadOnlySet<uint>? surveyVisited;
    public bool SurveyExhausted { get; private set; }
    private readonly TravelMountHelper travelMount = new(mount);
    public bool Active { get; private set; }
    public bool Transferring => Active && phase == Phase.Transfer;
    public bool Interacting => Active && phase == Phase.Menu;
    public Vector3? LocateCrystal(IslandCrystal crystal) => crystals.Locate(crystal);
    public string Status { get; private set; } = "尚未开始导航。";
    public string? CrystalDecision { get; private set; }
    public MapDestination? TakeArrival() { var result = arrival; arrival = null; return result; }
    public void Start(MapDestination destination, WorldContext world, Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now,
        bool summonMount = false, bool useCrystals = true, bool waitWithoutFate = true, float arrivalRadius = 3,
        bool unknownHeight = true, float nearbyGroundRadius = 25, bool preferCrystals = false, Func<Vector3, bool>? groundFilter = null,
        float? verifiedEndpointTolerance = null)
    {
        if (Active) { Status = "请先停止当前导航。"; return; }
        Stop(); CrystalDecision = null; goal = destination; context = world; origin = player; started = now;
        if (destination.Territory != world.Territory || destination.Map != world.Map || !NavigationRules.Finite(player))
        { Stop("目的地范围无效。"); return; }
        requireMount = summonMount; this.useCrystals = useCrystals; resumeFinal = false; travelMount.Reset();
        this.waitWithoutFate = waitWithoutFate;
        finalArrivalRadius = arrivalRadius;
        finalUnknownHeight = unknownHeight;
        finalNearbyGroundRadius = nearbyGroundRadius;
        finalGroundFilter = groundFilter;
        verifiedFinalEndpointTolerance = verifiedEndpointTolerance;
        this.preferCrystals = preferCrystals;
        Active = true;
        if (useCrystals && preferCrystals && mesh.SupportsRouteEstimates)
        {
            planner = new(mesh, world.Territory, player, destination.Position, now, nearbyGroundRadius, knownHeight: !unknownHeight);
            phase = Phase.Plan;
            Status = "正在用 vnavmesh 比较直达与水晶路线。";
            return;
        }
        StartPlannedJourney(player, fates, now);
    }

    public void StartUnspawnedFate(MapDestination centre, WorldContext world, Vector3 player, DateTimeOffset now,
        bool summonMount, bool useCrystals = true)
    {
        const float distance = 50;
        var stop = ArrivalRules.Position(player, centre.Position, distance);
        Start(centre with { Position = stop }, world, player, [], now, summonMount, useCrystals,
            waitWithoutFate: false, arrivalRadius: 1, preferCrystals: true,
            groundFilter: p => NavigationRules.Distance(p, centre.Position) >= distance - .05f);
    }

    private CrystalRoute? ChooseRoute(Vector3 player, IslandCrystal? source = null, IReadOnlySet<uint>? available = null)
        => measuredRoutes == null
            ? IslandCrystalRoutes.Choose(context.Territory, player, goal!.Position, source, available)
            : measuredRoutes.FirstOrDefault(r => (source == null || r.Source == source)
                && (available == null || available.Contains(r.Target.PlaceNameId)));

    private void StartPlannedJourney(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        if (useCrystals && preferCrystals
            && ChooseRoute(player) is { } nearby
            && NavigationRules.Distance(player, nearby.Source.Position) <= 3)
        {
            BeginJourney(player, fates, now);
            return;
        }
        if (requireMount) { phase = Phase.Mount; Status = "检查并召唤所选坐骑。"; return; }
        BeginJourney(player, fates, now);
    }
    private void BeginJourney(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        route = useCrystals ? ChooseRoute(player) : null;
        var estimate = measuredRoutes == null ? "平面估算" : "vnavmesh 路长";
        CrystalDecision = route == null ? $"{estimate}：水晶路线收益不足，直接前往。"
            : $"{estimate}：选择岛内水晶路线 {route.Source.PlaceNameId} → {route.Target.PlaceNameId}。";
        if (route == null) Final(player, fates, now);
        else BeginSource(player, fates, now);
    }
    public void StartSurveyLeg(IslandCrystal source, WorldContext world, Vector3 player,
        IReadOnlySet<uint> visited, DateTimeOffset now)
    {
        Stop();
        SurveyExhausted = false;
        if (source.Territory != world.Territory || !NavigationRules.Finite(player)) { Stop("巡查源水晶无效。"); return; }
        surveyVisited = visited;
        context = world;
        started = now;
        goal = new(world.Territory, world.Map, source.Position, "元灵巡查");
        route = new(source, source, 0);
        Active = true;
        BeginSource(player, [], now);
    }
    private void BeginSource(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        var source = route!.Source;
        phase = Phase.Source;
        var liveSource = crystals.Locate(source);
        if (liveSource is { } live ? Vector3.Distance(player, live) <= 4
            : NavigationRules.Distance(player, source.Position) <= 3 && (surveyVisited != null || preferCrystals))
        {
            EnterMenu(now);
            return;
        }
        var anchor = IslandCrystalRoutes.GroundAnchor(source, player, mesh, liveSource);
        if (anchor == null) { Fallback(player, fates, now, "水晶2米内没有可用的地面参考。"); return; }
        sourceAnchor = anchor;
        walk.Start(goal! with { Position = anchor.Value }, context, player, [], now, false, 2, unknownHeight: false,
            groundSearchRadius: IslandCrystalRoutes.GroundRadius, verifiedEndpointTolerance: .5f,
            groundFilter: p => IslandCrystalRoutes.NearGround(p, anchor.Value));
        if (walk.Active) Status = "前往岛内水晶。";
        else if (mesh.Ready && !mesh.Busy && mesh.Route.Count == 0) Fallback(player, fates, now, walk.Status);
        else Stop(walk.Status);
    }
    public void Tick(WorldContext world, Vector3 player, bool safe, bool transferScreen, bool mounted, bool casting,
        IReadOnlyList<FateEntry> fates, DateTimeOffset now, bool inCombat = false)
    {
        if (!Active) return;
        try
        {
            if (world != context || !safe && !(Transferring && transferScreen)) { Stop("角色或地图状态变化，已停止导航。"); return; }
            if (now - started >= TimeSpan.FromMinutes(5)) { Stop("导航超过5分钟，已停止。"); return; }
            if (phase == Phase.Plan)
            {
                if (planner != null)
                {
                    try
                    {
                        if (!planner.Tick(now)) return;
                        measuredRoutes = planner.Routes;
                    }
                    catch (Exception)
                    {
                        measuredRoutes = null;
                    }
                    planner.Dispose();
                    planner = null;
                    phaseAt = now;
                }
                if (mesh.Busy)
                {
                    if (now - phaseAt >= TimeSpan.FromSeconds(2)) Stop("vnavmesh 仍在规划，已停止岛内行程。");
                    return;
                }
                if (inCombat) BeginJourney(player, fates, now);
                else StartPlannedJourney(player, fates, now);
                return;
            }
            if (phase == Phase.Mount)
            {
                if (mesh.Busy || mesh.Route.Count > 0) { Stop("其他导航占用中，停止召唤坐骑。"); return; }
                if (inCombat)
                {
                    if (resumeFinal) Final(player, fates, now);
                    else if (combat != null) BeginJourney(player, fates, now);
                    else Fallback(player, fates, now, "战斗中无法上坐骑或传送。");
                    return;
                }
                var result = travelMount.Tick(mounted, casting, inCombat, now);
                if (result == TravelMountResult.Failed) { Stop("无法召唤所选坐骑，已停止导航。"); return; }
                if (result == TravelMountResult.Mounted)
                {
                    if (resumeFinal) Final(player, fates, now);
                    else BeginJourney(player, fates, now);
                    return;
                }
                Status = "等待所选坐骑召唤完成。";
                return;
            }
            if (phase == Phase.Transfer)
            {
                if (now - phaseAt >= TimeSpan.FromSeconds(45)) { Stop("未能确认岛内传送落地，已停止。"); return; }
                if (transferScreen || !safe || casting || NavigationRules.Distance(player, route!.Target.Position) > 15) return;
                if (now - phaseAt < TimeSpan.FromSeconds(1)) return;
                if (surveyVisited != null)
                {
                    var reached = goal! with { Position = route.Target.Position };
                    Stop("巡查水晶已落地。");
                    arrival = reached;
                    return;
                }
                PrepareFinal(player, fates, now); return;
            }
            if (phase == Phase.Source || phase == Phase.Final)
            {
                if (phase == Phase.Source && crystals.Locate(route!.Source) is { } observed
                    && sourceAnchor is { } previous && Vector3.Distance(observed, previous) > 1)
                {
                    walk.Stop();
                    if (mesh.Busy) { Status = "等待旧水晶路线释放后校正实际位置。"; return; }
                    BeginSource(player, fates, now);
                    return;
                }
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
                EnterMenu(now);
            }
            if (inCombat && surveyVisited == null)
            {
                if (combat == null) { Fallback(player, fates, now, "战斗中无法使用水晶传送。"); return; }
                if (crystalCombatAt == null)
                {
                    crystals.Cancel();
                    opened = false;
                    crystalCombatAt = now;
                    if (combat.BeginCrystalCombat(this) is { } error) { Stop(error); return; }
                }
                if (now - crystalCombatAt.Value >= TimeSpan.FromSeconds(60))
                { Stop("水晶前等待脱战超过 60 秒，已停止传送。"); return; }
                Status = "正在战斗，等待脱战后传送。";
                return;
            }
            if (crystalCombatAt != null)
            {
                if (!combat!.EndCrystalCombat(this))
                { Stop("未能关闭本次开启的自动循环，已停止传送。"); return; }
                crystalCombatAt = null;
                EnterMenu(now);
                return;
            }
            if (now - phaseAt >= TimeSpan.FromSeconds(15)) { Fallback(player, fates, now, "未在15秒内取得水晶传送菜单。"); return; }
            if (casting) return;
            if (mounted)
            {
                if (now < nextDismount) return;
                if (dismounts++ >= 3) { Stop("无法下坐骑进行水晶传送。"); return; }
                nextDismount = now.AddSeconds(3); mount.Dismount(); return;
            }
            if (!opened && !sourceReapproached && crystals.Locate(route!.Source) is { } liveSource
                && Vector3.Distance(player, liveSource) > 4)
            {
                sourceReapproached = true;
                BeginSource(player, fates, now);
                return;
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
            CrystalRoute? availableRoute;
            if (surveyVisited is { } visited)
            {
                var next = IslandCrystalRoutes.All.FirstOrDefault(c => c.Territory == world.Territory
                    && c != route!.Source && !visited.Contains(c.PlaceNameId) && choices.Contains(c.PlaceNameId));
                if (next == null)
                {
                    Stop("已巡查全部可用水晶。");
                    SurveyExhausted = true;
                    return;
                }
                availableRoute = new(route!.Source, next, 0);
            }
            else availableRoute = ChooseRoute(origin, route!.Source, choices);
            if (availableRoute == null) { Fallback(player, fates, now, "菜单中无已解锁且有收益的目标水晶。"); return; }
            route = availableRoute;
            if (!crystals.Select(route.Target)) { Fallback(player, fates, now, "未能唯一匹配并选择传送菜单条目。"); return; }
            phase = Phase.Transfer; phaseAt = now; Status = "等待岛内水晶传送落地。";
        }
        catch (Exception ex) { Stop("岛内导航失败：" + ex.Message); }
    }
    private void EnterMenu(DateTimeOffset now)
    {
        phase = Phase.Menu; phaseAt = now; dismounts = 0; nextDismount = now;
        Status = "准备与岛内水晶交互。";
    }
    private bool opened, sourceReapproached;
    private void Fallback(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now, string reason = "源水晶路径不可达。")
    {
        if (surveyVisited != null) { Stop(reason); return; }
        CrystalDecision = reason + "回退直接前往。"; crystals.Cancel(); PrepareFinal(player, fates, now);
    }
    private void PrepareFinal(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        if (!requireMount) { Final(player, fates, now); return; }
        resumeFinal = true; travelMount.Reset(); phase = Phase.Mount; Status = "传送后检查所选坐骑。";
    }
    private void Final(Vector3 player, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        phase = Phase.Final;
        walk.Start(goal!, context, player, fates, now, waitWithoutFate, finalArrivalRadius, finalUnknownHeight,
            nearbyGroundRadius: finalNearbyGroundRadius, groundFilter: finalGroundFilter, verifiedEndpointTolerance: verifiedFinalEndpointTolerance);
        if (!walk.Active) Stop(walk.Status); else Status = walk.Status;
    }
    public void Stop(string reason = "已停止导航。")
    {
        if (crystalCombatAt != null)
        {
            combat?.EndCrystalCombat(this);
            crystalCombatAt = null;
        }
        planner?.Dispose();
        planner = null;
        measuredRoutes = null;
        Active = false; arrival = null; opened = false; sourceReapproached = false; surveyVisited = null; walk.Stop();
        try { crystals.Cancel(); } catch { }
        Status = reason;
    }
    public void Dispose() { Stop(); walk.Dispose(); }
}
