using System.Numerics;

namespace ULKHelper.Automation;

internal enum TriggerMode { Normal, Recorded }
internal sealed record TriggerPlan(ushort FateId, byte Level, MapDestination Centre, TriggerMode Mode, Vector3[] Route);
internal sealed record TriggerMob(ulong Id, ushort FateId, Vector3 Position, bool Dead, bool Aggro, bool Targetable = true, bool Engaged = false, uint Hp = 0);
internal sealed record MobArea(ushort FateId, Vector3 Position);
internal sealed record TriggerRouteSet(int Selected, Vector3[][] Paths, string[]? Names = null)
{
    public const int MaxRoutes = 32;
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3[] Current => Selected >= 0 && Selected < Paths.Length ? Paths[Selected] : [];
    public string NameAt(int index) => Names != null && index < Names.Length && !string.IsNullOrWhiteSpace(Names[index])
        ? Names[index] : $"路线{index + 1}";
}
internal sealed record AutoTriggerSnapshot(bool Active, string Status, int? Level, IReadOnlyList<MobArea> Areas, IReadOnlyDictionary<ushort, TriggerRouteSet> Routes)
{
    public static readonly AutoTriggerSnapshot Empty = new(false, "尚未开始自动触发。", null, [], new Dictionary<ushort, TriggerRouteSet>());
    public Vector3[] SelectedRoute(ushort fateId) => Routes.GetValueOrDefault(fateId)?.Current ?? [];
}
internal sealed record TriggerFrame(WorldContext World, Vector3 Position, bool Safe, bool Mounted, bool Casting, bool InCombat,
    int? Level, IReadOnlyList<TriggerMob> Mobs, IReadOnlySet<ushort> ActiveFates, bool CanMountForTravel = false, bool Loading = false);
internal interface ITriggerCombat
{
    string? Error => null;
    string? BeginTrigger(bool needsApproach, bool useRotation, bool useBmr) => null;
    void EndTrigger() { }
    bool PrepareTarget(ulong id, bool fighting, IReadOnlyList<ulong>? eligible = null) => Target(id);
    string? PrepareStart();
    bool Target(ulong id);
    bool EnableStationary();
    bool EnableTrigger(bool approach) => EnableStationary();
    bool Disable();
    bool StationaryHealthy { get; }
}
internal static class AutoTriggerRules
{
    public static bool AttackMagia(int player,int mob,bool tank) => mob<player || mob<=player+2 && tank;
    public static Vector3? Ground(INavigationMesh mesh,Vector3 point,bool unknownHeight)
    {
        for(var i=0;i<17;i++)
        {
            var radius=i==0?0:i<=8?2:4;
            var angle=(i-1)%8*MathF.PI/4;
            var sample=point+new Vector3(MathF.Cos(angle)*radius,0,MathF.Sin(angle)*radius);
            var result=mesh.Ground(sample,unknownHeight);
            if(result is {} p && NavigationRules.Finite(p) && NavigationRules.Distance(point,p)<=5
                && (unknownHeight || Math.Abs(p.Y-point.Y)<=8))return p;
        }
        return null;
    }
    public static int Capacity(int player, IEnumerable<int> levels) => levels.Select(level => level < player ? 3 : level <= player + 2 ? 2 : 1).DefaultIfEmpty(3).Min();
    public static TriggerMode DefaultMode(ushort id) =>
        id is 1329 or 1338 or 1364 or 1365 or 1402 or 1403 or 1404 or 1418 or 1423
            ? TriggerMode.Recorded : TriggerMode.Normal;
    public static IEnumerable<int> WaveSizes(int count, int limit)
    {
        if (count <= 0 || limit <= 0) yield break;
        var waves = 1 + (count - 1) / limit;
        var size = count / waves;
        var remainder = count % waves;
        for (var i = 0; i < waves; i++)
            yield return size + (i >= waves - remainder ? 1 : 0);
    }
    // Fast open-path heuristic, not an exact travelling-salesman solution or navmesh travel-time estimate.
    public static List<Vector3> Order(Vector3 start, IEnumerable<Vector3> points, int maxPoints = 128)
    {
        var remaining = points.Where(NavigationRules.Finite).Distinct().Take(maxPoints).ToList();
        var result = new List<Vector3>(); var cursor = start;
        while (remaining.Count > 0)
        { var next = remaining.MinBy(p => Vector3.DistanceSquared(cursor, p)); result.Add(next); remaining.Remove(next); cursor = next; }
        for (var pass = 0; pass < 3; pass++)
        for (var i = 0; i < result.Count - 1; i++)
        for (var j = i + 1; j < result.Count; j++)
        {
            var before = i == 0 ? start : result[i - 1];
            var old = Vector3.Distance(before, result[i]) + (j + 1 < result.Count ? Vector3.Distance(result[j], result[j + 1]) : 0);
            var changed = Vector3.Distance(before, result[j]) + (j + 1 < result.Count ? Vector3.Distance(result[i], result[j + 1]) : 0);
            if (changed + .1f < old) result.Reverse(i, j - i + 1);
        }
        return result;
    }
}

