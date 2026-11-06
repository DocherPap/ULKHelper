using System.Numerics;

namespace ULKHelper.Automation;

internal sealed record EncounterObservation(ArrivalFate? Fate, bool Ended = false);
internal interface IFellingCombat
{
    string? Error => null;
    string? PrepareStart() => CheckReady();
    string? CheckReady();
    bool Target(ulong bossId);
    void SetBossMagiaAttack() { }
    bool Sync(ushort fateId);
    bool Enable();
    bool EnableCleanupRotation() => false;
    bool Disable();
    bool Healthy { get; }
}

// Explicitly enabled session. New coordinates are coalesced while fighting, never interrupt combat.
internal sealed class FellingFollowHelper(IslandTravelHelper travel, INavigationMesh mesh, IArrivalActions mounts, IFellingCombat combat) : IDisposable
{
    private readonly DestinationNavigationHelper approach = new(mesh);
    private enum Phase { Waiting, Travel, UnspawnedTravel, UnspawnedWait, WaitApproach, Prepare, Engage, Fight, Remount, ReturnCamp, ParkDelay, Park, Standby, Retry }
    private Phase phase;
    private WorldContext context;
    private MapDestination? pending, goal, last;
    private FateEntry? pendingFate;
    private ushort? goalFate;
    private DateTimeOffset sessionAt, phaseAt, nextAction, missingAt;
    private int attempts;
    private (ushort Id, int Start)? encounter;
    private bool pulled, magiaAttempted;
    private bool repeat, initialSearch, eatAvailable;
    private bool navigationOnly, navigationMount, navigationCrystals;
    private bool bossApproached;
    private string? lastAction;
    private IslandCrystal? standbyCrystal;
    private bool parkStarted;
    private Vector3? standbyAnchor;
    private DateTimeOffset retryAt;
    private int retryCount;
    private MapDestination? retryGoal;
    private string? retryReason;
    private bool leaderHold, userPaused;
    private DateTimeOffset idleUntil;
    private Phase resumePhase;
    public void WaitForLeader()
    {
        if (!Following) return;
        leaderHold = true;
        attempts = 0; lastAction = null;
        pending = null; pendingFate = null; initialSearch = false;
        if (phase == Phase.Retry) phase = Phase.Waiting;
    }
    private readonly Dictionary<ushort, int> finishedFates = new();
    public bool Active { get; private set; }
    public bool Following => Active && repeat;
    public bool NavigatingFate => Active && navigationOnly;
    public bool CleaningAfterFate => Active && phase == Phase.Remount;
    public bool Transferring => Active && travel.Transferring;
    public bool Interacting => Active && travel.Interacting;
    private bool InspectPendingFate => !repeat && !navigationOnly && phase == Phase.Waiting && pendingFate != null;
    public MapDestination? Goal => InspectPendingFate ? pending : goal;
    public ushort? FateId => InspectPendingFate ? pendingFate?.Id : encounter?.Id ?? goalFate;
    public string Status { get; private set; } = "尚未开始跟随伐木。";
    public bool Start(WorldContext world, DateTimeOffset now, bool repeatFollowing = true)
    {
        Stop();
        if (world.Territory is not (732 or 763 or 795 or 827)) { Status = "请先进入优雷卡岛内。"; return false; }
        var error = combat.PrepareStart();
        if (error != null) { Status = error; return false; }
        repeat = repeatFollowing; initialSearch = repeatFollowing; lastAction = null; finishedFates.Clear(); context = world; sessionAt = phaseAt = now; phase = Phase.Waiting; Active = true;
        Status = repeat ? "正在查找最近的活动 NM，或前往最近水晶待机。" : "等待本次目的地。"; return true;
    }
    public bool StartCrystalStandby(WorldContext world, Vector3 player, DateTimeOffset now)
    {
        if (Active) return false;
        repeat = initialSearch = false;
        context = world;
        sessionAt = now;
        Active = true;
        Park(player, now);
        return Active;
    }
    public bool StartEating(WorldContext world, FateEntry fate, DateTimeOffset now)
    {
        if (!Start(world, now, repeatFollowing: false)) return false;
        if (!NavigationRules.Finite(fate.Position)) { Stop("FATE 目的地无效。"); return false; }
        eatAvailable = true;
        QueueEating(fate);
        return true;
    }

