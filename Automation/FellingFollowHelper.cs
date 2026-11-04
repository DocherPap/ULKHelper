using System.Numerics;

namespace ULKHelper.Automation;

internal sealed record EncounterObservation(ArrivalFate? Fate, bool Ended = false);
internal interface IFellingCombat
{
    string? Error => null;
    string? PrepareStart() => CheckReady();
    string? CheckReady();
    bool Target(ulong bossId);
    bool Sync(ushort fateId);
    bool Enable();
    bool Disable();
    bool Healthy { get; }
}

// Explicitly enabled session. New coordinates are coalesced while fighting, never interrupt combat.
internal sealed class FellingFollowHelper(IslandTravelHelper travel, INavigationMesh mesh, IArrivalActions mounts, IFellingCombat combat) : IDisposable
{
    private readonly DestinationNavigationHelper approach = new(mesh);
    private enum Phase { Waiting, Travel, WaitApproach, Prepare, Fight, Remount, Park, Standby }
    private Phase phase;
    private WorldContext context;
    private MapDestination? pending, goal, last;
    private DateTimeOffset sessionAt, phaseAt, nextAction, missingAt;
    private int attempts;
    private (ushort Id, int Start)? encounter;
    private bool pulled;
    private bool repeat;
    private string? lastAction;
    private MapDestination? watch;
    private (ushort Id, int Start)? completed;
    private IslandCrystal? standbyCrystal;
    private bool parkStarted;
    private readonly Dictionary<ushort, int> finishedFates = new();
    public bool Active { get; private set; }
    public bool Following => Active && repeat;
    public bool Transferring => Active && travel.Transferring;
    public bool Interacting => Active && travel.Interacting;
    public MapDestination? Goal => goal;
    public ushort? FateId => encounter?.Id;
    public string Status { get; private set; } = "尚未开始跟随伐木。";
    public bool Start(WorldContext world, DateTimeOffset now, bool repeatFollowing = true)
    {
        Stop();
        if (world.Territory is not (732 or 763 or 795 or 827)) { Status = "请先进入优雷卡岛内。"; return false; }
        var error = combat.PrepareStart();
        if (error != null) { Status = error; return false; }
        repeat = repeatFollowing; lastAction = null; completed = null; finishedFates.Clear(); watch = null; context = world; sessionAt = phaseAt = now; phase = Phase.Waiting; Active = true;
        Status = "等待车头的新坐标（本次跟随最长2小时）。"; return true;
    }
    public void Receive(MapDestination destination)
    {
        if (!Active || destination.Territory != context.Territory || destination.Map != context.Map
            || !NavigationRules.Finite(destination.Position)) return;
        if (last != null && NavigationRules.Distance(last.Position, destination.Position) <= 1) return;
        last = pending = destination;
    }
    public bool StartAtBoss(WorldContext world, MapDestination destination, ArrivalFate fate, Vector3 boss,
        Vector3 player, DateTimeOffset now)
    {
        if (!Start(world, now, repeatFollowing: false)) return false;
        goal = destination;
        encounter = (fate.Id, fate.StartedAt);
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
        var standOff = ArrivalRules.Position(player, boss, 20);
        approach.Start(destination with { Position = standOff }, world, player, [], now, false);
        if (!approach.Active) { Stop(approach.Status); return false; }
        Status = "前往头目20米处，等待开怪。";
        return true;
    }
    public void Tick(ArrivalFrame frame, bool transferScreen, IReadOnlyList<FateEntry> fates, EncounterObservation observation, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (frame.World != context || !frame.Safe && !(Transferring && transferScreen)) { Stop("角色状态或地图变化，跟随已停止。"); return; }
            if (now - sessionAt >= TimeSpan.FromHours(2)) { Stop("本次跟随达到2小时上限。"); return; }
            if (pending == null && !frame.InCombat && watch != null && phase is Phase.Park or Phase.Standby && FreshFate(watch, fates) != null)
            { pending = watch; watch = null; }
            if (pending != null && !frame.InCombat && phase is Phase.Waiting or Phase.Travel or Phase.Prepare or Phase.Park or Phase.Standby)
            {
                if (!combat.Disable()) { Stop("未能关闭上一场战斗控制。"); return; }
                var error = combat.CheckReady();
                if (error != null) { Stop(error); return; }
                var next = pending; pending = null;
                if (repeat && (completed != null || pulled) && FreshFate(next, fates) == null)
                {
                    watch = next;
                    if (phase is not (Phase.Park or Phase.Standby)) Park(frame, now);
                }
                else
                {
                watch = null; travel.Stop(); approach.Stop(); goal = next;
                encounter = null; pulled = false; attempts = 0; missingAt = default; phaseAt = now;
                phase = Phase.Travel;
                travel.Start(goal, context, frame.Player, fates, now, true);
                if (!travel.Active) { Stop(travel.Status); return; }
                }
            }
            if (phase is Phase.Park or Phase.Standby) { TickPark(frame, now); return; }
            if (phase == Phase.Waiting) return;
            if (phase == Phase.WaitApproach)
            {
                if (observation.Ended) { Stop("目标FATE已结束。"); return; }
                if (observation.Fate is { } waiting && ArrivalRules.IsPulled(waiting).Started)
                    approach.Stop();
                else
                {
                    approach.Tick(frame.World, frame.Player, frame.Safe, [], now);
                    if (approach.Active) return;
                    if (approach.TakeArrival() == null) { Stop(approach.Status); return; }
                }
                phase = Phase.Prepare; phaseAt = nextAction = now; attempts = 0;
            }
            if (phase == Phase.Travel)
            {
                travel.Tick(frame.World, frame.Player, frame.Safe, transferScreen, frame.Mounted, frame.Casting, fates, now, frame.InCombat);
                Status = travel.Status;
                if (travel.Active) return;
                if (travel.TakeArrival() == null) { Stop(travel.Status); return; }
                phase = Phase.Prepare; phaseAt = nextAction = now; attempts = 0;
            }
            if (phase == Phase.Remount)
            {
                if (!combat.Disable()) { Status = "等待关闭战斗控制。"; return; }
                if (now - phaseAt > TimeSpan.FromSeconds(40)) { Stop("战后无法召唤坐骑，跟随已停止。"); return; }
                if (frame.InCombat || frame.Casting) { Status = "战斗控制已关闭，等待脱战后上坐骑。"; return; }
                if (frame.Mounted)
                {
                    if (!repeat) { Stop("本次战斗已结束并上坐骑。"); return; }
                    var latest = pending ?? last ?? goal;
                    if (latest != null && FreshFate(latest, fates) == null)
                    { watch = latest; pending = null; Park(frame, now); return; }
                    pending ??= latest;
                    phase = Phase.Waiting; goal = null; encounter = null; Status = "已上坐骑，等待车头的新坐标。"; return;
                }
                Act(mounts.Mount, "召唤所选坐骑", now); return;
            }
            if (observation.Ended && encounter != null) { Finish(now); RemountImmediately(frame, now); return; }
            var fate = observation.Fate;
            if (phase == Phase.Prepare && frame.Mounted && !frame.Casting)
            {
                if (fate != null && (pulled || ArrivalRules.IsPulled(fate).Started) && fate.BossId != 0 && fate.BossTargetable)
                    combat.Target(fate.BossId);
                approach.Stop(); Act(mounts.Dismount, "下坐骑", now); return;
            }
            if (fate == null)
            {
                if (phase == Phase.Fight)
                {
                    combat.Disable();
                    if (missingAt == default) missingAt = now;
                    if (!frame.InCombat && now - missingAt >= TimeSpan.FromSeconds(5)) { Finish(now); return; }
                    if (now - missingAt >= TimeSpan.FromSeconds(15)) { Stop("目标事件不可见，已关闭战斗控制。"); return; }
                }
                else if (repeat && completed != null) { watch = goal ?? last; Park(frame, now); return; }
                else if (now - phaseAt > TimeSpan.FromMinutes(5)) Stop("等待NM超时。");
                Status = "等待目的地附近的目标NM。"; return;
            }
            missingAt = default;
            if (encounter is { } bound && (bound.Id != fate.Id || bound.Start > 0 && fate.StartedAt > 0 && bound.Start != fate.StartedAt))
            { Stop("目标NM场次变化。"); return; }
            encounter = (fate.Id, fate.StartedAt > 0 ? fate.StartedAt : encounter?.Start ?? 0);
            if (fate.BossId != 0 && !fate.BossAlive) { Finish(now); RemountImmediately(frame, now); return; }
            if (phase == Phase.Fight && now - phaseAt > TimeSpan.FromMinutes(20)) { Stop("单场战斗超过20分钟。"); return; }
            if (phase == Phase.Prepare)
            {
                if (now - phaseAt > TimeSpan.FromMinutes(5)) { Stop("准备战斗超过5分钟。"); return; }
                pulled |= ArrivalRules.IsPulled(fate).Started;
                if (pulled && fate.BossId != 0 && fate.BossTargetable && !combat.Target(fate.BossId)) { Stop("无法锁定NM头目。"); return; }
                if (frame.Casting) return;
                if (frame.Mounted) { approach.Stop(); Act(mounts.Dismount, "下坐骑", now); return; }
                if (!pulled) { Status = "已到达并下坐骑，等待NM开怪。"; return; }
                if (fate.BossId == 0 || !fate.BossTargetable) { Status = "等待确认可选中的NM头目。"; return; }
                if (!combat.Target(fate.BossId)) { Stop("无法锁定NM头目。"); return; }
                if (fate.NeedsSync == null) { Status = "等待确认等级同步需求。"; return; }
                // Once pulled, hand movement to BossMod; never issue another stand-off destination.
                approach.Stop();
                if (fate.NeedsSync.Value && !fate.Synced)
                { Act(() => combat.Sync(fate.Id), "等级同步", now); return; }
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
                if (!combat.Healthy) { Stop("战斗插件状态改变，已停止跟随。"); return; }
                if (fate.BossId == 0 || !fate.BossTargetable) { combat.Disable(); phase = Phase.Prepare; phaseAt = now; return; }
                if (!combat.Target(fate.BossId)) Stop("无法保持NM头目目标。");
            }
        }
        catch (Exception ex) { Stop("跟随失败：" + ex.Message); }
    }
    private void Act(Func<bool> action, string label, DateTimeOffset now)
    {
        if (lastAction != label) { attempts = 0; nextAction = now; lastAction = label; }
        if (now < nextAction) return;
        if (attempts++ >= 3) { Stop(label + "失败，已停止跟随。"); return; }
        nextAction = now.AddSeconds(3); action(); Status = "等待" + label + "完成。";
    }
    private void Finish(DateTimeOffset now)
    {
        completed = encounter;
        if (encounter is { } ended) finishedFates[ended.Id] = ended.Start;
        approach.Stop(); combat.Disable(); phase = Phase.Remount; phaseAt = nextAction = now; attempts = 0;
        Status = "目标事件已结束，关闭战斗控制并准备上坐骑。";
    }
    private void RemountImmediately(ArrivalFrame frame, DateTimeOffset now)
    {
        if (combat.Disable() && !frame.Mounted && !frame.InCombat && !frame.Casting)
            Act(mounts.Mount, "召唤所选坐骑", now);
    }
    private FateEntry? FreshFate(MapDestination destination, IReadOnlyList<FateEntry> fates) => NavigationRules.FindFate(destination.Position,
        fates.Where(f => !Same(f, encounter) && !(finishedFates.TryGetValue(f.Id, out var start)
            && (f.StartedAt <= 0 || start <= 0 || f.StartedAt <= start))));
    private static bool Same(FateEntry fate, (ushort Id, int Start)? token) => token is { } value
        && fate.Id == value.Id && (fate.StartedAt <= 0 || value.Start <= 0 || fate.StartedAt <= value.Start);
    private void Park(ArrivalFrame frame, DateTimeOffset now)
    {
        travel.Stop(); approach.Stop();
        if (!combat.Disable()) { Stop("未能关闭战斗控制，停止水晶待机。"); return; }
        standbyCrystal = IslandCrystalRoutes.Nearest(context.Territory, frame.Player);
        if (standbyCrystal == null) { Stop("未找到当前岛屿的水晶。"); return; }
        phase = Phase.Park; phaseAt = nextAction = now; attempts = 0; lastAction = null; parkStarted = false;
        goal = watch; encounter = null; pulled = false;
        Status = "车头坐标附近没有新的NM，前往最近水晶待机。";
    }
    private void TickPark(ArrivalFrame frame, DateTimeOffset now)
    {
        if (phase == Phase.Standby) { Status = "已在最近水晶附近待机，等待新NM或车头坐标。"; return; }
        if (now - phaseAt >= TimeSpan.FromMinutes(5)) { Stop("前往水晶待机超时。"); return; }
        if (!parkStarted)
        {
            if (frame.InCombat || frame.Casting) return;
            if (!frame.Mounted) { Act(mounts.Mount, "召唤所选坐骑", now); return; }
            if (mesh.Busy || mesh.Route.Count > 0) { Stop("其他导航占用中，停止水晶待机。"); return; }
            var centre = mesh.Ground(standbyCrystal!.Position, true);
            if (centre == null || !NavigationRules.Finite(centre.Value)) { Stop("水晶附近没有可行走地面。"); return; }
            var point = ArrivalRules.Position(frame.Player, centre.Value, 7);
            approach.Start(new(context.Territory, context.Map, point, "最近水晶附近"), context, frame.Player, [], now, false);
            parkStarted = true;
            if (!approach.Active) { Stop(approach.Status); return; }
        }
        approach.Tick(context, frame.Player, frame.Safe, [], now);
        if (approach.Active) { Status = "前往最近水晶附近待机。"; return; }
        if (approach.TakeArrival() == null) { Stop(approach.Status); return; }
        phase = Phase.Standby; Status = "已在最近水晶附近待机，等待新NM或车头坐标。";
    }
    public void Stop(string reason = "已停止跟随伐木。")
    {
        Active = false; pending = goal = last = watch = null; encounter = null;
        travel.Stop(); approach.Stop(); combat.Disable(); Status = reason;
    }
    public void Dispose() { Stop(); approach.Dispose(); }
}
