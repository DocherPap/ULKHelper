using System.Numerics;

namespace ULKHelper.Automation;

internal enum TriggerMode { Normal, Recorded }
internal sealed record TriggerPlan(ushort FateId, byte Level, MapDestination Centre, TriggerMode Mode, Vector3[] Route, Vector3[]? SearchPoints = null);
internal sealed record TriggerMob(ulong Id, ushort FateId, Vector3 Position, bool Dead, bool Aggro);
internal sealed record MobArea(ushort FateId, Vector3 Position);
internal sealed record AutoTriggerSnapshot(bool Active, string Status, int? Level, IReadOnlyList<MobArea> Areas, IReadOnlyDictionary<ushort, Vector3[]> Routes)
{
    public static readonly AutoTriggerSnapshot Empty = new(false, "尚未开始自动触发。", null, [], new Dictionary<ushort, Vector3[]>());
}
internal sealed record TriggerFrame(WorldContext World, Vector3 Position, bool Safe, bool Mounted, bool Casting, bool InCombat,
    int? Level, IReadOnlyList<TriggerMob> Mobs, IReadOnlySet<ushort> ActiveFates);
internal interface ITriggerCombat
{
    string? BeginTrigger() => null;
    void EndTrigger() { }
    bool PrepareTarget(ulong id, bool fighting) => Target(id);
    string? PrepareStart();
    bool Target(ulong id);
    bool EnableStationary();
    bool Disable();
    bool StationaryHealthy { get; }
}
internal static class AutoTriggerRules
{
    public static int Capacity(int player, IEnumerable<int> levels) => levels.Select(level => level < player ? 3 : level <= player + 2 ? 2 : 1).DefaultIfEmpty(3).Min();
    public static TriggerMode DefaultMode(ushort id) => id is 1365 or 1402 or 1403 or 1404 or 1423 ? TriggerMode.Recorded : TriggerMode.Normal;
    // Fast open-path heuristic, not an exact travelling-salesman solution or navmesh travel-time estimate.
    public static List<Vector3> Order(Vector3 start, IEnumerable<Vector3> points)
    {
        var remaining = points.Where(NavigationRules.Finite).Distinct().Take(128).ToList();
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
internal sealed class AutoTriggerHelper(INavigationMesh mesh, IArrivalActions mounts, ITriggerCombat combat)
{
    private readonly DestinationNavigationHelper move = new(mesh);
    private readonly List<TriggerPlan> plans = [];
    private readonly Dictionary<ushort, int> kills = new();
    private readonly Dictionary<ulong, (TriggerMob Mob, DateTimeOffset Seen)> seen = new();
    private readonly HashSet<ulong> pulled = [];
    private readonly Queue<Vector3> waypoints = new();
    private readonly Queue<ulong> huntOrder = new();
    private bool scannedRing;
    private readonly HashSet<ushort> searchedKnown = [];
    private readonly HashSet<ushort> appeared = [];
    private WorldContext world;
    private DateTimeOffset started, phaseAt, actionAt, lastScan;
    private int index, pullLimit, killLimit, attempts;
    private ulong target;
    private bool rotation, walking, returnHome, finishPending;
    private enum Phase { Search, Pull, FinishRoute, Fight, Return, Wait }
    private Phase phase;
    public bool Active { get; private set; }
    public string Status { get; private set; } = "尚未开始自动触发。";
    public bool Start(IReadOnlyList<TriggerPlan> requested, TriggerFrame frame, int batch, int maxKills, DateTimeOffset now)
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
        var error = combat.BeginTrigger() ?? combat.PrepareStart();
        if (error != null) { combat.EndTrigger(); Status = error; return false; }
        huntOrder.Clear(); appeared.Clear(); searchedKnown.Clear(); scannedRing = false; plans.Clear(); plans.AddRange(requested); kills.Clear(); seen.Clear(); pulled.Clear(); waypoints.Clear();
        world = frame.World; started = phaseAt = now; actionAt = default; lastScan = default; attempts = 0; index = 0;
        pullLimit = batch == 21 ? int.MaxValue : Math.Clamp(batch, 1, 20);
        killLimit = maxKills == 7 ? int.MaxValue : Math.Clamp(maxKills, 1, 6) * 50;
        phase = Phase.Search; Active = true; rotation = walking = returnHome = finishPending = false; target = 0;
        Status = "自动触发已开始（会话上限2小时）。"; return true;
    }
    public void Stop(string reason = "已停止自动触发。")
    { Active = false; move.Stop(); if (rotation) combat.Disable(); rotation = false; combat.EndTrigger(); waypoints.Clear(); pulled.Clear(); seen.Clear(); Status = reason; }
    private void SetPhase(Phase value, DateTimeOffset now) { phase = value; phaseAt = now; attempts = 0; actionAt = default; }
    private bool Riding(bool desired, TriggerFrame frame, DateTimeOffset now)
    {
        if (frame.Mounted == desired) return true;
        if (frame.Casting || now < actionAt) return false;
        if (desired && frame.InCombat) return false;
        if (attempts++ >= 3) { Stop("骑乘状态切换失败（3次）。"); return false; }
        actionAt = now.AddSeconds(3); if (desired) mounts.Mount(); else mounts.Dismount(); return false;
    }
    private bool Go(Vector3 point, TriggerFrame frame, DateTimeOffset now, bool unknownHeight = false)
    {
        move.Start(new(world.Territory, world.Map, point, "触发路线"), world, frame.Position, [], now, false, unknownHeight: unknownHeight);
        walking = move.Active;
        if (!walking) Stop(move.Status);
        return walking;
    }
    private bool StartRotation(ulong id, bool fighting = false)
    {
        if (!combat.PrepareTarget(id, fighting)) return false;
        if (!rotation) { rotation = combat.EnableStationary(); if (!rotation) Stop("无法开启原地自动循环。"); }
        return rotation;
    }
    private bool EndRotation()
    {
        if (rotation && !combat.Disable()) { Stop("等待自动循环关闭，请稍后重新开始。"); return false; }
        rotation = false; return true;
    }
    private void Fight(DateTimeOffset now) { move.Stop(); walking = false; target = 0; SetPhase(Phase.Fight, now); }
    public void Tick(TriggerFrame frame, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (!frame.Safe || frame.World != world || frame.Level == null) { Stop("角色、地图或可操作状态变化，触发已停止。"); return; }
            if (now - started > TimeSpan.FromHours(2)) { Stop("自动触发达到2小时上限。"); return; }
            if (now - phaseAt > TimeSpan.FromMinutes(5)) { Stop("当前触发阶段超过5分钟，已停止。"); return; }
            if (!mesh.Ready || rotation && !combat.StationaryHealthy) { Stop("导航或循环控制失效，已停止。"); return; }
            if (plans.Count > AutoTriggerRules.Capacity(frame.Level.Value, plans.Select(p => (int)p.Level))) { Stop("元素等级变化，选择数量超过限制。"); return; }
            foreach (var mob in frame.Mobs)
            {
                if (seen.TryGetValue(mob.Id, out var old) && !old.Mob.Dead && mob.Dead && old.Mob.FateId == mob.FateId
                    && now - old.Seen <= TimeSpan.FromSeconds(2) && (pulled.Contains(mob.Id) || target == mob.Id))
                    kills[mob.FateId] = kills.GetValueOrDefault(mob.FateId) + 1;
            }
            seen.Clear(); foreach (var mob in frame.Mobs) seen[mob.Id] = (mob, now);
            appeared.UnionWith(frame.ActiveFates);
            var plan = plans[index];
            finishPending |= appeared.Contains(plan.FateId) || kills.GetValueOrDefault(plan.FateId) >= killLimit;
            var living = frame.Mobs.Where(m => m.FateId == plan.FateId && !m.Dead).ToArray();
            foreach (var mob in living.Where(m => m.Aggro)) pulled.Add(mob.Id);
            var phaseText = phase switch { Phase.Search => "寻找", Phase.Pull => "拉怪", Phase.FinishRoute => "前往终点", Phase.Fight => "原地击杀", Phase.Return => "返回起点", _ => "等待刷新" };
            Status = $"{plan.Centre.Label} · {phaseText} · 已拉 {pulled.Count}/{(pullLimit == int.MaxValue ? "∞" : pullLimit)} · 本次观测击杀 {kills.GetValueOrDefault(plan.FateId)}/{(killLimit == int.MaxValue ? "∞" : killLimit)}";
            if (finishPending && phase != Phase.Fight)
            { if (frame.InCombat) Fight(now); else { Advance(now); return; } }
            if (walking)
            {
                move.Tick(world, frame.Position, frame.Safe, [], now);
                if (!move.Active)
                { walking = false; if (move.TakeArrival() == null) { Stop(move.Status); return; } }
            }
            if (phase == Phase.FinishRoute)
            { if (!walking) Fight(now); return; }
            if (phase == Phase.Fight)
            {
                if (!Riding(false, frame, now)) return;
                var enemy = living.Where(m => pulled.Contains(m.Id) && m.Aggro).MinBy(m => Vector3.DistanceSquared(frame.Position, m.Position));
                if (enemy != null) { StartRotation(enemy.Id, true); return; }
                if (frame.InCombat) return; // Unknown/unloaded enemies are not counted as dead.
                if (!EndRotation()) return;
                pulled.Clear(); target = 0;
                if (finishPending) { Advance(now); return; }
                waypoints.Clear();
                if (plan.Mode == TriggerMode.Recorded) { returnHome = true; SetPhase(Phase.Return, now); }
                else { index = (index + 1) % plans.Count; SetPhase(Phase.Search, now); }
                return;
            }
            if (phase == Phase.Return)
            {
                if (!Riding(true, frame, now)) return;
                if (returnHome) { returnHome = false; Go(plan.Route[0], frame, now); return; }
                if (!walking) SetPhase(Phase.Wait, now);
                return;
            }
            if (phase == Phase.Wait)
            {
                if (frame.InCombat) { Fight(now); return; }
                if (living.Any(m => plan.Route.Any(p => Vector3.Distance(p, m.Position) <= 60))) SetPhase(Phase.Search, now);
                return;
            }
            if (plan.Mode == TriggerMode.Recorded)
            {
                if (phase == Phase.Search)
                {
                    if (!Riding(true, frame, now)) return;
                    waypoints.Clear(); foreach (var p in plan.Route) waypoints.Enqueue(p);
                    SetPhase(Phase.Pull, now);
                }
                if (!frame.Mounted) { Stop("特殊拉怪途中已下坐骑，停止路线。"); return; }
                if (pulled.Count >= pullLimit) { waypoints.Clear(); move.Stop(); walking = false; Go(plan.Route[^1], frame, now); SetPhase(Phase.FinishRoute, now); return; }
                if (!walking)
                { if (waypoints.TryDequeue(out var next)) Go(next, frame, now); else Fight(now); }
                return;
            }
            if (!Riding(false, frame, now)) return;
            if (phase == Phase.Pull)
            {
                var mob = living.FirstOrDefault(m => m.Id == target);
                if (mob == null || mob.Aggro || mob.Dead)
                { if (!EndRotation()) return; move.Stop(); walking = false; target = 0; SetPhase(Phase.Search, now); return; }
                if (!walking) StartRotation(mob.Id);
                if (now - phaseAt > TimeSpan.FromSeconds(25)) { Stop("目标未确认仇恨，停止拉怪。"); }
                return;
            }
            if (pulled.Count >= pullLimit || finishPending) { Fight(now); return; }
            var candidates = living.Where(m => !m.Aggro && !pulled.Contains(m.Id) && Vector3.Distance(m.Position, frame.Position) <= 100).ToArray();
            if (candidates.Length > 0)
            {
                while (huntOrder.Count > 0 && !candidates.Any(m => m.Id == huntOrder.Peek())) huntOrder.Dequeue();
                if (huntOrder.Count == 0)
                    foreach (var position in AutoTriggerRules.Order(frame.Position, candidates.Select(m => m.Position)))
                        huntOrder.Enqueue(candidates.First(m => m.Position == position).Id);
                var next = candidates.First(m => m.Id == huntOrder.Dequeue());
                move.Stop(); walking = false; target = next.Id; SetPhase(Phase.Pull, now);
                Go(ArrivalRules.Position(frame.Position, next.Position, 3), frame, now); return;
            }
            if (pulled.Count > 0 || frame.InCombat) { Fight(now); return; }
            if (walking) return;
            if (waypoints.Count == 0 && searchedKnown.Add(plan.FateId) && plan.SearchPoints is { Length: > 0 })
            {
                foreach (var point in AutoTriggerRules.Order(frame.Position, plan.SearchPoints)) waypoints.Enqueue(point);
            }
            if (waypoints.Count == 0)
            {
                if (scannedRing && plans.Count > 1)
                { index = (index + 1) % plans.Count; huntOrder.Clear(); scannedRing = false; SetPhase(Phase.Search, now); return; }
                if (now - lastScan < TimeSpan.FromSeconds(15)) return;
                scannedRing = true;
                lastScan = now;
                for (var i = 0; i < 8; i++) { var a = i * MathF.PI / 4; waypoints.Enqueue(plan.Centre.Position + new Vector3(MathF.Cos(a) * 45, 0, MathF.Sin(a) * 45)); }
            }
            if (waypoints.TryDequeue(out var spot)) Go(spot, frame, now, plan.SearchPoints?.Contains(spot) != true);
        }
        catch (Exception ex) { Stop("自动触发失败：" + ex.Message); }
    }
    private void Advance(DateTimeOffset now)
    {
        move.Stop(); walking = false; if (!EndRotation()) return;
        plans.RemoveAt(index); finishPending = false; pulled.Clear(); waypoints.Clear(); target = 0;
        if (plans.Count == 0) { Stop("计划已结束：FATE已出现或达到放弃数量。"); return; }
        index %= plans.Count; SetPhase(Phase.Search, now);
    }
}
