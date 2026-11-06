using System.Numerics;

namespace ULKHelper.Automation;

internal enum TriggerMode { Normal, Recorded }
internal enum TriggerPull { Attack, PassBy, Passive }
internal sealed record TriggerPlan(ushort FateId, byte Level, MapDestination Centre, TriggerMode Mode, Vector3[] Route,
    TriggerPull? Pull = null, Vector3? StandbyPoint = null, bool GrindLight = false)
{
    public TriggerPull PullMethod => Pull ?? DefaultPull(Mode);
    public static TriggerPull DefaultPull(TriggerMode mode) => mode == TriggerMode.Recorded ? TriggerPull.PassBy : TriggerPull.Attack;
}
internal sealed record TriggerMob(ulong Id, ushort FateId, Vector3 Position, bool Dead, bool Aggro, bool Targetable = true, bool Engaged = false, uint Hp = 0,
    float HitboxRadius = 0, uint MaxHp = 0);
internal sealed record MobArea(ushort FateId, Vector3 Position);
internal sealed record TriggerRouteSet(int Selected, Vector3[][] Paths, string[]? Names = null)
{
    public const int MaxRoutes = 32;
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3[] Current => Selected >= 0 && Selected < Paths.Length ? Paths[Selected] : [];
    public string NameAt(int index) => Names != null && index < Names.Length && !string.IsNullOrWhiteSpace(Names[index])
        ? Names[index] : $"路线{index + 1}";
}
internal sealed record AutoTriggerSnapshot(bool Active, string Status, int? Level, IReadOnlyList<MobArea> Areas,
    IReadOnlyDictionary<ushort, TriggerRouteSet> Routes, IReadOnlyDictionary<ushort, Vector3>? StandbyPoints = null)
{
    public static readonly AutoTriggerSnapshot Empty = new(false, "尚未开始自动触发。", null, [], new Dictionary<ushort, TriggerRouteSet>());
    public Vector3[] SelectedRoute(ushort fateId) => Routes.GetValueOrDefault(fateId)?.Current ?? [];
    public Vector3? StandbyPoint(ushort fateId) => StandbyPoints?.TryGetValue(fateId, out var point) == true ? point : null;
}
internal sealed record TriggerFrame(WorldContext World, Vector3 Position, bool Safe, bool Mounted, bool Casting, bool InCombat,
    int? Level, IReadOnlyList<TriggerMob> Mobs, IReadOnlySet<ushort> ActiveFates, bool CanMountForTravel = false, bool Loading = false,
    bool RangedJob = false)
{
    public bool InAttackDismountRange(TriggerMob mob) =>
        Vector3.DistanceSquared(Position, mob.Position) <= (RangedJob ? 100 : 9);
}
internal interface ITriggerCombat
{
    string? Error => null;
    string? TriggerWarning => null;
    bool TriggerApproachAvailable => true;
    bool TriggerControlsAvailable => true;
    string? BeginTrigger(bool needsApproach, bool useRotation, bool useBmr, bool optionalControls = false) => null;
    void EndTrigger() { }
    void ReleasePullAttack() { }
    bool PrepareTarget(ulong id, bool fighting, IReadOnlyList<ulong>? eligible = null) => Target(id);
    string? PrepareStart();
    bool Target(ulong id);
    bool EnableStationary();
    bool EnableTrigger(bool approach) => EnableStationary();
    bool EnableRecordedPull() => EnableStationary();
    bool Disable();
    bool StationaryHealthy { get; }
}
internal static class AutoTriggerRules
{
    public const float PassiveStandbyRadius = 5;
    public const float LightStandbyRadius = 10;
    public const string PassByLevelWarning = "当前FATE触发等级过低，路过无法建立仇恨";
    public static bool CanPassBy(int? playerLevel, int fateLevel) =>
        playerLevel == null || playerLevel.Value - fateLevel < 2;

    public static Vector3 Backstep(Vector3 position, Vector3 goal, bool left)
    {
        var direction = goal - position;
        direction.Y = 0;
        if (direction.LengthSquared() < .01f) return position;
        var forward = Vector3.Normalize(direction);
        var side = new Vector3(-forward.Z, 0, forward.X);
        return position + (-forward + side * (left ? 1 : -1)) * MathF.Sqrt(2);
    }

    public static bool AttackMagia(int player,int mob,bool tank) => mob<player || mob<=player+2 && tank;
    public static Vector3? Ground(INavigationMesh mesh,Vector3 point,bool unknownHeight)
    {
        foreach (var candidate in NavigationGround.Candidates(mesh, point, unknownHeight)) return candidate;
        return null;
    }
    public static int Capacity(int player, IEnumerable<int> levels) => levels.Select(level => level < player ? 3 : level <= player + 2 ? 2 : 1).DefaultIfEmpty(3).Min();
    public static TriggerMode DefaultMode(ushort id) =>
        id is 1329 or 1338 or 1364 or 1365 or 1402 or 1403 or 1404 or 1418 or 1423
            ? TriggerMode.Recorded : TriggerMode.Normal;
    public static bool InCorridor(Vector3 point, Vector3 start, Vector3 end, float width)
    {
        var offset = new Vector2(point.X - start.X, point.Z - start.Z);
        var segment = new Vector2(end.X - start.X, end.Z - start.Z);
        var length = segment.LengthSquared();
        var t = length == 0 ? 0 : Math.Clamp(Vector2.Dot(offset, segment) / length, 0, 1);
        return Math.Abs(point.Y - (start.Y + (end.Y - start.Y) * t)) <= 8
            && Vector2.DistanceSquared(offset, segment * t) <= width * width / 4;
    }

    public static bool InRecordedRoute(Vector3 point, Vector3[] route)
    {
        for (var i = 1; i < route.Length; i++)
            if (InCorridor(point, route[i - 1], route[i], 15)) return true;
        return false;
    }
}

