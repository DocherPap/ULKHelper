using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FateState = Dalamud.Game.ClientState.Fates.FateState;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using ULKHelper.Bunnies;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal sealed partial class AutomationHelpers
{
    // CN EventItem.Singular, verified in local sqpack.
    private const uint LuckyCarrot = 2002482;
    private readonly IslandTravelHelper bunnyTravel;
    private readonly HashSet<(ushort Id, int Start)> bunnyHandled = [];
    private IBunnyTracker? bunnyTracker;
    private WorldContext? bunnyWorld;
    private HashSet<ushort> bunnySelected = [];
    private (ushort Id, int Start) bunnyFate;
    private enum BunnyPhase { Waiting, Travel, Fight, Reward, Search, Chest }
    private BunnyPhase bunnyPhase;
    private DateTimeOffset bunnyStarted, bunnyPhaseAt, nextBunnyTick, nextBunnyCarrot, lastBunnyClue, nextBunnySync;
    private Vector3? bunnyGoal, lastFailedClueOrigin;
    private bool bunnyCombatOwned, bunnyCombatEnabled, bunnyDirectionOnly;
    private int bunnySyncAttempts, bunnyCarrotFailures;
    private int bunnyLimit;
    private bool bunnyTreasureCounted;
    private DateTimeOffset bunnyHuntStarted;
    public int BunnyFoundCount { get; private set; }
    public bool BunnyAutomationActive => bunnyWorld.HasValue;
    public string BunnyAutomationStatus { get; private set; } = "尚未开始自动挖宝。";
    public void AttachBunnyTracker(IBunnyTracker tracker) => bunnyTracker = tracker;

    public void SetBunnyAutomation(uint territory, IReadOnlyCollection<ushort> selected, bool enabled, int limit = 0)
    {
        var ids = selected.ToHashSet();
        commands.Enqueue(() =>
        {
            if (!enabled) { StopBunnyAutomation("已停止自动挖宝。"); return; }
            if (BunnyAutomationActive) return;
            var world = Context();
            var island = ElementalMaps?.Islands.FirstOrDefault(i => i.Territory == world.Territory);
            if (bunnyTracker == null || island == null || world.Territory != territory || ids.Count is < 1 or > 2
                || ids.Any(id => !island.Fates.Any(f => f.Seed.Bunny && f.Seed.Id == id))
                || !CanMove() || SurveyActive || commonJourney.Active || trigger.Active || follow.Active
                || navigation.Active || combat.CleanupPending)
            { BunnyAutomationStatus = "请在对应岛屿脱战后选择兔子 FATE，并停止其他任务。"; return; }
            bunnySelected = ids;
            bunnyLimit = Math.Max(0, limit);
            BunnyFoundCount = 0;
            bunnyWorld = world;
            bunnyHandled.Clear();
            bunnyStarted = DateTimeOffset.UtcNow;
            nextBunnyTick = default;
            if (bunnyTracker.Snapshot.Active && bunnyTracker.Snapshot.Territory == world.Territory)
                BeginBunnySearch(bunnyStarted);
            else SetBunnyPhase(BunnyPhase.Waiting, bunnyStarted, "等待所选幸福兔 FATE（至少剩余 10 分钟）。");
        });
    }

    private void SetBunnyPhase(BunnyPhase phase, DateTimeOffset now, string status)
    {
        bunnyPhase = phase;
        bunnyPhaseAt = now;
        BunnyAutomationStatus = status;
    }

    private void StopBunnyCombat()
    {
        if (!bunnyCombatOwned) return;
        bunnyCombatOwned = bunnyCombatEnabled = false;
        combat.EndTrigger();
    }

    private bool PauseBunnyCombat()
    {
        if (!bunnyCombatEnabled) return true;
        if (!combat.Disable())
        {
            StopBunnyAutomation("未能暂停幸福兔战斗控制。");
            return false;
        }
        bunnyCombatEnabled = false;
        return true;
    }

    private void StopBunnyAutomation(string status)
    {
        if (!BunnyAutomationActive) return;
        bunnyWorld = null;
        bunnyTravel.Stop();
        StopBunnyCombat();
        BunnyAutomationStatus = status;
    }

    private void BeginBunnySearch(DateTimeOffset now)
    {
        bunnyGoal = lastFailedClueOrigin = null;
        bunnyDirectionOnly = false;
        bunnyTreasureCounted = false;
        bunnyHuntStarted = now;
        bunnyCarrotFailures = 0;
        lastBunnyClue = nextBunnyCarrot = now;
        SetBunnyPhase(BunnyPhase.Search, now, "已获得指引财宝，开始寻找宝箱。");
    }

    private unsafe void TickBunnyAutomation(WorldContext world, bool loading, DateTimeOffset now)
    {
        if (!BunnyAutomationActive || now < nextBunnyTick) return;
        nextBunnyTick = now.AddMilliseconds(250);
        if (world != bunnyWorld || !client.IsLoggedIn || now - bunnyStarted >= TimeSpan.FromHours(2))
        { StopBunnyAutomation("已离开原副本或达到两小时上限。"); return; }
        var phaseLimit = bunnyPhase is BunnyPhase.Search or BunnyPhase.Chest or BunnyPhase.Fight ? 15 : 5;
        if (bunnyPhase != BunnyPhase.Waiting && now - bunnyPhaseAt >= TimeSpan.FromMinutes(phaseLimit))
        { StopBunnyAutomation("自动挖宝当前阶段已超时。"); return; }
        if (loading && bunnyTravel.Transferring) return;
        if (loading || objects.LocalPlayer is not { IsDead: false } player)
        { StopBunnyAutomation("角色状态变化，已停止自动挖宝。"); return; }
        var safe = SafeState(bunnyTravel.Interacting || bunnyTravel.Transferring);
        if (!safe)
        {
            if (bunnyPhase == BunnyPhase.Chest || bunnyTravel.Transferring) return;
            StopBunnyAutomation("当前有其他交互，已停止自动挖宝。");
            return;
        }
        var mounted = conditions[ConditionFlag.Mounted];
        var inCombat = conditions[ConditionFlag.InCombat];
        var snapshot = bunnyTracker!.Snapshot;
        var hasBuff = player.StatusList.Any(s => s.StatusId == 1531);
        if ((bunnyPhase is BunnyPhase.Search or BunnyPhase.Chest) && !bunnyTreasureCounted
            && snapshot.Territory == world.Territory && snapshot.LastTreasureAt is { } found && found >= bunnyHuntStarted)
        {
            bunnyTreasureCounted = true;
            BunnyFoundCount++;
            bunnyTravel.Stop();
            if (bunnyLimit > 0 && BunnyFoundCount >= bunnyLimit)
                StopBunnyAutomation($"已挖出 {BunnyFoundCount} 个宝箱，达到目标。请手动打开宝箱。");
            else if (bunnyPhase != BunnyPhase.Chest)
                SetBunnyPhase(BunnyPhase.Chest, now, $"已挖出 {BunnyFoundCount} 个宝箱，请手动打开。");
            return;
        }
        if (bunnyPhase == BunnyPhase.Waiting)
        {
            if (combat.CleanupPending || inCombat) return;
            if (hasBuff) { BeginBunnySearch(now); return; }
            var next = fates.Where(f => f != null && f.TerritoryType.RowId == world.Territory
                && f.State == FateState.Running && f.TimeRemaining >= 600 && bunnySelected.Contains(f.FateId)
                && !bunnyHandled.Contains((f.FateId, f.StartTimeEpoch))).MinBy(f => f.TimeRemaining);
            if (next == null) return;
            bunnyFate = (next.FateId, next.StartTimeEpoch);
            bunnyHandled.Add(bunnyFate);
            bunnyTravel.Start(new(world.Territory, world.Map, next.Position, next.Name.TextValue),
                world, player.Position, [], now, mounts.Selected != 0, waitWithoutFate: false);
            if (!bunnyTravel.Active) { StopBunnyAutomation(bunnyTravel.Status); return; }
            SetBunnyPhase(BunnyPhase.Travel, now, "前往幸福兔 FATE。");
            return;
        }

        if (bunnyPhase is BunnyPhase.Travel or BunnyPhase.Fight)
        {
            var activeFate = fates.FirstOrDefault(f => f != null && f.TerritoryType.RowId == world.Territory
                && f.FateId == bunnyFate.Id && f.StartTimeEpoch == bunnyFate.Start && f.State == FateState.Running);
            if (activeFate == null && !bunnyTravel.Transferring)
            {
                bunnyTravel.Stop();
                StopBunnyCombat();
                SetBunnyPhase(BunnyPhase.Reward, now, "FATE 已结束，等待指引财宝。");
                return;
            }
            if (bunnyPhase == BunnyPhase.Travel)
            {
                bunnyTravel.Tick(world, player.Position, safe, false, mounted, player.IsCasting, [], now, inCombat);
                if (bunnyTravel.Active) { BunnyAutomationStatus = bunnyTravel.Status; return; }
                if (bunnyTravel.TakeArrival() == null) { StopBunnyAutomation(bunnyTravel.Status); return; }
                bunnySyncAttempts = 0;
                nextBunnySync = default;
                SetBunnyPhase(BunnyPhase.Fight, now, "已到达幸福兔 FATE，正在确认同步。");
                return;
            }
            if (player.IsCasting) return;
            var manager = FateManager.Instance();
            if (manager == null || manager->GetCurrentFateId() != bunnyFate.Id)
            { PauseBunnyCombat(); return; }
            var level = TriggerGameReader.Level();
            if (level == null || activeFate!.MaxLevel == 0) { PauseBunnyCombat(); return; }
            var needsSync = level.Value > activeFate.MaxLevel;
            if (mounted || needsSync && manager->SyncedFateId != bunnyFate.Id)
            {
                if (!PauseBunnyCombat()) return;
                if (now < nextBunnySync) return;
                if (bunnySyncAttempts++ >= 6) { StopBunnyAutomation("未能下坐骑或完成 FATE 等级同步。"); return; }
                nextBunnySync = now.AddSeconds(2);
                if (mounted) mounts.Dismount();
                else combat.Sync(bunnyFate.Id);
                return;
            }
            var target = objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.IsTargetable && !n.IsDead
                && ((Character*)n.Address)->FateId == bunnyFate.Id
                && ActionManager.ClassifyTarget((Character*)n.Address) == ActionManager.TargetCategory.Enemy)
                .MinBy(n => Vector3.DistanceSquared(player.Position, n.Position));
            if (target == null)
            {
                PauseBunnyCombat();
                return;
            }
            if (!bunnyCombatOwned)
            {
                bunnyCombatOwned = true;
                var error = combat.BeginTrigger(true, true, true) ?? combat.PrepareStart();
                if (error != null) { StopBunnyAutomation(error); return; }
            }
            if (!combat.Target(target.GameObjectId)) return;
            if (!bunnyCombatEnabled)
            {
                bunnyCombatEnabled = combat.EnableTrigger(true);
                if (!bunnyCombatEnabled) { StopBunnyAutomation(combat.Error ?? "无法启动 RSR / BMR AI。"); return; }
            }
            else if (!combat.StationaryHealthy) { StopBunnyAutomation("RSR / BMR 控制已变化。"); return; }
            BunnyAutomationStatus = "正在参加幸福兔 FATE，目标限定为该 FATE 内的怪物。";
            return;
        }

        if (bunnyPhase == BunnyPhase.Reward)
        {
            if (hasBuff) BeginBunnySearch(now);
            else if (now - bunnyPhaseAt >= TimeSpan.FromSeconds(10))
                SetBunnyPhase(BunnyPhase.Waiting, now, "未获得指引财宝，等待下一场幸福兔 FATE。");
            return;
        }
        if (bunnyPhase == BunnyPhase.Chest)
        {
            if (!hasBuff && now - bunnyPhaseAt >= TimeSpan.FromSeconds(10))
                SetBunnyPhase(BunnyPhase.Waiting, now, "本轮寻宝结束，等待下一场幸福兔 FATE。");
            return;
        }
        if (!hasBuff)
        {
            bunnyTravel.Stop();
            SetBunnyPhase(BunnyPhase.Chest, now, "指引财宝已结束，请确认附近是否有待打开的宝箱。");
            return;
        }
        if (bunnyTravel.Active)
        {
            bunnyTravel.Tick(world, player.Position, safe, false, mounted, player.IsCasting, [], now, inCombat);
            if (!bunnyTravel.Active && bunnyTravel.TakeArrival() == null)
                BunnyAutomationStatus = $"前往线索失败，等待下次提示：{bunnyTravel.Status}";
        }
        if (bunnyTravel.Transferring) return;
        var clue = snapshot.Territory == world.Territory ? snapshot.Clues.LastOrDefault() : null;
        if (clue != null && clue.Time > lastBunnyClue)
        {
            lastBunnyClue = clue.Time;
            if (clue.Distance == BunnyDistance.Here)
            {
                bunnyTravel.Stop();
                SetBunnyPhase(BunnyPhase.Chest, now, "已找到宝箱，请手动打开。");
                return;
            }
            UpdateBunnyGoal(world, player.Position, snapshot, clue, now);
        }
        if (now < nextBunnyCarrot || player.IsCasting) return;
        if (mounted)
        {
            nextBunnyCarrot = now.AddSeconds(2);
            if (!mounts.Dismount() && ++bunnyCarrotFailures >= 3)
                StopBunnyAutomation("无法下坐骑使用幸运胡萝卜。");
            return;
        }
        nextBunnyCarrot = now.AddSeconds(10);
        var action = ActionManager.Instance();
        if (action != null && action->GetActionStatus(ActionType.EventItem, LuckyCarrot) == 0
            && action->UseAction(ActionType.EventItem, LuckyCarrot))
            bunnyCarrotFailures = 0;
        else if (++bunnyCarrotFailures >= 3)
            StopBunnyAutomation("连续三次无法使用幸运胡萝卜，已停止。");
    }

    private void UpdateBunnyGoal(WorldContext world, Vector3 player, BunnySnapshot snapshot, BunnyClue clue, DateTimeOffset now)
    {
        if (snapshot.Candidates.Count == 0 && !bunnyDirectionOnly)
        {
            bunnyTravel.Stop();
            bunnyGoal = null;
            bunnyDirectionOnly = lastFailedClueOrigin is { } previous && NavigationRules.Distance(previous, clue.Position) <= 3;
            lastFailedClueOrigin = clue.Position;
            bunnyTracker!.Reset();
            if (!bunnyDirectionOnly) { BunnyAutomationStatus = "没有匹配点位，已重置筛选，等待下次胡萝卜。"; return; }
        }
        else if (!bunnyDirectionOnly) lastFailedClueOrigin = null;

        var island = ElementalMaps?.Islands.FirstOrDefault(i => i.Territory == world.Territory && i.MapId == world.Map);
        if (island == null) return;
        Vector3 goal;
        if (!bunnyDirectionOnly && snapshot.Candidates.Count == 1
            && snapshot.Points.FirstOrDefault(p => snapshot.Candidates.Contains(p.Id)) is { } unique)
            goal = MapProjection.MapToWorld(new(unique.X, unique.Y), island);
        else if (clue.Direction is { } direction)
        {
            var (min, max) = BunnyRules.Range(clue.Distance);
            var distance = float.IsFinite(max) ? (min + max) / 2 : 200;
            var angle = (int)direction * MathF.PI / 4;
            goal = clue.Position + new Vector3(MathF.Sin(angle) * distance, 0, -MathF.Cos(angle) * distance);
            var uv = MapProjection.ToUv(MapProjection.WorldToMap(goal, island), island.SizeFactor);
            goal = MapProjection.MapToWorld(MapProjection.ToMap(Vector2.Clamp(uv, new(.01f), new(.99f)), island.SizeFactor), island);
        }
        else return;
        if (bunnyTravel.Active && bunnyGoal is { } old && NavigationRules.Distance(old, goal) <= 5) return;
        bunnyTravel.Stop();
        bunnyGoal = goal;
        if (NavigationRules.Distance(player, goal) <= 3) return;
        bunnyTravel.Start(new(world.Territory, world.Map, goal, "幸福兔宝箱线索"), world, player, [], now, waitWithoutFate: false);
        BunnyAutomationStatus = bunnyTravel.Active ? "正在前往宝箱线索。" : bunnyTravel.Status;
    }
}