    private void QueueEating(FateEntry fate)
    {
        last = pending = new(context.Territory, context.Map, fate.Position, fate.Name, fate.Id);
        pendingFate = fate;
        phase = Phase.Waiting;
        Status = $"准备前往 {fate.Name}，等待开怪。";
    }
    public void StartFateNavigation(WorldContext world, MapDestination destination, FateEntry fate, DateTimeOffset now,
        bool summonMount, bool useCrystals)
    {
        Stop();
        repeat = initialSearch = false;
        navigationOnly = true;
        navigationMount = summonMount;
        navigationCrystals = useCrystals;
        finishedFates.Clear();
        context = world;
        sessionAt = phaseAt = now;
        phase = Phase.Waiting;
        Active = true;
        Receive(destination with { FateId = fate.Id }, fate);
        if (pending == null) Stop("FATE 目的地无效。");
        else Status = $"准备前往 {fate.Name}。";
    }
    public void Receive(MapDestination destination, FateEntry? fate = null)
    {
        if (!Active || destination.Territory != context.Territory || destination.Map != context.Map
            || !NavigationRules.Finite(destination.Position)) return;
        if (leaderHold)
        {
            leaderHold = false; last = null;
            pending = destination; pendingFate = fate;
            return;
        }
        if ((pending ?? goal ?? last) is { } previous && NavigationRules.Overlaps(previous.Position, destination.Position)) return;
        last = pending = destination;
        pendingFate = fate;
        retryCount = 0; retryAt = default;
        if (phase == Phase.Retry) phase = Phase.Waiting;
    }
    public bool StartAtBoss(WorldContext world, MapDestination destination, ArrivalFate fate, Vector3 boss,
        Vector3 player, DateTimeOffset now)
    {
        if (!Start(world, now, repeatFollowing: false)) return false;
        goal = destination;
        magiaAttempted = false;
        encounter = (fate.Id, fate.StartedAt);
        bossApproached = true;
        pulled = false;
        missingAt = default;
        attempts = 0;
        nextAction = now;
        phase = Phase.WaitApproach;
        if (ArrivalRules.IsPulled(fate).Started)
        {
            phase = Phase.Prepare;
            Status = "已检测到开怪，准备接管战斗。";
            return true;
        }
        var distance = fate.WaitDistance;
        var standOff = ArrivalRules.Position(player, boss, distance);
        approach.Start(destination with { Position = standOff }, world, player, [], now, false, unknownHeight: false,
            groundFilter: point => NavigationRules.Distance(point, boss) >= distance - 1);
        if (!approach.Active) { Stop(approach.Status); return false; }
        Status = $"前往头目 {distance:0} 米处，等待开怪。";
        return true;
    }
    public void Tick(ArrivalFrame frame, bool transferScreen, IReadOnlyList<FateEntry> fates, EncounterObservation observation, DateTimeOffset now,
        bool userMoving = false, bool temporaryBusy = false, IReadOnlyList<FateEntry>? knownFates = null, ulong cleanupTarget = 0)
    {
        if (!Active) return;
        try
        {
            if (frame.World != context || !frame.Safe && !temporaryBusy && !(Transferring && transferScreen)) { Stop("角色状态或地图变化，跟随已停止。"); return; }
            if (now - sessionAt >= TimeSpan.FromHours(2)) { Stop("本次跟随达到2小时上限。"); return; }
            var externalNavigation = repeat && !travel.Active && !approach.Active && (mesh.Busy || mesh.Route.Count > 0);
            if (repeat && !Transferring && (userMoving || temporaryBusy || externalNavigation || frame.InCombat && !frame.Mounted && phase is not (Phase.Fight or Phase.Engage or Phase.Remount)))
            {
                if (!userPaused)
                {
                    resumePhase = phase; userPaused = true;
                    travel.Stop(); approach.Stop(); combat.Disable();
                }
                idleUntil = now.AddSeconds(2);
            }
            if (userPaused)
            {
                if (frame.InCombat && !frame.Mounted || frame.Casting) idleUntil = now.AddSeconds(2);
                if (userMoving || temporaryBusy || externalNavigation || frame.InCombat && !frame.Mounted || frame.Casting || now < idleUntil)
                { Status = "用户正在操作或战斗，跟车暂停；空闲2秒后继续。"; return; }
                userPaused = false; phaseAt = nextAction = now; attempts = 0;
                if (resumePhase is Phase.Travel or Phase.UnspawnedTravel or Phase.WaitApproach && goal != null && pending == null)
                { pending = goal with { FateId = goalFate }; phase = Phase.Waiting; }
                else if (resumePhase == Phase.Park) { parkStarted = false; }
                else if (resumePhase is Phase.Fight or Phase.Engage) phase = Phase.Prepare;
            }
            if (phase == Phase.Retry)
            {
                if (pending != null) phase = Phase.Waiting;
                else if (now < retryAt) { Status = $"导航暂不可用，等待重试：{retryReason}"; return; }
                else
                {
                    if (retryCount >= 3)
                    {
                        retryCount = 0; last = null; retryGoal = null; Park(frame.Player, now); return;
                    }
                    else { pending = retryGoal is { } retry ? retry with { FateId = goalFate } : null; phase = Phase.Waiting; }
                }
            }
            if (leaderHold && !Transferring && (!frame.InCombat || frame.Mounted)
                && phase is not (Phase.Remount or Phase.Fight or Phase.Engage))
            {
                var nearest = (knownFates ?? []).Where(f => NavigationRules.Finite(f.Position)
                    && !finishedFates.ContainsKey(f.Id) && !fates.Any(live => live.Id == f.Id))
                    .OrderBy(f => NavigationRules.Distance(frame.Player, f.Position)).ThenBy(f => f.Id).FirstOrDefault();
                leaderHold = false; last = goal = null; goalFate = null; encounter = null; phase = Phase.Waiting;
                if (nearest == null) { Park(frame.Player, now); return; }
                Receive(new(context.Territory, context.Map, nearest.Position, nearest.Name, nearest.Id), nearest);
            }
            if (initialSearch && (!frame.InCombat || frame.Mounted) && !frame.Casting)
            {
                initialSearch = false;
                if (pending == null)
                {
                    var nearest = fates.Where(f => NavigationRules.Finite(f.Position)
                        && !(finishedFates.TryGetValue(f.Id, out var start) && (f.StartedAt <= 0 || start <= 0 || f.StartedAt <= start)))
                        .OrderBy(f => NavigationRules.Distance(frame.Player, f.Position)).ThenBy(f => f.Id).FirstOrDefault();
                    if (nearest != null)
                        Receive(new(context.Territory, context.Map, nearest.Position, nearest.Name), nearest);
                    else Park(frame.Player, now);
                }
            }
            if (pending != null && (navigationOnly || !frame.InCombat || frame.Mounted) && !Transferring
                && phase is Phase.Waiting or Phase.Travel or Phase.UnspawnedTravel or Phase.UnspawnedWait
                    or Phase.WaitApproach or Phase.Prepare or Phase.ParkDelay or Phase.Park or Phase.Standby)
            {
                if (!navigationOnly)
                {
                    if (!combat.Disable()) { Stop("未能关闭上一场战斗控制。"); return; }
                    if (combat.CheckReady() is { } error) { Stop(error); return; }
                }
                var next = pending; pending = null;
                magiaAttempted = false;
                travel.Stop(); approach.Stop(); goal = next;
                encounter = null; pulled = false; attempts = 0; missingAt = default; phaseAt = now;
                bossApproached = false;
                var selected = pendingFate;
                var pendingSelection = selected != null;
                pendingFate = null;
                selected ??= next.FateId is { } exact
                    ? (knownFates ?? []).Concat(fates).FirstOrDefault(f => f.Id == exact)
                    : NavigationRules.FindFate(next.Position, knownFates ?? fates, 100);
                goalFate = selected?.Id ?? next.FateId;
                var live = goalFate is { } id ? LiveFate(id, fates) : null;
                if (!repeat && !navigationOnly && pendingSelection && live == null)
                { Stop("选中的 NM 已结束或无法确认。"); return; }
                if (!repeat && !navigationOnly && live != null && LoadedBoss(observation.Fate, live.Id, live.StartedAt) is { } knownBoss)
                {
                    encounter = (live.Id, live.StartedAt);
                    goal = next with { Position = live.Position };
                    WaitAtLoadedBoss(frame, observation.Fate!, knownBoss, now);
                    return;
                }
                if (selected != null && live == null)
                {
                    goal = next with { Position = selected.Position, Label = selected.Name };
                    phase = Phase.UnspawnedTravel;
                    travel.StartUnspawnedFate(goal, context, frame.Player, now, !navigationOnly || navigationMount,
                        !navigationOnly || navigationCrystals);
                }
                else BeginFateTravel(frame, live, now);
                observation = new(null);
                if (!travel.Active) { RetryFollowing(travel.Status, now); return; }
            }
            if (phase == Phase.ParkDelay)
            {
                if ((!frame.InCombat || frame.Mounted) && now - phaseAt >= TimeSpan.FromSeconds(3)) Park(frame.Player, now);
                else Status = "等待新坐标，3秒后前往最近水晶。";
                return;
            }
            if (phase == Phase.ReturnCamp)
            {
                travel.Tick(frame.World, frame.Player, frame.Safe, transferScreen, frame.Mounted, frame.Casting, [], now, frame.InCombat);
                if (travel.Active) Status = $"返回营地：{travel.Status}";
                else Stop(travel.TakeArrival() != null ? "本岛暂无其他活动 FATE，已返回营地。" : $"返回营地失败：{travel.Status}");
                return;
            }
            if (phase is Phase.Park or Phase.Standby) { TickPark(frame, now); return; }
            if (phase == Phase.Waiting) return;
            if (phase is Phase.UnspawnedTravel or Phase.UnspawnedWait)
            {
                if (!Transferring && frame.Safe && goalFate is { } id && LiveFate(id, fates) is { } spawned)
                {
                    travel.Stop();
                    if (mesh.Busy) { Status = "FATE 已刷新，等待旧路线释放。"; return; }
                    BeginFateTravel(frame, spawned, now);
                    if (!travel.Active) RetryFollowing(travel.Status, now);
                    return;
                }
                Status = $"等待 {goal!.Label} 刷新（中心约 50 米）。";
                if (phase == Phase.UnspawnedWait) return;
                travel.Tick(frame.World, frame.Player, frame.Safe, transferScreen, frame.Mounted, frame.Casting, [], now, frame.InCombat);
                if (travel.Active) { Status = $"前往 {goal.Label} 中心约 50 米 · {travel.Status}"; return; }
                if (travel.TakeArrival() == null) { RetryFollowing(travel.Status, now); return; }
                if (!repeat) { Stop($"已在 {goal.Label} 中心约 50 米处停下。"); return; }
                phase = Phase.UnspawnedWait;
                return;
            }
            if (phase == Phase.WaitApproach)
            {
                if (observation.Ended)
                {
                    if (navigationOnly) Stop("目标 FATE 已结束。");
                    else { Finish(now); RemountImmediately(frame, now); }
                    return;
                }
                if (!navigationOnly && observation.Fate is { } waiting && ArrivalRules.IsPulled(waiting).Started)
                    approach.Stop();
                else
                {
                    approach.Tick(frame.World, frame.Player, frame.Safe, [], now);
                    if (approach.Active) return;
                    if (approach.TakeArrival() == null) { RetryFollowing(approach.Status, now); return; }
                }
                if (navigationOnly) { Stop("已到达 FATE 头目附近。"); return; }
                phase = Phase.Prepare; phaseAt = nextAction = now; attempts = 0;
            }
            if (phase == Phase.Travel)
            {
                if (encounter == null && FreshFate(goal!, fates) is { } near) encounter = (near.Id, near.StartedAt);
                if (!travel.Transferring && encounter is { } known && observation.Fate is { } nearby
                    && LoadedBoss(nearby, known.Id, known.Start) is { } boss
                    && (!repeat && !navigationOnly || NavigationRules.Distance(frame.Player, goal!.Position) <= Math.Max(50, nearby.WaitDistance + 20)))
                {
                    if (!repeat && !navigationOnly) WaitAtLoadedBoss(frame, nearby, boss, now);
                    else ApproachBoss(frame, boss, nearby.WaitDistance, now);
                    return;
                }
                var activeFate = navigationOnly && goalFate is { } id ? LiveFate(id, fates) : null;
                travel.Tick(frame.World, frame.Player, frame.Safe, transferScreen, frame.Mounted, frame.Casting,
                    activeFate == null ? [] : [activeFate], now, frame.InCombat);
                Status = travel.Status;
                if (travel.Active) return;
                if (travel.TakeArrival() == null) { RetryFollowing(travel.Status, now); return; }
                if (navigationOnly) { Stop("已到达 FATE 附近。"); return; }
                if (encounter == null)
                {
                    if (!repeat) { Stop("已到达车头坐标。"); return; }
                    BeginParkDelay(now);
                    return;
                }
                phase = Phase.Prepare; phaseAt = nextAction = now; attempts = 0;
            }
            if (phase == Phase.Remount)
            {
                if (frame.InCombat)
                {
                    phaseAt = now;
                    if (!frame.Mounted && !frame.Casting && cleanupTarget != 0
                        && combat.Target(cleanupTarget) && combat.EnableCleanupRotation())
                        Status = "FATE 已结束，RSR Manual 正在清理仇恨列表中的非 FATE 怪。";
                    else
                    {
                        combat.Disable();
                        Status = "已关闭自动输出，等待脱战。";
                    }
                    return;
                }
                if (!combat.Disable()) { Status = "等待关闭战斗控制。"; return; }
                if (now - phaseAt > TimeSpan.FromSeconds(40)) { Stop("战后无法召唤坐骑，跟随已停止。"); return; }
                if (frame.Casting) { Status = "战斗控制已关闭，等待读条结束后上坐骑。"; return; }
                if (frame.Mounted)
                {
                    if (eatAvailable) { ContinueEating(frame, fates, now); return; }
                    if (!repeat) { Stop("本次战斗已结束并上坐骑。"); return; }
                    if (leaderHold) { phase = Phase.Waiting; return; }
                    BeginParkDelay(phaseAt);
                    if (pending != null) phase = Phase.Waiting;
                    return;
                }
                Act(mounts.Mount, "召唤所选坐骑", now); return;
            }
            if (observation.Ended && encounter != null) { Finish(now); RemountImmediately(frame, now); return; }
            var fate = observation.Fate;
            if (phase == Phase.Prepare && !bossApproached && fate?.BossPosition is { } loadedBoss)
            {
                ApproachBoss(frame, loadedBoss, fate.WaitDistance, now);
                return;
            }
            if (phase == Phase.Prepare && frame.Mounted && !frame.Casting)
            {
                if (fate != null && (pulled || ArrivalRules.IsPulled(fate).Started) && fate.BossId != 0 && fate.BossTargetable)
                    combat.Target(fate.BossId);
                approach.Stop(); Act(mounts.Dismount, "下坐骑", now); return;
            }
            if (fate == null)
            {
                if (phase == Phase.Engage) { approach.Stop(); phase = Phase.Prepare; }
                if (phase == Phase.Fight)
                {
                    combat.Disable();
                    if (missingAt == default) missingAt = now;
                    if (!frame.InCombat && now - missingAt >= TimeSpan.FromSeconds(5)) { Finish(now); return; }
                    if (encounter is { } missing && LiveFate(missing.Id, fates) == null
                        && now - missingAt >= TimeSpan.FromSeconds(5))
                    {
                        Finish(now, confirmed: false);
                        return;
                    }
                    if (now - missingAt >= TimeSpan.FromSeconds(15)) { Stop("目标事件不可见，已关闭战斗控制。"); return; }
                }
                else if (now - phaseAt > TimeSpan.FromMinutes(5)) Stop("等待NM超时。");
                Status = "等待目的地附近的目标NM。"; return;
            }
            missingAt = default;
            if (encounter is { } bound && (bound.Id != fate.Id || bound.Start > 0 && fate.StartedAt > 0 && bound.Start != fate.StartedAt))
            { Stop("目标NM场次变化。"); return; }
            encounter = (fate.Id, fate.StartedAt > 0 ? fate.StartedAt : encounter?.Start ?? 0);
            if (fate.BossId != 0 && !fate.BossAlive) { Finish(now); RemountImmediately(frame, now); return; }
            if (phase == Phase.Fight && now - phaseAt > TimeSpan.FromMinutes(20)) { Stop("单场战斗超过20分钟。"); return; }
            if (phase == Phase.Engage)
            {
                if (now - phaseAt > TimeSpan.FromMinutes(5)) { Stop("开怪后接近头目超时。"); return; }
                var boss = fate.BossPosition ?? fate.Centre;
                if (NavigationRules.Distance(frame.Player, boss) <= 10)
                { approach.Stop(); phase = Phase.Prepare; }
                else
                {
                    approach.Tick(context, frame.Player, frame.Safe, [], now);
                    Status = "已确认开怪，先接近头目10米内。";
                    if (approach.Active) return;
                    if (approach.TakeArrival() == null) { RetryFollowing(approach.Status, now); return; }
                    phase = Phase.Prepare;
                }
            }
            if (phase == Phase.Prepare)
            {
                if (now - phaseAt > TimeSpan.FromMinutes(5)) { Stop("准备战斗超过5分钟。"); return; }
                pulled |= ArrivalRules.IsPulled(fate).Started;
                if (pulled && fate.BossId != 0 && fate.BossTargetable && !combat.Target(fate.BossId)) { Stop("无法锁定NM头目。"); return; }
                if (frame.Casting) return;
                if (frame.Mounted) { approach.Stop(); Act(mounts.Dismount, "下坐骑", now); return; }
                if (!pulled) { Status = "已到达并下坐骑，等待NM开怪。"; return; }
                if (fate.NeedsSync == null) { Status = "等待确认等级同步需求。"; return; }
                if (fate.NeedsSync.Value && !fate.Synced)
                { Act(() => combat.Sync(fate.Id), "等级同步", now); return; }
                var boss = fate.BossPosition ?? fate.Centre;
                if (NavigationRules.Distance(frame.Player, boss) > 10)
                {
                    approach.Stop();
                    approach.Start(goal! with { Position = ArrivalRules.Position(frame.Player, boss, 9) }, context,
                        frame.Player, [], now, false, unknownHeight: false,
                        groundFilter: p => NavigationRules.Distance(p, boss) <= 10);
                    if (!approach.Active) { RetryFollowing(approach.Status, now); return; }
                    phase = Phase.Engage; phaseAt = now;
                    Status = "已确认开怪，先接近头目10米内。"; return;
                }
                if (fate.BossId == 0 || !fate.BossTargetable) { Status = "等待确认可选中的NM头目。"; return; }
                if (!combat.Target(fate.BossId)) { Stop("无法锁定NM头目。"); return; }
                approach.Stop();
                if (!combat.Enable()) { Stop(combat.Error ?? "自动循环或BossMod未能开启。"); return; }
                phase = Phase.Fight; phaseAt = now; Status = "正在战斗，持续锁定NM头目。"; return;
            }
            if (phase == Phase.Fight)
            {
                if (frame.Mounted || fate.NeedsSync == null || fate.NeedsSync == true && !fate.Synced)
                {
                    combat.Disable(); phase = Phase.Prepare; phaseAt = nextAction = now; attempts = 0; return;
                }
                if (!combat.Healthy)
                {
                    if (!repeat) { Stop("战斗插件状态改变，已停止跟随。"); return; }
                    combat.Disable(); userPaused = true; resumePhase = Phase.Fight; idleUntil = now.AddSeconds(2);
                    Status = "战斗控制被修改，等待用户战斗结束后继续跟车。"; return;
                }
                if (fate.BossId == 0 || !fate.BossTargetable) { combat.Disable(); phase = Phase.Prepare; phaseAt = now; return; }
                if (!combat.Target(fate.BossId)) { Stop("无法保持NM头目目标。"); return; }
                if (frame.InCombat && !magiaAttempted)
                {
                    magiaAttempted = true;
                    combat.SetBossMagiaAttack();
                }
            }
        }
        catch (Exception ex) { Stop("跟随失败：" + ex.Message); }
    }
    private void RetryFollowing(string reason, DateTimeOffset now)
    {
        if (!repeat) { Stop(reason); return; }
        travel.Stop(); approach.Stop();
        if (pending != null) { phase = Phase.Waiting; return; }
        if (retryGoal != goal) { retryCount = 0; retryGoal = goal; }
        retryCount++;
        retryReason = reason; retryAt = now.AddSeconds(10 * retryCount);
        phase = Phase.Retry;
        Status = "导航暂不可用，保留跟车并等待重试：" + reason;
    }
    private void Act(Func<bool> action, string label, DateTimeOffset now)
    {
        if (lastAction != label) { attempts = 0; nextAction = now; lastAction = label; }
        if (now < nextAction) return;
        if (attempts++ >= 3) { Stop(label + "失败，已停止跟随。"); return; }
        nextAction = now.AddSeconds(3); action(); Status = "等待" + label + "完成。";
    }
    private void Finish(DateTimeOffset now, bool confirmed = true)
    {
        if (confirmed && encounter is { } ended) finishedFates[ended.Id] = ended.Start;
        approach.Stop(); combat.Disable(); phase = Phase.Remount; phaseAt = nextAction = now; attempts = 0;
        Status = confirmed ? "目标事件已结束，关闭战斗控制并准备上坐骑。" : "目标 FATE 持续不可见，结束本次追踪并等待脱战。";
    }
    private void RemountImmediately(ArrivalFrame frame, DateTimeOffset now)
    {
        if (combat.Disable() && !frame.Mounted && !frame.InCombat && !frame.Casting)
            Act(mounts.Mount, "召唤所选坐骑", now);
    }
    private void BeginFateTravel(ArrivalFrame frame, FateEntry? live, DateTimeOffset now)
    {
        if (live != null)
        {
            encounter = (live.Id, live.StartedAt);
            goal = goal! with { Position = live.Position };
        }
        phase = Phase.Travel;
        travel.Start(goal!, context, frame.Player, navigationOnly && live != null ? [live] : [], now,
            !navigationOnly || navigationMount, !navigationOnly || navigationCrystals,
            waitWithoutFate: false, unknownHeight: live == null, preferCrystals: live != null);
    }