// Framework-driven, bounded session. All inputs are copied managed observations.
internal sealed class AutoTriggerHelper(INavigationMesh mesh, IArrivalActions mounts, ITriggerCombat combat, IIslandCrystalActions? crystals = null)
{
    private readonly DestinationNavigationHelper move = new(mesh);
    private readonly IslandTravelHelper? islandTravel = crystals == null ? null : new(mesh, crystals, mounts);
    private readonly List<TriggerPlan> plans = [];
    private readonly Dictionary<ushort, int> kills = new();
    private readonly Dictionary<ulong, (TriggerMob Mob, DateTimeOffset Seen)> seen = new();
    private readonly HashSet<ulong> pulled = [];
    private readonly HashSet<ulong> waitingMobs = [];
    private Vector3[] recordedMovement = [];
    private readonly Dictionary<ulong, TriggerMob> cache = new();
    private readonly Dictionary<ulong, DateTimeOffset> retryAfter = new();
    private readonly Queue<ulong[]> waves = new();
    private ulong[] wave = [];
    private const float CacheRadius = 100;
    private const float PullRadius = 45;
    private readonly HashSet<ushort> appeared = [];
    private WorldContext world;
    private DateTimeOffset started, phaseAt, actionAt, nextCache, targetAt;
    private DateTimeOffset targetProgressAt;
    private float targetDistance;
    private uint targetHp;
    private int pullLimit, killLimit, attempts;
    private ulong target;
    private bool rotation, walking, returnHome, finishPending;
    private enum Phase { Search, Centre, Approach, Pull, Fight, Return, Wait }
    private Phase phase;
    public bool Active { get; private set; }
    public bool Transferring => islandTravel?.Transferring == true;
    public bool Interacting => islandTravel?.Interacting == true;
    public string Status { get; private set; } = "尚未开始自动触发。";
    public bool Start(IReadOnlyList<TriggerPlan> requested, TriggerFrame frame, int batch, int maxKills, DateTimeOffset now,
        bool useRotation = true, bool useBmr = true)
    {
        if (Active) return false;
        if (frame.World.Territory is not (732 or 763 or 795 or 827) || !frame.Safe || frame.InCombat || frame.Level == null || requested.Count == 0 || requested.Count > 3
            || requested.Select(p => p.FateId).Distinct().Count() != requested.Count
            || requested.Any(p => p.Centre.Territory != frame.World.Territory || p.Centre.Map != frame.World.Map || !Enum.IsDefined(p.Mode) || !NavigationRules.Finite(p.Centre.Position))
            || requested.Count > AutoTriggerRules.Capacity(frame.Level.Value, requested.Select(p => (int)p.Level)))
        { Status = "无法开始：检查元素等级、选点数量、当前岛屿与脱战状态。"; return false; }
        if (requested.Any(p => p.Mode == TriggerMode.Recorded && (p.Route.Length < 2 || p.Route.Length > 128 || p.Route.Any(v => !NavigationRules.Finite(v)))))
        { Status = "特殊触发需先记录本岛起点和终点（最多128点）。"; return false; }
        if (!mesh.Ready || mesh.Busy || mesh.Route.Count > 0) { Status = "导航网格未就绪或正在使用。"; return false; }
        var error = combat.BeginTrigger(requested.Any(p => p.Mode == TriggerMode.Normal), useRotation, useBmr) ?? combat.PrepareStart();
        if (error != null) { combat.EndTrigger(); Status = error; return false; }
        appeared.Clear(); plans.Clear(); plans.AddRange(requested); kills.Clear(); seen.Clear();
        world = frame.World; started = now;
        pullLimit = batch == 21 ? int.MaxValue : Math.Clamp(batch, 1, 20);
        killLimit = maxKills == 7 ? int.MaxValue : Math.Clamp(maxKills, 1, 6) * 50;
        Active = true; rotation = walking = returnHome = false;
        SelectNearest(frame.Position, now);
        Status = "自动触发已开始（会话上限2小时）。"; return true;
    }
    public void Stop(string reason = "已停止自动触发。")
    {
        var wasActive = Active;
        Active = false; StopTravel();
        if (wasActive) { if (rotation) combat.Disable(); combat.EndTrigger(); }
        rotation = walking = false;
        StopRecordedMovement(); waitingMobs.Clear(); pulled.Clear(); seen.Clear(); cache.Clear(); waves.Clear(); wave = [];
        Status = reason;
    }
    private void SelectNearest(Vector3 position, DateTimeOffset now)
    {
        var nearest = plans.MinBy(p => NavigationRules.Distance(position, p.Centre.Position))!;
        plans.Remove(nearest); plans.Insert(0, nearest);
        cache.Clear(); retryAfter.Clear(); waves.Clear(); wave = []; pulled.Clear(); waitingMobs.Clear(); seen.Clear();
        target = 0; finishPending = false; nextCache = default;
        SetPhase(nearest.Mode == TriggerMode.Recorded ? Phase.Search : Phase.Centre, now);
    }
    private void SetPhase(Phase value, DateTimeOffset now) { phase = value; phaseAt = now; attempts = 0; actionAt = default; }
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
    private bool Go(Vector3 point, TriggerFrame frame, DateTimeOffset now, bool unknownHeight = false)
    {
        var grounded=AutoTriggerRules.Ground(mesh,point,unknownHeight);
        if(grounded==null || !NavigationRules.Finite(grounded.Value) || NavigationRules.Distance(point,grounded.Value)>5)
        {
            Stop($"触发目标附近无可行走地面：目标={point}；玩家={frame.Position}；已知高度={!unknownHeight}");return false;
        }
        point=grounded.Value;
        var destination = new MapDestination(world.Territory, world.Map, point, "触发路线");
        if (islandTravel != null && !frame.InCombat && IslandCrystalRoutes.Choose(world.Territory, frame.Position, point) != null)
        {
            islandTravel.Start(destination, world, frame.Position, [], now, waitWithoutFate: false);
            walking = islandTravel.Active;
            if (!walking) Stop(islandTravel.Status);
            return walking;
        }
        move.Start(destination, world, frame.Position, [], now, false,
            arrivalRadius: 1, unknownHeight: false, relaxedArrival: plans[0].Mode == TriggerMode.Normal);
        walking = move.Active;
        if (!walking) Stop(move.Status);
        return walking;
    }
    private bool StartRotation(ulong id, bool fighting, bool approach, IReadOnlyList<ulong>? eligible = null)
    {
        if (!combat.PrepareTarget(id, fighting, eligible)) { Stop("准备攻击目标失败：" + (combat.Error ?? "目标不可用或战斗设置已变化。")); return false; }
        if (!rotation) { rotation = combat.EnableTrigger(approach); if (!rotation) Stop("无法开启战斗控制：" + (combat.Error ?? "RSR／BMR未确认启用。")); }
        return rotation;
    }
    private bool EndRotation()
    {
        if (rotation && !combat.Disable()) { Stop("等待自动循环关闭，请稍后重新开始。"); return false; }
        rotation = false; return true;
    }
    private void StopRecordedMovement()
    {
        var issued = recordedMovement;
        recordedMovement = [];
        try { if (issued.Length != 0 && NavigationRules.OwnsRoute(mesh.Route, issued)) mesh.Stop(); }
        catch { /* The navigation provider can unload mid-route. */ }
    }