// Framework-driven, bounded session. All inputs are copied managed observations.
internal sealed class AutoTriggerHelper(INavigationMesh mesh, IArrivalActions mounts, ITriggerCombat combat, IIslandCrystalActions? crystals = null,
    Func<TriggerFrame, Vector3, TriggerMob?, bool>? useMobility = null)
{
    private readonly DestinationNavigationHelper move = new(mesh);
    private readonly IslandTravelHelper? islandTravel = crystals == null ? null : new(mesh, crystals, mounts, combat as ICrystalCombat);
    private readonly List<TriggerPlan> plans = [];
    private readonly Dictionary<ushort, int> kills = new();
    private readonly Dictionary<ulong, (TriggerMob Mob, DateTimeOffset Seen)> seen = new();
    private readonly HashSet<ulong> pulled = [];
    private readonly HashSet<ulong> waitingMobs = [];
    private readonly HashSet<ulong> covered = [];
    private Vector3 lastPullPosition;
    private int previousAggro;
    private bool recordedReady, useApproach;
    private Vector3[] recordedMovement = [];
    private DateTimeOffset recordedTagUntil;
    private bool recordedRouteInterrupted;
    private int recordedRestarts;
    private Vector3[] recoveryMovement = [];
    private Vector3? resumePullGoal;
    private Vector3 progressPosition;
    private DateTimeOffset progressAt, recoveryUntil;
    private DateTimeOffset nextMobility;
    private DateTimeOffset mobilityUntil;
    private Vector3? mobilityGoal;
    private int recoveryAttempts;
    private readonly Dictionary<ulong, TriggerMob> cache = new();
    private readonly Dictionary<ulong, DateTimeOffset> retryAfter = new();
    private const float PullRadius = 45;
    private readonly HashSet<ushort> appeared = [];
    private WorldContext world;
    private DateTimeOffset started, phaseAt, actionAt, targetAt;
    private DateTimeOffset targetProgressAt;
    private float targetDistance;
    private uint targetHp;
    private int pullLimit, killLimit, attempts;
    private ulong target, pullMoveTarget;
    private bool rotation, pullRotationOnly, walking, returnHome, finishPending;
    private readonly HashSet<ulong> passiveEngaged = [];
    private DateTimeOffset passiveLastAddition;
    private bool passiveParked, passiveSawCombat;
    private enum Phase { Search, Centre, Approach, Pull, Fight, Return, Wait, PassiveWait, PassiveFight }
    private Phase phase;
    public bool Active { get; private set; }
    public bool ObserveAllEngaged => Active && plans[0] is { PullMethod: TriggerPull.Passive, GrindLight: true };
    public bool Transferring => islandTravel?.Transferring == true;
    public bool Interacting => islandTravel?.Interacting == true;
    public string Status { get; private set; } = "尚未开始自动触发。";
    public bool Start(IReadOnlyList<TriggerPlan> requested, TriggerFrame frame, int batch, int maxKills, DateTimeOffset now,
        bool useRotation = true, bool useBmr = true)
    {
        if (Active) return false;
        if (frame.World.Territory is not (732 or 763 or 795 or 827) || !frame.Safe || frame.InCombat || frame.Level == null || requested.Count == 0 || requested.Count > 3
            || requested.Select(p => p.FateId).Distinct().Count() != requested.Count
            || requested.Any(p => p.Centre.Territory != frame.World.Territory || p.Centre.Map != frame.World.Map || !Enum.IsDefined(p.Mode)
                || !Enum.IsDefined(p.PullMethod) || !NavigationRules.Finite(p.Centre.Position))
            || requested.Count > AutoTriggerRules.Capacity(frame.Level.Value, requested.Select(p => (int)p.Level)))
        { Status = "无法开始：检查元素等级、选点数量、当前岛屿与脱战状态。"; return false; }
        if (requested.Any(p => p.PullMethod == TriggerPull.PassBy && !AutoTriggerRules.CanPassBy(frame.Level, p.Level)))
        { Status = AutoTriggerRules.PassByLevelWarning; return false; }
        if (requested.Any(p => p.PullMethod == TriggerPull.Passive &&
            (p.StandbyPoint is not { } point || !NavigationRules.Finite(point))))
        { Status = "请先设定被动拉怪的挂机点位。"; return false; }
        var hasPassive = requested.Any(p => p.PullMethod == TriggerPull.Passive);
        var onlyPassive = requested.All(p => p.PullMethod == TriggerPull.Passive);
        if (requested.Any(p => p.PullMethod != TriggerPull.Passive && p.Mode == TriggerMode.Recorded &&
            (p.Route.Length < 2 || p.Route.Length > 128 || p.Route.Any(v => !NavigationRules.Finite(v)))))
        { Status = "特殊触发需先记录本岛起点和终点（最多128点）。"; return false; }
        if (!onlyPassive && (!mesh.Ready || mesh.Busy || mesh.Route.Count > 0))
        { Status = "导航网格未就绪或正在使用。"; return false; }
        var error = combat.BeginTrigger(hasPassive || requested.Any(p => p.Mode == TriggerMode.Normal && p.PullMethod == TriggerPull.Attack),
            useRotation, useBmr, optionalControls: onlyPassive);
        error ??= combat.PrepareStart();
        if (error != null) { combat.EndTrigger(); Status = error; return false; }
        useApproach = useBmr && combat.TriggerApproachAvailable;
        appeared.Clear(); plans.Clear(); plans.AddRange(requested); kills.Clear(); seen.Clear();
        world = frame.World; started = now;
        pullLimit = batch == 21 ? int.MaxValue : Math.Clamp(batch, 1, 20);
        killLimit = maxKills == 7 ? int.MaxValue : Math.Clamp(maxKills, 1, 6) * 50;
        Active = true; rotation = pullRotationOnly = walking = returnHome = false;
        nextMobility = now;
        SelectNearest(frame.Position, now);
        Status = "自动触发已开始（会话上限2小时）。"; return true;
    }
    public void Stop(string reason = "已停止自动触发。")
    {
        var wasActive = Active;
        Active = false; StopTravel();
        if (wasActive) { if (rotation) combat.Disable(); combat.EndTrigger(); }
        rotation = walking = false;
        StopRecordedMovement(); waitingMobs.Clear(); pulled.Clear(); seen.Clear(); cache.Clear(); covered.Clear();
        Status = reason;
    }
    private void SelectNearest(Vector3 position, DateTimeOffset now)
    {
        var nearest = plans.MinBy(p => NavigationRules.Distance(position,
            p.PullMethod == TriggerPull.Passive ? p.StandbyPoint!.Value : p.Centre.Position))!;
        plans.Remove(nearest); plans.Insert(0, nearest);
        cache.Clear(); retryAfter.Clear(); pulled.Clear(); waitingMobs.Clear(); seen.Clear();
        target = pullMoveTarget = 0; finishPending = returnHome = false;
        recoveryAttempts = 0;
        recordedReady = true;
        ResetCoverage(position);
        if (nearest.PullMethod == TriggerPull.Passive) BeginPassiveWait(now);
        else SetPhase(nearest.Mode == TriggerMode.Recorded ? Phase.Search : Phase.Centre, now);
    }
    private void SetPhase(Phase value, DateTimeOffset now)
    {
        phase = value; phaseAt = now; attempts = 0; actionAt = default;
        progressAt = default;
        recoveryAttempts = 0;
    }
    private bool Riding(bool desired, TriggerFrame frame, DateTimeOffset now, bool optional = false)
    {
        if (frame.Mounted == desired) return true;
        if (frame.Casting || now < actionAt) return false;
        if (desired && frame.InCombat) return false;
        if (attempts >= 3)
        {
            if (optional) return true;
            Stop("骑乘状态切换失败（3次）。");
            return false;
        }
        attempts++;
        actionAt = now.AddSeconds(3); if (desired) mounts.Mount(); else mounts.Dismount(); return false;
    }
    private bool Go(Vector3 point, TriggerFrame frame, DateTimeOffset now, bool unknownHeight = false, bool pulling = false, bool preferCrystals = false)
    {
        var destination = new MapDestination(world.Territory, world.Map, point, "触发路线");
        if (!pulling && islandTravel != null && !frame.InCombat && (preferCrystals || IslandCrystalRoutes.Choose(world.Territory, frame.Position, point) != null))
        {
            islandTravel.Start(destination, world, frame.Position, [], now, waitWithoutFate: false, preferCrystals: preferCrystals);
            walking = islandTravel.Active;
            if (!walking) Stop(islandTravel.Status);
            return walking;
        }
        move.Start(destination, world, frame.Position, [], now, false,
            arrivalRadius: pulling ? 2 : 1, unknownHeight: unknownHeight, relaxedArrival: !pulling && plans[0].Mode == TriggerMode.Normal);
        walking = move.Active;
        if (!walking) Stop(move.Status);
        return walking;
    }
    private bool GoToPullMob(TriggerMob mob, TriggerFrame frame, DateTimeOffset now)
    {
        pullMoveTarget = mob.Id;
        progressAt = default;
        recoveryAttempts = 0;
        return Go(mob.Position, frame, now, pulling: true);
    }
    private bool StartRotation(ulong id, bool fighting, bool approach, IReadOnlyList<ulong>? eligible = null, bool recordedPull = false)
    {
        if (!combat.PrepareTarget(id, fighting, eligible)) { Stop("准备攻击目标失败：" + (combat.Error ?? "目标不可用或战斗设置已变化。")); return false; }
        if (!rotation)
        {
            pullRotationOnly = !fighting && (recordedPull || !approach);
            rotation = pullRotationOnly ? combat.EnableRecordedPull() : combat.EnableTrigger(approach);
            if (!rotation) Stop("无法开启战斗控制：" + (combat.Error ?? "RSR／BossMod未确认启用。"));
        }
        return rotation;
    }
    private bool EndRotation()
    {
        combat.ReleasePullAttack();
        if (rotation && !combat.Disable()) { Stop("等待自动循环关闭，请稍后重新开始。"); return false; }
        rotation = pullRotationOnly = false; return true;
    }
    private void StopRecordedMovement()
    {
        var issued = recordedMovement;
        recordedMovement = [];
        recordedTagUntil = default;
        recordedRouteInterrupted = false;
        recordedRestarts = 0;
        try { if (issued.Length != 0 && NavigationRules.OwnsRoute(mesh.Route, issued)) mesh.Stop(); }
        catch { /* The navigation provider can unload mid-route. */ }
    }

    private void StopTravel()
    {
        StopRecovery();
        resumePullGoal = null;
        mobilityGoal = null;
        mobilityUntil = default;
        move.Stop();
        if (islandTravel?.Active == true) islandTravel.Stop();
    }

    private void Fight(DateTimeOffset now)
    {
        StopTravel();
        StopRecordedMovement();
        walking = false;
        target = 0;
        if (!pullRotationOnly || EndRotation()) SetPhase(Phase.Fight, now);
    }

    private void BeginPassiveWait(DateTimeOffset now)
    {
        passiveParked = passiveSawCombat = false;
        passiveEngaged.Clear();
        passiveLastAddition = now;
        SetPhase(Phase.PassiveWait, now);
    }

    private bool PassiveNavigationReady()
    {
        try { return mesh.Ready; }
        catch { return false; }
    }

    private void TickPassive(TriggerPlan plan, TriggerFrame frame, TriggerMob[] living, DateTimeOffset now, bool navigationReady)
    {
        var candidates = plan.GrindLight ? frame.Mobs : living;
        var engaged = candidates.Where(m => !m.Dead && m.Engaged && (plan.GrindLight || m.Targetable)
            && Math.Abs(m.Position.Y - frame.Position.Y) <= 8
            && NavigationRules.Distance(m.Position, frame.Position) <= PullRadius)
            .DistinctBy(m => m.Id).OrderByDescending(m => m.Hp).ToArray();
        var eligible = engaged.Where(m => m.Targetable).Select(m => m.Id).ToArray();
        var mode = plan.GrindLight ? "被动拉怪 / 顺便刷光" : "被动拉怪";
        var label = $"{plan.Centre.Label} · {mode} · 击杀 {kills.GetValueOrDefault(plan.FateId)}";

        if (phase == Phase.PassiveFight)
        {
            passiveSawCombat |= frame.InCombat;
            if (!frame.InCombat && (passiveSawCombat || now - phaseAt >= TimeSpan.FromSeconds(8)))
            {
                if (!EndRotation()) return;
                pulled.Clear();
                if (finishPending) Advance(frame.Position, now);
                else BeginPassiveWait(now);
                return;
            }
            Status = $"{label} · 战斗中 · 已交战 {engaged.Length} 只";
            if (combat.TriggerControlsAvailable)
            {
                if (eligible.Length > 0) StartRotation(eligible[0], true, true, eligible);
                else EndRotation();
            }
            return;
        }

        if (finishPending && !frame.InCombat) { Advance(frame.Position, now); return; }
        var anchor = plan.StandbyPoint!.Value;
        var radius = plan.GrindLight ? AutoTriggerRules.LightStandbyRadius : AutoTriggerRules.PassiveStandbyRadius;
        bool InStandbyRange(Vector3 point) => NavigationRules.Distance(point, anchor) <= radius
            && Math.Abs(point.Y - anchor.Y) <= 3;
        if (!navigationReady)
        {
            if (walking) { move.Stop(); walking = false; }
            passiveParked = InStandbyRange(frame.Position);
            if (!passiveParked)
            {
                passiveEngaged.Clear();
                Status = $"{label} · 请手动走到记录点 {radius} 米内";
                return;
            }
        }
        if (!passiveParked)
        {
            Status = $"{label} · 前往 {radius} 米范围内的挂机点";
            if (!walking)
            {
                if (mesh.Busy || mesh.Route.Count > 0) { Status = $"{label} · 等待其他导航结束"; return; }
                if (!Riding(false, frame, now)) return;
                var angle = Random.Shared.NextSingle() * MathF.Tau;
                var distance = MathF.Sqrt(Random.Shared.NextSingle()) * radius;
                var point = anchor + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * distance;
                move.Start(plan.Centre with { Position = point }, world, frame.Position, [], now, false,
                    arrivalRadius: 1, unknownHeight: false,
                    groundFilter: InStandbyRange);
                if (!move.Active) { Stop(move.Status); return; }
                walking = true;
            }
            move.Tick(world, frame.Position, frame.Safe, [], now);
            if (move.Active) return;
            walking = false;
            if (move.TakeArrival() == null) { Stop(move.Status); return; }
            passiveParked = true;
        }

        if (engaged.Length == 0 || engaged.Any(m => !passiveEngaged.Contains(m.Id))) passiveLastAddition = now;
        passiveEngaged.Clear();
        passiveEngaged.UnionWith(engaged.Select(m => m.Id));
        var average = engaged.Length > 0 && engaged.All(m => m.MaxHp > 0)
            ? engaged.Average(m => Math.Min(1d, (double)m.Hp / m.MaxHp)) : (double?)null;
        Status = $"{label} · 挂机等交战 · 已交战 {engaged.Length} 只"
            + (average is { } hp ? $" · 平均血量 {hp:P0}" : "");
        if (combat.TriggerWarning is { } warning) Status += $" · {warning}";
        if (average is not < .95 || now - passiveLastAddition <= TimeSpan.FromSeconds(2)) return;
        if (!combat.TriggerControlsAvailable)
        {
            Status = $"{label} · 已满足接战条件，等待手动战斗";
            if (!frame.InCombat) return;
        }
        else if (eligible.Length == 0 || !Riding(false, frame, now) || !StartRotation(eligible[0], true, true, eligible))
            return;
        passiveSawCombat = frame.InCombat;
        SetPhase(Phase.PassiveFight, now);
    }

    public void Tick(TriggerFrame frame, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (now - started > TimeSpan.FromHours(2)) { Stop("自动触发达到2小时上限。"); return; }
            if (phase is not (Phase.PassiveWait or Phase.PassiveFight) && now - phaseAt > TimeSpan.FromMinutes(5))
            { Stop("当前触发阶段超过5分钟，已停止。"); return; }
            if (Transferring && frame.Loading) return;
            if (!frame.Safe || frame.World != world || frame.Level == null) { Stop("角色、地图或可操作状态变化，触发已停止。"); return; }
            var plan = plans[0];
            var passive = plan.PullMethod == TriggerPull.Passive;
            var navigationReady = passive ? PassiveNavigationReady() : mesh.Ready;
            if (!passive && !navigationReady || rotation && (phase is not (Phase.Fight or Phase.PassiveFight) || frame.InCombat) && !combat.StationaryHealthy)
            { Stop("导航或循环控制失效，已停止。"); return; }
            if (rotation && !walking && recordedMovement.Length == 0 && recoveryMovement.Length == 0
                && mobilityUntil == default && navigationReady && (mesh.Busy || mesh.Route.Count > 0))
            { Stop("检测到其他导航任务，已释放战斗控制。"); return; }
            if (plans.Count > AutoTriggerRules.Capacity(frame.Level.Value, plans.Select(p => (int)p.Level))) { Stop("元素等级变化，选择数量超过限制。"); return; }
            if (plan.PullMethod == TriggerPull.PassBy && !AutoTriggerRules.CanPassBy(frame.Level, plan.Level))
            { Stop(AutoTriggerRules.PassByLevelWarning); return; }
            var observed = frame.Mobs.Where(m => m.FateId == plan.FateId).ToArray();
            foreach (var mob in observed)
            {
                if (seen.TryGetValue(mob.Id, out var old) && !old.Mob.Dead && mob.Dead && old.Mob.FateId == mob.FateId
                    && now - old.Seen <= TimeSpan.FromSeconds(2) && (pulled.Contains(mob.Id) || old.Mob.Aggro))
                    kills[mob.FateId] = kills.GetValueOrDefault(mob.FateId) + 1;
            }
            seen.Clear(); foreach (var mob in observed) seen[mob.Id] = (mob, now);
            appeared.UnionWith(frame.ActiveFates);
            finishPending |= appeared.Contains(plan.FateId) || kills.GetValueOrDefault(plan.FateId) >= killLimit;
            var living = observed.Where(m => !m.Dead).ToArray();
            foreach (var mob in living.Where(m => m.Aggro)) pulled.Add(mob.Id);
            if (passive) { TickPassive(plan, frame, living, now, navigationReady); return; }
            RefreshCache(plan, living, now);
            var phaseText = phase switch { Phase.Centre => "前往FATE中心", Phase.Approach => "前往下一只触发怪", Phase.Search => "前往记录路线", Phase.Pull => "拉怪", Phase.Fight => "击杀", Phase.Return => "返回起点", _ => "等待刷新" };
            var coverage = plan.Mode == TriggerMode.Recorded ? $"沿线可拉 {cache.Count}（出发需 3 只）"
                : $"缓存 {cache.Count} · 已覆盖 {covered.Count}/{(pullLimit == int.MaxValue ? "∞" : pullLimit)}";
            Status = $"{plan.Centre.Label} · {phaseText} · {coverage} · 已拉 {pulled.Count} · 本次观测击杀 {kills.GetValueOrDefault(plan.FateId)}/{(killLimit == int.MaxValue ? "∞" : killLimit)}";
            if (combat.TriggerWarning is { } warning) Status += $" · {warning}";
            if (finishPending && phase != Phase.Fight && !(plan.Mode == TriggerMode.Recorded && phase == Phase.Pull))
            { if (frame.InCombat) Fight(now); else { Advance(frame.Position, now); return; } }
            if (phase == Phase.Centre && !Transferring && cache.Count >= pullLimit)
            {
                StopTravel();
                walking = false;
                ResetCoverage(frame.Position);
                SetPhase(Phase.Approach, now);
            }
            if (phase == Phase.Pull)
            {
                if (ResumeAfterMobility(frame, now)) return;
                if (plan.Mode == TriggerMode.Normal && TickPullRecovery(plan, frame, living, now)) return;
                if (TryPullMobility(plan, frame, now)) return;
            }
            if (phase == Phase.Approach && !Transferring && !frame.InCombat && living.Any(m => CanPull(m, plan, frame.Position, now)))
            {
                StopTravel();
                walking = false;
                ResetCoverage(frame.Position);
                SetPhase(Phase.Pull, now);
            }
            if (walking)
            {
                if (phase == Phase.Pull && plan.Mode == TriggerMode.Normal && RedirectPullMove(plan, frame))
                {
                    StopTravel();
                    walking = false;
                    pullMoveTarget = 0;
                    combat.ReleasePullAttack();
                    target = 0;
                }
                else if (islandTravel?.Active == true)
                {
                    islandTravel.Tick(world, frame.Position, frame.Safe, frame.Loading, frame.Mounted, frame.Casting, [], now, frame.InCombat);
                    if (islandTravel.Active) return;
                    walking = false;
                    if (islandTravel.TakeArrival() == null) { Stop(islandTravel.Status); return; }
                    if (phase is Phase.Centre or Phase.Approach) { ResetCoverage(frame.Position); SetPhase(Phase.Pull, now); }
                }
                else
                {
                    move.Tick(world, frame.Position, frame.Safe, [], now);
                    if (!move.Active)
                    {
                        walking = false;
                        if (move.TakeArrival() == null) { Stop(move.Status); return; }
                        if (phase is Phase.Centre or Phase.Approach) { ResetCoverage(frame.Position); SetPhase(Phase.Pull, now); }
                    }
                }
            }
            if (phase == Phase.Fight)
            {
                if (!Riding(false, frame, now)) return;
                var eligible = living.Where(m => m.Targetable && m.Aggro)
                    .OrderByDescending(m => m.Hp).ThenBy(m => Vector3.DistanceSquared(frame.Position, m.Position)).ToArray();
                if (frame.InCombat && eligible.Length > 0)
                {
                    StartRotation(eligible[0].Id, true, plan.Mode == TriggerMode.Normal && plan.PullMethod == TriggerPull.Attack, eligible.Select(m => m.Id).ToArray());
                    return;
                }
                if (frame.InCombat) { EndRotation(); return; }
                if (!EndRotation()) return;
                if (plan.PullMethod == TriggerPull.PassBy)
                    foreach (var id in covered)
                        if (!pulled.Contains(id)) retryAfter[id] = now.AddSeconds(15);
                pulled.Clear(); target = 0;
                ResetCoverage(frame.Position);
                if (finishPending) { Advance(frame.Position, now); return; }
                if (plan.Mode == TriggerMode.Recorded) { returnHome = true; SetPhase(Phase.Return, now); }
                else SetPhase(Phase.Approach, now);
                return;
            }
            if (phase == Phase.Return)
            {
                if (!TravelRiding(plan, frame, now)) return;
                if (returnHome) { returnHome = false; Go(plan.Route[0], frame, now); return; }
                if (!walking)
                {
                    waitingMobs.Clear();
                    waitingMobs.UnionWith(cache.Keys);
                    recordedReady = false;
                    SetPhase(Phase.Wait, now);
                }
                return;
            }
            if (plan.Mode == TriggerMode.Recorded)
            {
                if (phase is Phase.Search or Phase.Wait)
                {
                    if (Vector3.Distance(frame.Position, plan.Route[0]) > 2)
                    {
                        if (!walking && TravelRiding(plan, frame, now)) Go(plan.Route[0], frame, now);
                        return;
                    }
                    if (walking) return;
                    waitingMobs.IntersectWith(cache.Keys);
                    recordedReady |= cache.Keys.Any(id => !waitingMobs.Contains(id));
                    if (!recordedReady || cache.Count < 3)
                    {
                        if (frame.InCombat) Fight(now);
                        else if (phase != Phase.Wait) SetPhase(Phase.Wait, now);
                        return;
                    }
                    if (plan.PullMethod == TriggerPull.Attack ? !Riding(false, frame, now) : !TravelRiding(plan, frame, now))
                        return;
                    if (mesh.Busy || mesh.Route.Count > 0) { Stop("已有其他移动任务，无法开始记录路线。"); return; }
                    recordedMovement = plan.Route[1..];
                    mesh.Move(recordedMovement.ToList());
                    SetPhase(Phase.Pull, now);
                    return;
                }
                if (recordedTagUntil != default)
                {
                    TickRecordedTag(plan, frame, now);
                    return;
                }
                var remaining = mesh.Route;
                if (WaitForRecordedRoute(plan, remaining)) return;
                if (remaining.Count <= 1 && Vector3.Distance(frame.Position, plan.Route[^1]) <= 2) Fight(now);
                else if (remaining.Count == 0)
                {
                    if (recordedRestarts++ >= 2) Stop("预设路线恢复两次后仍提前结束。");
                    else mesh.Move(recordedMovement.ToList());
                }
                else if (plan.PullMethod == TriggerPull.Attack)
                {
                    var candidate = NearestPullMob(frame.Position, false);
                    if (candidate != null)
                    {
                        if (!ReadyForPullAttack(candidate, frame, now)) return;
                        if (!StartRotation(candidate.Id, false, false, recordedPull: true)) return;
                        if (InRecordedAttackRange(frame, candidate))
                        {
                            target = candidate.Id;
                            recordedMovement = remaining.ToArray();
                            mesh.Stop();
                            recordedTagUntil = now.AddSeconds(3);
                        }
                    }
                    else combat.ReleasePullAttack();
                }
                if (remaining.Count > 0 && remaining.Count < recordedMovement.Length)
                {
                    recordedMovement = remaining.ToArray();
                    recordedRestarts = 0;
                }
                return;
            }
            TickNormal(plan, frame, living, now);
        }
        catch (Exception ex) { Stop("自动触发失败：" + ex.Message); }
    }
    private bool TryPullMobility(TriggerPlan plan, TriggerFrame frame, DateTimeOffset now)
    {
        if (useMobility == null || frame.Mounted || frame.Casting || now < nextMobility || mesh.Busy
            || recordedTagUntil != default || recordedRouteInterrupted) return false;
        var route = mesh.Route;
        if (route.Count > 0 && (move.OwnsMovement || NavigationRules.OwnsRoute(route, recordedMovement)))
        {
            nextMobility = now.AddMilliseconds(750);
            if (!useMobility(frame, route[0], null)) return false;
            mobilityGoal = move.Target;
            if (recordedMovement.Length > 0) recordedMovement = route.ToArray();
        }
        else if (route.Count == 0 && plan.Mode == TriggerMode.Normal && plan.PullMethod == TriggerPull.Attack
            && rotation && useApproach && cache.TryGetValue(target, out var mob) && !frame.InAttackDismountRange(mob))
        {
            nextMobility = now.AddMilliseconds(750);
            if (!useMobility(frame, mob.Position, mob)) return false;
        }
        else return false;
        mobilityUntil = now.AddMilliseconds(750);
        return true;
    }

    private bool ResumeAfterMobility(TriggerFrame frame, DateTimeOffset now)
    {
        if (mobilityUntil == default) return false;
        if (now < mobilityUntil || mesh.Busy) return true;
        var route = mesh.Route;
        if (route.Count > 0 && !move.OwnsMovement && !NavigationRules.OwnsRoute(route, recordedMovement)) return true;
        mobilityUntil = default;
        nextMobility = now.AddMilliseconds(750);
        progressAt = default;
        targetProgressAt = now;
        var goal = mobilityGoal;
        mobilityGoal = null;
        if (route.Count > 0)
        {
            if (!NavigationRules.OwnsRoute(route, recordedMovement) || route.Count <= 1
                || Vector3.Distance(frame.Position, route[0]) > 2) return false;
            recordedMovement = route.Skip(1).ToArray();
            mesh.Stop();
            mesh.Move(recordedMovement.ToList());
            return true;
        }
        if (goal is { } destination && Vector3.Distance(frame.Position, destination) > 2)
        {
            move.Stop();
            Go(destination, frame, now, pulling: true);
            return true;
        }
        while (recordedMovement.Length > 1 && Vector3.Distance(frame.Position, recordedMovement[0]) <= 2)
            recordedMovement = recordedMovement[1..];
        if (recordedMovement.Length > 0 && Vector3.Distance(frame.Position, recordedMovement[^1]) > 2)
        {
            mesh.Move(recordedMovement.ToList());
            return true;
        }
        return false;
    }
    private bool WaitForRecordedRoute(TriggerPlan plan, IReadOnlyList<Vector3> remaining, bool tagging = false)
    {
        var busy = mesh.Busy;
        if (busy || remaining.Count > 0 && (tagging || !NavigationRules.OwnsRoute(remaining, recordedMovement)))
        {
            if (!recordedRouteInterrupted)
            {
                if (!EndRotation()) return true;
                recordedRouteInterrupted = true;
            }
            Status = $"{plan.Centre.Label} · 等待其他移动结束后恢复预设路线（寻路={busy}，路线点={remaining.Count}）。";
            return true;
        }
        if (!recordedRouteInterrupted) return false;
        recordedRouteInterrupted = false;
        recordedTagUntil = default;
        target = 0;
        if (remaining.Count == 0) mesh.Move(recordedMovement.ToList());
        Status = $"{plan.Centre.Label} · 已恢复预设路线。";
        return true;
    }

    private void TickRecordedTag(TriggerPlan plan, TriggerFrame frame, DateTimeOffset now)
    {
        if (WaitForRecordedRoute(plan, mesh.Route, tagging: true)) return;
        if (cache.ContainsKey(target) && now < recordedTagUntil)
        {
            Status = $"{plan.Centre.Label} · 等待目标建立仇恨。";
            StartRotation(target, false, false, recordedPull: true);
            return;
        }
        if (cache.Remove(target)) retryAfter[target] = now.AddSeconds(30);
        combat.ReleasePullAttack();
        target = 0;
        var nearest = NearestPullMob(frame.Position, false);
        if (nearest != null && InRecordedAttackRange(frame, nearest))
        {
            target = nearest.Id;
            recordedTagUntil = now.AddSeconds(3);
            StartRotation(target, false, false, recordedPull: true);
            return;
        }
        recordedTagUntil = default;
        mesh.Move(recordedMovement.ToList());
    }

    private static bool InRecordedAttackRange(TriggerFrame frame, TriggerMob mob) =>
        Vector3.Distance(frame.Position, mob.Position) <= (frame.RangedJob ? 10 : 3) + mob.HitboxRadius;
    private void StopRecovery()
    {
        var issued = recoveryMovement;
        recoveryMovement = [];
        try { if (issued.Length != 0 && NavigationRules.OwnsRoute(mesh.Route, issued)) mesh.Stop(); }
        catch { /* The navigation provider can unload mid-route. */ }
    }

    private bool TickPullRecovery(TriggerPlan plan, TriggerFrame frame, TriggerMob[] living, DateTimeOffset now)
    {
        if (recoveryMovement.Length != 0)
        {
            var route = mesh.Route;
            if (mesh.Busy || route.Count > 0 && !NavigationRules.OwnsRoute(route, recoveryMovement))
            {
                Status = $"{plan.Centre.Label} · 等待其他移动结束后继续原目标。";
                return true;
            }
            if (route.Count > 0 && now < recoveryUntil
                && NavigationRules.Distance(frame.Position, recoveryMovement[0]) > .5f)
            {
                Status = $"{plan.Centre.Label} · 正在后撤脱卡。";
                return true;
            }
            StopRecovery();
            progressAt = default;
            lastPullPosition = frame.Position;
            var goal = resumePullGoal;
            resumePullGoal = null;
            if (goal is { } waypoint) return Go(waypoint, frame, now, pulling: true) || !Active;
            targetProgressAt = now;
            return false;
        }

        var current = rotation && useApproach && !frame.Mounted
            ? living.FirstOrDefault(m => m.Id == target && CanPull(m, plan, frame.Position, now))
            : null;
        var movingWithMesh = walking && move.OwnsMovement;
        var goalPosition = movingWithMesh ? move.Target
            : current != null && !frame.InAttackDismountRange(current) ? current.Position : (Vector3?)null;
        if (frame.Casting || goalPosition == null)
        {
            progressAt = default;
            return false;
        }
        if (progressAt == default || NavigationRules.Distance(progressPosition, frame.Position) >= 1)
        {
            progressPosition = frame.Position;
            progressAt = now;
            return false;
        }
        if (now - progressAt < TimeSpan.FromSeconds(2) || recoveryAttempts >= 2) return false;

        recoveryAttempts++;
        progressAt = now;
        var heading = movingWithMesh && NavigationRules.Distance(frame.Position, mesh.Route[0]) >= .5f
            ? mesh.Route[0] : goalPosition.Value;
        var left = Random.Shared.Next(2) == 0;
        var waypointCandidate = RecoveryPoint(frame.Position, heading, left) ?? RecoveryPoint(frame.Position, heading, !left);
        if (waypointCandidate is not { } point) return false;
        if (movingWithMesh)
        {
            resumePullGoal = move.Target;
            move.Stop();
            walking = false;
        }
        else if (mesh.Busy || mesh.Route.Count > 0 || !EndRotation()) return !Active;
        recoveryMovement = [point];
        recoveryUntil = now.AddSeconds(2);
        mesh.Move([point]);
        Status = $"{plan.Centre.Label} · 移动不足 1 米，后撤脱卡。";
        return true;
    }

    private Vector3? RecoveryPoint(Vector3 position, Vector3 heading, bool left)
    {
        var desired = AutoTriggerRules.Backstep(position, heading, left);
        var grounded = mesh.Ground(desired, false);
        return grounded is { } point && NavigationRules.Finite(point)
            && Vector3.Distance(point, desired) <= .75f && NavigationRules.Distance(position, point) >= 1
            ? point : null;
    }
    private void ResetCoverage(Vector3 position)
    {
        covered.Clear();
        previousAggro = 0;
        lastPullPosition = position;
    }

    private bool TravelRiding(TriggerPlan plan, TriggerFrame frame, DateTimeOffset now) =>
        plan.PullMethod == TriggerPull.Attack || frame.InCombat || !frame.CanMountForTravel
        || Riding(true, frame, now, optional: true);

    private bool ReadyForPullAttack(TriggerMob mob, TriggerFrame frame, DateTimeOffset now) =>
        !frame.Mounted || frame.InAttackDismountRange(mob) && Riding(false, frame, now);

    private bool InArea(Vector3 position, TriggerPlan plan) => plan.Mode != TriggerMode.Recorded
        || phase == Phase.Pull && plan.PullMethod == TriggerPull.Attack || AutoTriggerRules.InRecordedRoute(position, plan.Route);

    private void RefreshCache(TriggerPlan plan, IReadOnlyList<TriggerMob> living, DateTimeOffset now)
    {
        cache.Clear();
        foreach (var mob in living)
            if (IsPullTarget(mob, plan, now)) cache[mob.Id] = mob;
    }

    private bool IsPullTarget(TriggerMob mob, TriggerPlan plan, DateTimeOffset now) =>
        mob.Targetable && !mob.Aggro && !mob.Engaged && !pulled.Contains(mob.Id)
        && retryAfter.GetValueOrDefault(mob.Id) <= now
        && NavigationRules.Finite(mob.Position) && InArea(mob.Position, plan);

    private bool CanPull(TriggerMob mob, TriggerPlan plan, Vector3 position, DateTimeOffset now) =>
        IsPullTarget(mob, plan, now) && NavigationRules.Distance(position, mob.Position) <= PullRadius;

    private TriggerMob? NearestPullMob(Vector3 position, bool passBy) => cache.Values
        .Where(m => (!passBy || !covered.Contains(m.Id)) && NavigationRules.Distance(position, m.Position) <= PullRadius)
        .MinBy(m => NavigationRules.Distance(position, m.Position));

    private static bool IsCloser(TriggerMob candidate, TriggerMob current, Vector3 position) =>
        candidate.Id != current.Id && NavigationRules.Distance(position, candidate.Position) + 1
        <= NavigationRules.Distance(position, current.Position);

    private bool RedirectPullMove(TriggerPlan plan, TriggerFrame frame)
    {
        var passBy = plan.PullMethod == TriggerPull.PassBy;
        var nearest = NearestPullMob(frame.Position, passBy);
        if (!cache.TryGetValue(pullMoveTarget, out var current)) return true;
        return nearest != null && (IsCloser(nearest, current, frame.Position)
            || !passBy && frame.Mounted && frame.InAttackDismountRange(nearest));
    }

    private void TickNormal(TriggerPlan plan, TriggerFrame frame, TriggerMob[] living, DateTimeOffset now)
    {
        var passBy = plan.PullMethod == TriggerPull.PassBy;
        if (phase == Phase.Centre)
        {
            if (walking) return;
            if (!frame.InCombat && frame.CanMountForTravel && NavigationRules.Distance(frame.Position, plan.Centre.Position) > 5
                && !Riding(true, frame, now, optional: true)) return;
            Go(plan.Centre.Position, frame, now, true, preferCrystals: true);
            return;
        }
        if (phase == Phase.Approach)
        {
            if (walking) return;
            if (frame.InCombat) { Fight(now); return; }
            var next = living.Where(m => IsPullTarget(m, plan, now))
                .MinBy(m => NavigationRules.Distance(frame.Position, m.Position));
            if (next == null) SetPhase(Phase.Wait, now);
            else if (!frame.CanMountForTravel || Riding(true, frame, now, optional: true))
                Go(next.Position, frame, now);
            return;
        }
        if (phase == Phase.Wait)
        {
            if (frame.InCombat) { Fight(now); return; }
            if (living.Any(m => IsPullTarget(m, plan, now)))
            {
                SetPhase(Phase.Approach, now);
            }
            return;
        }
        var aggro = living.Count(m => m.Aggro);
        if (aggro < previousAggro)
        {
            Fight(now);
            Status = $"{plan.Centre.Label} · 仇恨减少，结束拉怪。";
            return;
        }
        previousAggro = aggro;
        if (frame.Position != lastPullPosition)
            foreach (var mob in cache.Values)
                if (AutoTriggerRules.InCorridor(mob.Position, lastPullPosition, frame.Position, 5)) covered.Add(mob.Id);
        lastPullPosition = frame.Position;
        covered.UnionWith(pulled);
        if (covered.Count >= pullLimit && (passBy || target == 0 || pulled.Contains(target)))
        { Fight(now); return; }
        if (walking) return;
        var candidate = NearestPullMob(frame.Position, passBy);
        if (passBy && target != 0)
        {
            if (cache.TryGetValue(target, out var passed)
                && AutoTriggerRules.InCorridor(passed.Position, frame.Position, frame.Position, 5))
                covered.Add(target);
            if (!covered.Contains(target))
            {
                cache.Remove(target);
                retryAfter[target] = now.AddSeconds(15);
            }
            target = 0;
            if (covered.Count >= pullLimit) { Fight(now); return; }
        }
        if (target != 0)
        {
            if (!cache.TryGetValue(target, out var current) || !CanPull(current, plan, frame.Position, now)
                || candidate != null && !frame.InAttackDismountRange(current) && IsCloser(candidate, current, frame.Position))
            {
                combat.ReleasePullAttack();
                target = 0;
                progressAt = default;
                recoveryAttempts = 0;
            }
            else
            {
                if (!Riding(false, frame, now)) return;
                var distance = NavigationRules.Distance(frame.Position, current.Position);
                if (distance <= targetDistance - 1 || current.Hp < targetHp)
                {
                    targetProgressAt = now;
                    targetDistance = distance;
                    targetHp = current.Hp;
                }
                if (now - targetAt >= TimeSpan.FromSeconds(25) || now - targetProgressAt >= TimeSpan.FromSeconds(6))
                {
                    retryAfter[target] = now.AddSeconds(30);
                    cache.Remove(target);
                    Status = $"跳过拉怪无进展的目标 {target}（距离 {distance:0.0} 米），继续尝试本波。";
                    combat.ReleasePullAttack();
                    target = 0;
                    progressAt = default;
                    recoveryAttempts = 0;
                    return;
                }
                StartRotation(current.Id, false, useApproach);
                return;
            }
        }

        candidate = NearestPullMob(frame.Position, passBy);
        if (candidate != null)
        {
            if (frame.Casting && !rotation || !TravelRiding(plan, frame, now)) return;
            if (!passBy && !ReadyForPullAttack(candidate, frame, now))
            {
                if (!frame.InAttackDismountRange(candidate)) GoToPullMob(candidate, frame, now);
                return;
            }
            target = candidate.Id;
            progressAt = default;
            recoveryAttempts = 0;
            if (passBy)
            {
                if (NavigationRules.Distance(frame.Position, candidate.Position) > 2)
                    GoToPullMob(candidate, frame, now);
                return;
            }
            targetAt = targetProgressAt = now;
            targetDistance = NavigationRules.Distance(frame.Position, candidate.Position);
            targetHp = candidate.Hp;
            if (!useApproach && targetDistance > 2.5f && !GoToPullMob(candidate, frame, now)) return;
            StartRotation(target, false, useApproach);
            return;
        }
        if (frame.InCombat || covered.Count > 0)
        {
            Fight(now);
            Status = $"{plan.Centre.Label} · 附近暂无可拉目标，开始击杀。";
            return;
        }
        if (EndRotation()) SetPhase(Phase.Wait, now);
    }

    private void Advance(Vector3 position, DateTimeOffset now)
    {
        StopTravel(); walking = false;
        plans.RemoveAt(0);
        if (plans.Count == 0) { Stop("计划已结束：FATE已出现或达到放弃数量，原地待机。"); return; }
        if (!EndRotation()) return;
        SelectNearest(position, now);
    }
}
