using System.Numerics;

namespace ULKHelper.Automation;

internal sealed class CliffEscapeHelper(INavigationMesh mesh, IArrivalActions mount, ICrystalCombat? combat,
    IReadOnlyList<CliffRegion>? regions = null) : IDisposable
{
    private enum Phase { CheckExit, ApproachPath, LandingPath, Mount, Approach, Edge, Drop, Landed }
    private Phase phase;
    private CliffDrop[] candidates = [];
    private CliffDrop drop = null!;
    private Vector3 exit;
    private int index, dismounts;
    private DateTimeOffset started, phaseAt, nextDismount;
    private DateTimeOffset? calmAt, landedAt, fightAt;
    private Task<List<Vector3>>? path;
    private CancellationTokenSource? cancel;
    private List<Vector3> approachPath = [], issued = [];
    private readonly TravelMountHelper mounting = new(mount);
    private bool fighting, requireMount;
    public bool Active { get; private set; }
    public bool Succeeded { get; private set; }
    public bool HandlingCombat => Active;
    public string Status { get; private set; } = "";

    public bool Start(uint territory, Vector3 player, Vector3 escapeTarget, bool summonMount, DateTimeOffset now)
    {
        Stop();
        if (!mesh.SupportsRouteEstimates || !mesh.Ready || mesh.Busy || mesh.Route.Count > 0) return false;
        candidates = (regions ?? IslandCliffRoutes.All).Where(r => r.Territory == territory && IslandCliffRoutes.Contains(r, player))
            .SelectMany(r => r.Drops).Where(IslandCliffRoutes.Valid).OrderBy(d => Vector3.DistanceSquared(player, d.Approach))
            .Take(24).ToArray();
        if (candidates.Length == 0) return false;
        exit = escapeTarget; requireMount = summonMount; started = phaseAt = now;
        Active = true; Succeeded = false; index = 0; mounting.Reset();
        phase = Phase.CheckExit;
        Status = "检查平台出口与下层水晶路线。";
        try { Request(player, exit); }
        catch (Exception ex) { Stop("平台出口查询失败：" + ex.GetBaseException().Message); }
        return true;
    }
    private void Request(Vector3 from, Vector3 to)
    {
        cancel?.Dispose(); cancel = new();
        path = mesh.FindPath(from, to, cancel.Token);
    }
    private List<Vector3>? TakePath(Vector3 target)
    {
        if (path == null || !path.IsCompleted || mesh.Busy) return null;
        List<Vector3> result;
        try { result = path.GetAwaiter().GetResult(); } catch { result = []; }
        path = null; cancel?.Dispose(); cancel = null;
        return result.Count > 0 && result.All(NavigationRules.Finite) && Vector3.Distance(result[^1], target) <= .5f ? result : [];
    }
    private void Next(Vector3 player, DateTimeOffset now)
    {
        StopOwnMovement(); calmAt = null;
        if (index >= candidates.Length) { Stop("平台出口未通过完整路线或现场碰撞检查，已停止，未跳崖。"); return; }
        drop = candidates[index++]; phase = Phase.ApproachPath; phaseAt = now;
        Request(player, drop.Approach);
    }
    public void Tick(Vector3 player, bool mounted, bool casting, bool inCombat, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (!mesh.Ready || now - started >= TimeSpan.FromMinutes(5)) { Stop("落崖行程超时或网格失效。"); return; }
            if (mesh.Route.Count > 0 && !NavigationRules.OwnsRoute(mesh.Route, issued)) { Stop("其他导航接管，已停止落崖。"); return; }
            if (issued.Count > 0 && mesh.Busy) { Stop("其他寻路接管，已停止落崖。"); return; }
            if (path != null && now - phaseAt > TimeSpan.FromSeconds(15)) { Stop("平台出口寻路超时。"); return; }
            if (phase == Phase.CheckExit)
            {
                if (TakePath(exit) is not { } result) return;
                if (result.Count > 0) { Finish("已有正常步行出口，继续原行程。"); return; }
                Next(player, now); return;
            }
            if (phase == Phase.ApproachPath)
            {
                if (TakePath(drop.Approach) is not { } result) return;
                if (result.Count == 0) { Next(player, now); return; }
                approachPath = result;
                phase = Phase.LandingPath; phaseAt = now;
                Request(drop.Landing, exit); return;
            }
            if (phase == Phase.LandingPath)
            {
                if (TakePath(exit) is not { } result) return;
                if (result.Count == 0) { Next(player, now); return; }
                phase = Phase.Mount; phaseAt = now; mounting.Reset();
            }
            if (phase == Phase.Mount)
            {
                if (mesh.Busy) { Stop("其他寻路接管，未开始平台移动。"); return; }
                if (requireMount && !inCombat)
                {
                    var result = mounting.Tick(mounted, casting, inCombat, now);
                    if (result == TravelMountResult.Failed) { Stop("平台行程无法上坐骑。"); return; }
                    if (result != TravelMountResult.Mounted) return;
                }
                if (casting) return;
                issued = approachPath.ToList(); mesh.Move(issued.ToList());
                phase = Phase.Approach; phaseAt = now; Status = "前往崖边安全检查点。"; return;
            }
            if (phase == Phase.Approach)
            {
                if (Vector3.Distance(player, drop.Approach) > .75f)
                {
                    if (now - phaseAt > TimeSpan.FromSeconds(60) || mesh.Route.Count == 0) Stop("未能到达崖边检查点，未跳崖。");
                    return;
                }
                StopOwnMovement(); phase = Phase.Edge; phaseAt = now; calmAt = null;
                Status = "已到崖边，检查战斗和落点。";
            }
            if (phase is Phase.Edge or Phase.Landed)
            {
                if (inCombat)
                {
                    calmAt = null; fightAt ??= now;
                    if (now - fightAt >= TimeSpan.FromMinutes(3)) { Stop("崖边清理战斗超过3分钟，已停止。"); return; }
                    if (mounted)
                    {
                        if (now < nextDismount || casting) return;
                        if (dismounts++ >= 3) { Stop("无法下坐骑清理战斗，未继续落崖。"); return; }
                        nextDismount = now.AddSeconds(3); mount.Dismount(); return;
                    }
                    if (!fighting)
                    {
                        if (combat == null) { Stop("需要RSR清理崖边战斗，未继续落崖。"); return; }
                        fighting = true;
                        if (combat.BeginCliffCombat(this) is { } error) { Stop(error); return; }
                    }
                    if (!combat!.CliffCombatHealthy(this)) { Stop("崖边自动循环被更改，已停止。"); return; }
                    Status = "原地使用RSR清理战斗，不启用BM/BMR，脱战后继续。"; return;
                }
                if (fighting)
                {
                    if (!combat!.EndCrystalCombat(this)) { Stop("崖边战斗控制未能恢复，已停止。"); return; }
                    fighting = false;
                }
                calmAt ??= now;
                if (casting || now - calmAt < TimeSpan.FromMilliseconds(500)) return;
                if (phase == Phase.Landed) { Finish("已落地，重新规划水晶或营地路线。"); return; }
                if (Vector3.Distance(player, drop.Approach) > 1.25f) { Stop("角色已离开崖边检查点，未跳崖。"); return; }
                if (mesh.Busy) { Stop("其他寻路接管，未跳崖。"); return; }
                if (mesh.ClearCliffDrop(drop) != true) { Next(player, now); return; }
                issued = [drop.Landing]; mesh.Move(issued.ToList());
                phase = Phase.Drop; phaseAt = now; landedAt = null;
                Status = "脱战与现场碰撞检查通过，前往下层落点。"; return;
            }
            if (phase == Phase.Drop)
            {
                // Combat before leaving the ledge must never carry the player into the drop.
                if (inCombat && player.Y >= drop.Approach.Y - 1)
                { StopOwnMovement(); phase = Phase.Edge; calmAt = null; return; }
                if (Vector3.Distance(player, drop.Landing) <= 2 && Math.Abs(player.Y - drop.Landing.Y) <= 1)
                {
                    landedAt ??= now;
                    if (now - landedAt >= TimeSpan.FromMilliseconds(700))
                    { StopOwnMovement(); phase = Phase.Landed; calmAt = null; }
                }
                else landedAt = null;
                if (now - phaseAt > TimeSpan.FromSeconds(30)) Stop("未能确认下层落地，已停止。");
            }
        }
        catch (Exception ex) { Stop("平台行程失败：" + ex.GetBaseException().Message); }
    }
    private void Finish(string message) { Stop(message); Succeeded = true; }
    private void StopOwnMovement()
    {
        if (issued.Count > 0 && NavigationRules.OwnsRoute(mesh.Route, issued)) mesh.Stop();
        issued = [];
    }
    public void Stop(string message = "已停止平台行程。")
    {
        cancel?.Cancel(); cancel?.Dispose(); cancel = null;
        if (path is { } pending) _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        path = null;
        StopOwnMovement();
        if (fighting) combat?.EndCrystalCombat(this);
        fighting = false; dismounts = 0; nextDismount = default; fightAt = calmAt = landedAt = null;
        Active = Succeeded = false; Status = message;
    }
    public void Dispose() => Stop();
}