    private void StopTravel()
    {
        move.Stop();
        if (islandTravel?.Active == true) islandTravel.Stop();
    }

    private void Fight(DateTimeOffset now) { StopTravel(); StopRecordedMovement(); walking = false; target = 0; SetPhase(Phase.Fight, now); }
    public void Tick(TriggerFrame frame, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (now - started > TimeSpan.FromHours(2)) { Stop("自动触发达到2小时上限。"); return; }
            if (now - phaseAt > TimeSpan.FromMinutes(5)) { Stop("当前触发阶段超过5分钟，已停止。"); return; }
            if (Transferring && frame.Loading) return;
            if (!frame.Safe || frame.World != world || frame.Level == null) { Stop("角色、地图或可操作状态变化，触发已停止。"); return; }
            if (!mesh.Ready || rotation && (phase != Phase.Fight || frame.InCombat) && !combat.StationaryHealthy)
            { Stop("导航或循环控制失效，已停止。"); return; }
            if (rotation && (mesh.Busy || mesh.Route.Count > 0))
            { Stop("检测到其他导航任务，已释放战斗控制。"); return; }
            if (plans.Count > AutoTriggerRules.Capacity(frame.Level.Value, plans.Select(p => (int)p.Level))) { Stop("元素等级变化，选择数量超过限制。"); return; }
            var plan = plans[0];
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
            var phaseText = phase switch { Phase.Centre => "前往FATE中心", Phase.Approach => "前往下一只触发怪", Phase.Search => "前往记录路线", Phase.Pull => "拉怪", Phase.Fight => "击杀", Phase.Return => "返回起点", _ => "等待刷新" };
            var quota = wave.Length > 0 ? wave.Length.ToString() : pullLimit == int.MaxValue ? "∞" : pullLimit.ToString();
            Status = $"{plan.Centre.Label} · {phaseText} · 缓存 {cache.Count} · 待拉波次 {waves.Count} · 已拉 {pulled.Count}/{quota} · 本次观测击杀 {kills.GetValueOrDefault(plan.FateId)}/{(killLimit == int.MaxValue ? "∞" : killLimit)}";
            if (finishPending && phase != Phase.Fight && !(plan.Mode == TriggerMode.Recorded && phase == Phase.Pull))
            { if (frame.InCombat) Fight(now); else { Advance(frame.Position, now); return; } }
            if (phase == Phase.Approach && !frame.InCombat && living.Any(m => CanPull(m, plan, frame.Position, now)))
            {
                StopTravel();
                walking = false;
                nextCache = default;
                SetPhase(Phase.Pull, now);
            }
            if (walking)
            {
                if (islandTravel?.Active == true)
                {
                    islandTravel.Tick(world, frame.Position, frame.Safe, frame.Loading, frame.Mounted, frame.Casting, [], now, frame.InCombat);
                    if (islandTravel.Active) return;
                    walking = false;
                    if (islandTravel.TakeArrival() == null) { Stop(islandTravel.Status); return; }
                    if (phase is Phase.Centre or Phase.Approach) SetPhase(Phase.Pull, now);
                }
                else
                {
                    move.Tick(world, frame.Position, frame.Safe, [], now);
                    if (!move.Active)
                    {
                        walking = false;
                        if (move.TakeArrival() == null) { Stop(move.Status); return; }
                        if (phase is Phase.Centre or Phase.Approach) SetPhase(Phase.Pull, now);
                    }
                }
            }
            if (plan.Mode == TriggerMode.Normal && phase != Phase.Centre) RefreshCache(plan, observed, now);
            if (phase == Phase.Fight)
            {
                if (!Riding(false, frame, now)) return;
                var eligible = living.Where(m => m.Targetable && m.Aggro)
                    .OrderByDescending(m => m.Hp).ThenBy(m => Vector3.DistanceSquared(frame.Position, m.Position)).ToArray();
                if (frame.InCombat && eligible.Length > 0)
                {
                    StartRotation(eligible[0].Id, true, plan.Mode == TriggerMode.Normal, eligible.Select(m => m.Id).ToArray());
                    return;
                }
                if (frame.InCombat) { EndRotation(); return; }
                if (!EndRotation()) return;
                pulled.Clear(); target = 0;
                if (finishPending) { Advance(frame.Position, now); return; }
                if (plan.Mode == TriggerMode.Recorded) { returnHome = true; SetPhase(Phase.Return, now); }
                else
                {
                    wave = [];
                    SetPhase(Phase.Approach, now);
                }
                return;
            }
            if (phase == Phase.Return)
            {
                if (!Riding(true, frame, now)) return;
                if (returnHome) { returnHome = false; Go(plan.Route[0], frame, now); return; }
                if (!walking)
                {
                    waitingMobs.Clear();
                    waitingMobs.UnionWith(living.Select(m => m.Id));
                    SetPhase(Phase.Wait, now);
                }
                return;
            }
            if (phase == Phase.Wait && plan.Mode == TriggerMode.Recorded)
            {
                waitingMobs.ExceptWith(observed.Where(m => m.Dead).Select(m => m.Id));
                if (!frame.InCombat && living.Any(m => m.Targetable && !m.Engaged && !waitingMobs.Contains(m.Id)
                    && Vector3.Distance(frame.Position, m.Position) <= 60))
                    SetPhase(Phase.Search, now);
                return;
            }
            if (plan.Mode == TriggerMode.Recorded)
            {
                if (phase == Phase.Search)
                {
                    if (!Riding(true, frame, now)) return;
                    if (Vector3.Distance(frame.Position, plan.Route[0]) > 2)
                    {
                        if (!walking) Go(plan.Route[0], frame, now);
                        return;
                    }
                    if (walking) return;
                    if (mesh.Busy || mesh.Route.Count > 0) { Stop("已有其他移动任务，无法开始记录路线。"); return; }
                    recordedMovement = plan.Route[1..];
                    mesh.Move(recordedMovement.ToList());
                    SetPhase(Phase.Pull, now);
                    return;
                }
                if (!frame.Mounted) { Stop("特殊拉怪途中已下坐骑，停止路线。"); return; }
                var remaining = mesh.Route;
                if (mesh.Busy || remaining.Count > 0 && !NavigationRules.OwnsRoute(remaining, recordedMovement))
                { Stop("记录路线已被其他移动替换。"); return; }
                if (remaining.Count <= 1 && Vector3.Distance(frame.Position, plan.Route[^1]) <= 2) Fight(now);
                else if (remaining.Count == 0) Stop("记录路线提前结束，未到达终点。");
                return;
            }
            TickNormal(plan, frame, living, now);
        }
        catch (Exception ex) { Stop("自动触发失败：" + ex.Message); }
    }
    private void RefreshCache(TriggerPlan plan, IReadOnlyList<TriggerMob> observed, DateTimeOffset now)
    {
        // Invalidate immediately, but refresh positions and discover new objects once a second.
        var refresh = now >= nextCache;
        if (refresh)
        {
            cache.Clear();
            nextCache = now.AddSeconds(1);
        }
        foreach (var mob in observed)
        {
            if (mob.Dead || mob.Aggro || mob.Engaged || !mob.Targetable || pulled.Contains(mob.Id)
                || retryAfter.GetValueOrDefault(mob.Id) > now
                || !NavigationRules.Finite(mob.Position) || NavigationRules.Distance(mob.Position, plan.Centre.Position) > CacheRadius)
                cache.Remove(mob.Id);
            else if (refresh)
                cache[mob.Id] = mob;
        }
    }

    private void BuildWaves(Vector3 position)
    {
        var groups = cache.Values.ToLookup(m => m.Position);
        var ordered = AutoTriggerRules.Order(position, groups.Select(g => g.Key), groups.Count)
            .SelectMany(p => groups[p]).Select(m => m.Id).ToArray();
        var offset = 0;
        foreach (var size in AutoTriggerRules.WaveSizes(ordered.Length, pullLimit))
        {
            waves.Enqueue(ordered[offset..(offset + size)]);
            offset += size;
        }
    }

    private bool IsPullTarget(TriggerMob mob, TriggerPlan plan, DateTimeOffset now) =>
        mob.Targetable && !mob.Aggro && !mob.Engaged && !pulled.Contains(mob.Id)
        && retryAfter.GetValueOrDefault(mob.Id) <= now
        && NavigationRules.Distance(plan.Centre.Position, mob.Position) <= CacheRadius;

    private bool CanPull(TriggerMob mob, TriggerPlan plan, Vector3 position, DateTimeOffset now) =>
        IsPullTarget(mob, plan, now) && NavigationRules.Distance(position, mob.Position) <= PullRadius;

    private void TickNormal(TriggerPlan plan, TriggerFrame frame, TriggerMob[] living, DateTimeOffset now)
    {
        if (phase == Phase.Centre)
        {
            if (walking) return;
            if (!frame.InCombat && frame.CanMountForTravel && NavigationRules.Distance(frame.Position, plan.Centre.Position) > 5
                && !Riding(true, frame, now, optional: true)) return;
            Go(plan.Centre.Position, frame, now, true);
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
                nextCache = default;
                SetPhase(Phase.Approach, now);
            }
            return;
        }
        if (walking) return;
        if (wave.Length > 0 && pulled.Count >= wave.Length) { Fight(now); return; }
        if (target != 0)
        {
            var current = living.FirstOrDefault(m => m.Id == target);
            if (current == null || !CanPull(current, plan, frame.Position, now))
            {
                cache.Remove(target);
                target = 0;
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
                    target = 0;
                    return;
                }
                StartRotation(current.Id, false, true);
                return;
            }
        }

        if (wave.Length == 0)
        {
            if (waves.Count == 0) BuildWaves(frame.Position);
            while (waves.TryDequeue(out var next))
            {
                if (!living.Any(m => next.Contains(m.Id) && CanPull(m, plan, frame.Position, now))) continue;
                wave = next.Where(cache.ContainsKey).ToArray();
                if (wave.Length != 0) break;
            }
        }
        if (wave.Length == 0 || pulled.Count >= wave.Length)
        {
            if (frame.InCombat || pulled.Count > 0) Fight(now);
            else if (EndRotation()) SetPhase(Phase.Wait, now);
            return;
        }
        var candidate = living.Where(m => cache.ContainsKey(m.Id) && wave.Contains(m.Id) && CanPull(m, plan, frame.Position, now))
            .MinBy(m => NavigationRules.Distance(frame.Position, m.Position));
        if (candidate != null)
        {
            if (frame.Casting && !rotation || !Riding(false, frame, now)) return;
            target = candidate.Id;
            targetAt = targetProgressAt = now;
            targetDistance = NavigationRules.Distance(frame.Position, candidate.Position);
            targetHp = candidate.Hp;
            StartRotation(target, false, true);
            return;
        }
        if (frame.InCombat || pulled.Count > 0)
        {
            Fight(now);
            Status = $"{plan.Centre.Label} · 附近暂无可拉目标，开始击杀。";
            return;
        }
        if (!EndRotation()) return;
        wave = [];
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