    private void ApproachBoss(ArrivalFrame frame, Vector3 boss, float distance, DateTimeOffset now)
    {
        travel.Stop();
        if (navigationOnly && NavigationRules.Distance(frame.Player, boss) <= distance && Math.Abs(frame.Player.Y - boss.Y) <= 5)
        { Stop($"已在 FATE 头目 {distance:0} 米内。"); return; }
        bossApproached = true;
        phase = Phase.WaitApproach;
        phaseAt = now;
        approach.Start(goal! with { Position = ArrivalRules.Position(frame.Player, boss, distance) },
            context, frame.Player, [], now, waitWithoutFate: false, unknownHeight: false,
            groundFilter: point => NavigationRules.Distance(point, boss) >= distance - 1);
        if (!approach.Active) Stop(approach.Status);
        else Status = $"前往头目 {distance:0} 米处，等待开怪。";
    }

    private static Vector3? LoadedBoss(ArrivalFate? fate, ushort id, int start) =>
        fate is { BossAlive: true, BossPosition: { } position } && fate.Id == id
        && (start == 0 || fate.StartedAt == start) && NavigationRules.Finite(position) ? position : null;

    private void WaitAtLoadedBoss(ArrivalFrame frame, ArrivalFate fate, Vector3 boss, DateTimeOffset now)
    {
        if (navigationOnly || !ArrivalRules.IsPulled(fate).Started)
        {
            ApproachBoss(frame, boss, fate.WaitDistance, now);
            return;
        }
        travel.Stop(); approach.Stop();
        bossApproached = true;
        phase = Phase.Prepare;
        phaseAt = nextAction = now;
        attempts = 0;
        Status = "已检测到开怪，准备接管战斗。";
    }
    private void BeginParkDelay(DateTimeOffset now)
    {
        goal = null;
        goalFate = null;
        encounter = null;
        phase = Phase.ParkDelay;
        phaseAt = now;
        Status = "等待新坐标，3秒后前往最近水晶。";
    }
    private void ContinueEating(ArrivalFrame frame, IReadOnlyList<FateEntry> fates, DateTimeOffset now)
    {
        var next = fates.Where(f => NavigationRules.Finite(f.Position) && !Same(f, encounter) && !Finished(f))
            .MinBy(f => NavigationRules.Distance(frame.Player, f.Position));
        if (next != null) { QueueEating(next); return; }
        if (IslandCrystalRoutes.CampDestination(context, frame.Player) is not { } camp)
        { Stop("未找到当前岛的入口营地。"); return; }
        goal = null; goalFate = null; encounter = null;
        travel.Start(camp, context, frame.Player, [], now, waitWithoutFate: false);
        if (!travel.Active) { Stop($"返回营地失败：{travel.Status}"); return; }
        phase = Phase.ReturnCamp;
        Status = "本岛暂无其他活动 FATE，返回营地。";
    }
    private FateEntry? LiveFate(ushort id, IReadOnlyList<FateEntry> fates) => fates.FirstOrDefault(f => f.Id == id && !Finished(f));
    private bool Finished(FateEntry fate) => finishedFates.TryGetValue(fate.Id, out var start)
        && (fate.StartedAt <= 0 || start <= 0 || fate.StartedAt <= start);
    private FateEntry? FreshFate(MapDestination destination, IReadOnlyList<FateEntry> fates) => NavigationRules.FindFate(destination.Position,
        fates.Where(f => !Same(f, encounter) && !Finished(f)), 100);
    private static bool Same(FateEntry fate, (ushort Id, int Start)? token) => token is { } value
        && fate.Id == value.Id && (fate.StartedAt <= 0 || value.Start <= 0 || fate.StartedAt <= value.Start);
    private void Park(Vector3 player, DateTimeOffset now)
    {
        travel.Stop(); approach.Stop();
        if (repeat && !combat.Disable()) { Stop("未能关闭战斗控制，停止水晶待机。"); return; }
        standbyCrystal = IslandCrystalRoutes.Nearest(context.Territory, player);
        if (standbyCrystal == null) { Stop("未找到当前岛屿的水晶。"); return; }
        phase = Phase.Park; phaseAt = nextAction = now; attempts = 0; lastAction = null; parkStarted = false;
        standbyAnchor = null;
        goal = null; goalFate = null; encounter = null; pulled = false;
        Status = "前往最近水晶待机。";
    }
    private void TickPark(ArrivalFrame frame, DateTimeOffset now)
    {
        var live = travel.LocateCrystal(standbyCrystal!);
        if (live is { } observed && standbyAnchor is { } previous
            && Vector3.Distance(observed, previous) > 1)
        {
            approach.Stop(); parkStarted = false; phase = Phase.Park;
        }
        if (phase == Phase.Standby) { Status = "已在最近水晶附近待机，等待新NM或车头坐标。"; return; }
        if (now - phaseAt >= TimeSpan.FromMinutes(5)) { Stop("前往水晶待机超时。"); return; }
        if (!frame.Safe) return;
        if (!parkStarted)
        {
            if (frame.InCombat && !frame.Mounted || frame.Casting) return;
            if (!frame.Mounted) { Act(mounts.Mount, "召唤所选坐骑", now); return; }
            if (mesh.Busy) { Status = "等待寻路结束后规划水晶待机。"; return; }
            if (mesh.Route.Count > 0) { Stop("其他导航占用中，停止水晶待机。"); return; }
            // A high sky ray can hit a nearby hut's roof. Prefer the live crystal's floor,
            // otherwise the player's current layer, and refine when the crystal loads.
            var centre = live ?? AutoTriggerRules.Ground(mesh,
                standbyCrystal!.Position with { Y = frame.Player.Y }, false)
                ?? AutoTriggerRules.Ground(mesh, standbyCrystal!.Position, true);
            if (centre == null || !NavigationRules.Finite(centre.Value)) { Stop("水晶附近没有可行走地面。"); return; }
            // A provisional mesh lookup supplies height only. Keep the radius centred on the crystal.
            var anchor = live ?? (standbyCrystal!.Position with { Y = centre.Value.Y });
            standbyAnchor = anchor;
            var point = ArrivalRules.Position(frame.Player, anchor, 7);
            approach.Start(new(context.Territory, context.Map, point, "最近水晶附近"), context, frame.Player, [], now, false, unknownHeight: false,
                groundFilter: p => NavigationRules.Distance(p, anchor) <= 10 && Math.Abs(p.Y - anchor.Y) <= 3);
            parkStarted = true;
            if (!approach.Active) { Stop(approach.Status); return; }
        }
        approach.Tick(context, frame.Player, frame.Safe, [], now);
        if (approach.Active) { Status = "前往最近水晶附近待机。"; return; }
        if (approach.TakeArrival() == null) { Stop(approach.Status); return; }
        if (!repeat) { Stop("已到达最近水晶附近，原地待机。"); return; }
        phase = Phase.Standby; Status = "已在最近水晶附近待机，等待新NM或车头坐标。";
    }
    public void Stop(string reason = "已停止跟随伐木。")
    {
        Active = false; pending = goal = last = null; pendingFate = null; encounter = null; goalFate = null;
        navigationOnly = false;
        eatAvailable = false;
        leaderHold = userPaused = false; idleUntil = default;
        retryAt = default; retryCount = 0; retryGoal = null; retryReason = null; standbyAnchor = null;
        travel.Stop(); approach.Stop(); combat.Disable(); Status = reason;
    }
    public void Dispose() { Stop(); approach.Dispose(); }
}
