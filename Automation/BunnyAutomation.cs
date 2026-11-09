using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FateState = Dalamud.Game.ClientState.Fates.FateState;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ULKHelper.Bunnies;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal sealed partial class AutomationHelpers
{
    // CN EventItem.Singular, verified in local sqpack.
    private const uint LuckyCarrot = 2002482;
    // Local CN BNpcName rows for Happy Bunny.
    private const uint HappyBunny = 7248, HappyBunnyVariant = 11309;
    private ulong bunnyPullTarget;
    private Vector3? bunnyCombatDestination, bunnyFightPosition;
    private Queue<Vector3>? bunnyFightCandidates;
    private Func<Vector3, bool>? bunnyFightGround;
    private readonly IslandTravelHelper bunnyTravel;
    private readonly TravelMountHelper bunnySearchMount;
    private readonly HashSet<(ushort Id, int Start)> bunnyHandled = [];
    private IBunnyTracker? bunnyTracker;
    private WorldContext? bunnyWorld;
    private HashSet<ushort> bunnySelected = [];
    private (ushort Id, int Start) bunnyFate;
    private enum BunnyPhase { Waiting, Travel, Fight, Reward, Search, SearchFight, SearchRemount, Chest }
    private BunnyPhase bunnyPhase;
    private DateTimeOffset bunnyStarted, bunnyPhaseAt, nextBunnyTick, nextBunnyCarrot, lastBunnyClue, nextBunnySync, nextBunnyMove;
    private Vector3? bunnyGoal, lastFailedClueOrigin;
    private bool bunnyCombatOwned, bunnyCombatEnabled, bunnyDirectionOnly;
    private int bunnySyncAttempts, bunnyCarrotFailures, bunnyVeryNearClues;
    private int bunnyLimit;
    private int bunnyMinimumSeconds;
    private bool bunnyTreasureCounted, bunnySearchOnly, bunnyStandbyIssued, bunnyStandbyArrived;
    private DateTimeOffset bunnyHuntStarted;
    private BunnyChestSighting? bunnyChestTarget;
    private DateTimeOffset nextBunnyChestAction;
    private int bunnyChestDismounts, bunnyChestInteractions;
    private bool bunnyChestApproachStarted;
    private bool bunnySearchCombatPending, bunnySearchCombatOwned, bunnySearchAtGoal;
    private ulong bunnySearchCombatTarget;
    private int bunnySearchDismounts;
    private DateTimeOffset nextBunnySearchDismount;
    public int BunnyFoundCount { get; private set; }
    public bool BunnyAutomationActive => bunnyWorld.HasValue;
    public string BunnyAutomationStatus { get; private set; } = "尚未开始自动挖宝。";
    public void AttachBunnyTracker(IBunnyTracker tracker) => bunnyTracker = tracker;

    public void SetBunnyAutomation(uint territory, IReadOnlyCollection<ushort> selected, bool enabled, int limit = 0, int minimumMinutes = 5)
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
                || !CanMove() || RemoteActive || SurveyActive || commonJourney.Active || trigger.Active || follow.Active
                || navigation.Active || combat.CleanupPending)
            { BunnyAutomationStatus = "请在对应岛屿脱战后选择兔子 FATE，并停止其他任务。"; return; }
            bunnySearchOnly = false;
            bunnyStandbyIssued = false;
            bunnySelected = ids;
            bunnyLimit = Math.Max(0, limit);
            var minutes = Math.Clamp(minimumMinutes, 0, 30);
            bunnyMinimumSeconds = minutes * 60;
            BunnyFoundCount = 0;
            bunnyWorld = world;
            bunnyHandled.Clear();
            bunnyStarted = DateTimeOffset.UtcNow;
            nextBunnyTick = default;
            if (bunnyTracker.Snapshot.Active && bunnyTracker.Snapshot.Territory == world.Territory)
                BeginBunnySearch(bunnyStarted);
            else SetBunnyPhase(BunnyPhase.Waiting, bunnyStarted, minutes == 0 ? "等待所选幸福兔 FATE（不限剩余时间）。" : $"等待所选幸福兔 FATE（至少剩余 {minutes} 分钟）。");
        });
    }

    private void StartBunnyTreasure(WorldContext world, DateTimeOffset now)
    {
        if (bunnyTracker == null || world.Territory is not (763 or 795 or 827)
            || objects.LocalPlayer is not { } player || !player.StatusList.Any(s => s.StatusId == 1531))
        {
            commonStatus = "需要获得指引财宝后才能开始自动寻找宝藏。";
            return;
        }

        bunnySearchOnly = true;
        bunnyWorld = world;
        bunnyStarted = now;
        nextBunnyTick = default;
        bunnyLimit = 1;
        BunnyFoundCount = 0;
        BeginBunnySearch(now);
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
        ReleaseBunnySearchCombat();
        bunnySearchCombatOwned = false;
        BunnyAutomationStatus = status;
        if (bunnySearchOnly) commonStatus = status;
        bunnySearchOnly = false;
    }

    private void BeginBunnySearch(DateTimeOffset now)
    {
        bunnyGoal = lastFailedClueOrigin = null;
        bunnyDirectionOnly = false;
        bunnySearchCombatPending = bunnySearchAtGoal = false;
        bunnyTreasureCounted = false;
        bunnyHuntStarted = now;
        bunnyCarrotFailures = 0;
        bunnyVeryNearClues = 0;
        lastBunnyClue = default;
        nextBunnyCarrot = now;
        SetBunnyPhase(BunnyPhase.Search, now, "已获得指引财宝，等待幸运胡萝卜线索。");
    }

    private unsafe void TickBunnyAutomation(WorldContext world, bool loading, DateTimeOffset now)
    {
        if (!BunnyAutomationActive || now < nextBunnyTick) return;
        nextBunnyTick = now.AddMilliseconds(250);
        if (world != bunnyWorld || !client.IsLoggedIn || now - bunnyStarted >= TimeSpan.FromHours(2))
        { StopBunnyAutomation("已离开副本或达到两小时上限。"); return; }
        var phaseLimit = bunnyPhase switch
        {
            BunnyPhase.Chest => TimeSpan.FromSeconds(12),
            BunnyPhase.Search or BunnyPhase.Fight or BunnyPhase.SearchFight => TimeSpan.FromMinutes(15),
            BunnyPhase.SearchRemount => TimeSpan.FromSeconds(15),
            _ => TimeSpan.FromMinutes(5),
        };
        if (bunnyPhase != BunnyPhase.Waiting && now - bunnyPhaseAt >= phaseLimit)
        {
            StopBunnyAutomation(bunnyPhase == BunnyPhase.Chest ? "未能确认本轮宝箱已打开，自动寻宝已停止。"
                : "自动挖宝当前阶段已超时。");
            return;
        }
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
        if (bunnyPhase == BunnyPhase.Search && bunnyTravel.Active && (inCombat || bunnySearchCombatPending))
        {
            bunnySearchCombatPending = true;
            bunnyTravel.Tick(world, player.Position, safe, loading, mounted, player.IsCasting, [], now, inCombat);
            if (bunnyTravel.Active)
            {
                BunnyAutomationStatus = $"途中交战，先到当前线索终点 · {bunnyTravel.Status}";
                return;
            }
            bunnySearchCombatPending = false;
            if (bunnyTravel.TakeArrival() == null)
            { StopBunnyAutomation($"无法到达线索终点：{bunnyTravel.Status}"); return; }
            bunnySearchAtGoal = true;
            if (inCombat) BeginBunnySearchFight(now);
            else BeginBunnySearchRemount(now);
        }
        if (bunnyPhase == BunnyPhase.Search && bunnySearchAtGoal && inCombat)
            BeginBunnySearchFight(now);
        if (bunnyPhase == BunnyPhase.SearchFight)
        {
            TickBunnySearchFight(player, mounted, inCombat, now);
            return;
        }
        if (bunnyPhase == BunnyPhase.SearchRemount)
        {
            if (inCombat) { BeginBunnySearchFight(now); return; }
            if (!mounted && !EnsureTravelMount())
            { StopBunnyAutomation("脱战后找不到可用的坐骑。"); return; }
            var mount = bunnySearchMount.Tick(mounted, player.IsCasting, false, now);
            if (mount == TravelMountResult.Failed) { StopBunnyAutomation("脱战后无法上坐骑。"); return; }
            if (mount != TravelMountResult.Mounted) return;
            if (snapshot.Clues.LastOrDefault() is { } oldClue && oldClue.Time > lastBunnyClue)
                lastBunnyClue = oldClue.Time;
            nextBunnyCarrot = now;
            SetBunnyPhase(BunnyPhase.Search, now, "已脱战并上坐骑，继续检查指引财宝。");
        }
        if ((bunnyPhase is BunnyPhase.Search or BunnyPhase.Chest) && !bunnyTreasureCounted
            && snapshot.Territory == world.Territory && snapshot.LastTreasureAt is { } found && found >= bunnyHuntStarted)
        {
            bunnyTreasureCounted = true;
            BunnyFoundCount++;
            BeginBunnyChest(now, "已发现财宝，正在识别本轮宝箱。");
        }
        if (hasBuff && bunnyPhase is BunnyPhase.Waiting or BunnyPhase.Travel or BunnyPhase.Fight or BunnyPhase.Reward)
        {
            bunnyTravel.Stop();
            StopBunnyCombat();
            BeginBunnySearch(now);
        }
        if (bunnyPhase == BunnyPhase.Waiting)
        {
            if (combat.CleanupPending || inCombat && !bunnyTravel.Active) return;
            var next = fates.Where(f => f != null && f.TerritoryType.RowId == world.Territory
                && f.State == FateState.Running && f.TimeRemaining >= bunnyMinimumSeconds && bunnySelected.Contains(f.FateId)
                && !bunnyHandled.Contains((f.FateId, f.StartTimeEpoch))).MinBy(f => f.TimeRemaining);
            if (next == null)
            {
                if (!bunnyStandbyIssued)
                {
                    var island = ElementalMaps?.Islands.FirstOrDefault(i => i.Territory == world.Territory && i.MapId == world.Map);
                    var point = island?.Fates.Where(f => bunnySelected.Contains(f.Seed.Id))
                        .MinBy(f => NavigationRules.Distance(player.Position, MapProjection.MapToWorld(new(f.Seed.X, f.Seed.Y), island)));
                    if (island == null || point == null) return;
                    bunnyTravel.Start(new(world.Territory, world.Map, MapProjection.MapToWorld(new(point.Seed.X, point.Seed.Y), island), "兔子 FATE 待机"),
                        world, player.Position, [], now, summonMount: EnsureTravelMount(), waitWithoutFate: false,
                        nearbyGroundRadius: NavigationRules.BunnyNearbyGroundRadius, preferCrystals: true);
                    bunnyStandbyIssued = true;
                    bunnyStandbyArrived = false;
                }
                if (bunnyTravel.Active)
                {
                    bunnyTravel.Tick(world, player.Position, safe, false, mounted, player.IsCasting, [], now, inCombat);
                    BunnyAutomationStatus = bunnyTravel.Status;
                }
                else
                {
                    if (!bunnyStandbyArrived && bunnyTravel.TakeArrival() == null)
                    { StopBunnyAutomation("兔子待机导航失败：" + bunnyTravel.Status); return; }
                    bunnyStandbyArrived = true;
                    BunnyAutomationStatus = "在所选兔子 FATE 附近待机。";
                }
                return;
            }
            if (bunnyTravel.Transferring || bunnyTravel.Interacting)
            {
                bunnyTravel.Tick(world, player.Position, safe, loading, mounted, player.IsCasting, [], now, inCombat);
                return;
            }
            bunnyTravel.Stop();
            bunnyStandbyIssued = false;
            bunnyFate = (next.FateId, next.StartTimeEpoch);
            bunnyHandled.Add(bunnyFate);
            bunnyTravel.Start(new(world.Territory, world.Map, next.Position, next.Name.TextValue),
                world, player.Position, [], now, EnsureTravelMount(), waitWithoutFate: false, nearbyGroundRadius: Math.Clamp(next.Radius - 3, 0, NavigationRules.BunnyNearbyGroundRadius), preferCrystals: true);
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
                bunnyPullTarget = 0;
                nextBunnyMove = default;
                bunnyCombatDestination = bunnyFightPosition = null;
                bunnyFightCandidates = null;
                bunnyFightGround = null;
                SetBunnyPhase(BunnyPhase.Fight, now, "已到达幸福兔 FATE，正在确认同步。");
                return;
            }
            if (player.IsCasting) return;
            var manager = FateManager.Instance();
            if (manager == null || manager->GetCurrentFateId() != bunnyFate.Id)
            {
                bunnyTravel.Stop();
                bunnyCombatDestination = null;
                if (bunnyPullTarget != 0) combat.ReleasePullAttack();
                PauseBunnyCombat();
                BunnyAutomationStatus = "等待进入本场幸福兔 FATE 的有效范围。";
                return;
            }
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
            TickBunnyFight(world, player.Position, activeFate.Position, activeFate.Radius, now);
            return;
        }

        if (bunnyPhase == BunnyPhase.Reward)
        {
            if (now - bunnyPhaseAt >= TimeSpan.FromSeconds(10))
                SetBunnyPhase(BunnyPhase.Waiting, now, "未获得指引财宝，等待下一场幸福兔 FATE。");
            return;
        }
        if (bunnyPhase == BunnyPhase.Chest || !hasBuff)
        {
            if (bunnyPhase != BunnyPhase.Chest)
                BeginBunnyChest(now, "指引财宝已结束，正在确认本轮发现结果。");
            TickBunnyChest(world, player, snapshot, mounted, inCombat, now);
            return;
        }
        if (bunnyTravel.Active)
        {
            bunnyTravel.Tick(world, player.Position, safe, false, mounted, player.IsCasting, [], now, inCombat);
            if (bunnyTravel.Active) BunnyAutomationStatus = bunnyTravel.Status;
            else
            {
                bunnySearchAtGoal = bunnyTravel.TakeArrival() != null;
                if (!bunnySearchAtGoal) BunnyAutomationStatus = $"前往线索失败，等待下次提示：{bunnyTravel.Status}";
            }
        }
        if (bunnyTravel.Transferring) return;
        var clue = snapshot.Active && snapshot.Territory == world.Territory ? snapshot.Clues.LastOrDefault() : null;
        if (clue != null && clue.Time > lastBunnyClue)
        {
            lastBunnyClue = clue.Time;
            if (clue.Distance == BunnyDistance.VeryNear) bunnyVeryNearClues++;
            if (clue.Distance == BunnyDistance.Here)
            {
                bunnyTravel.Stop();
                BunnyAutomationStatus = "已到财宝附近，继续探测宝箱。";
            }
            else UpdateBunnyGoal(world, player.Position, snapshot, clue, now);
        }
        if (bunnyTravel.Active || now < nextBunnyCarrot || player.IsCasting || inCombat || combat.CleanupPending) return;
        nextBunnyCarrot = now.AddSeconds(10);
        var action = ActionManager.Instance();
        if (action != null && action->GetActionStatus(ActionType.EventItem, LuckyCarrot) == 0
            && action->UseAction(ActionType.EventItem, LuckyCarrot))
        {
            bunnyCarrotFailures = 0;
            BunnyAutomationStatus = "已使用幸运胡萝卜，等待系统方向和距离提示。";
        }
        else if (++bunnyCarrotFailures >= 3)
            StopBunnyAutomation("连续三次无法使用幸运胡萝卜，已停止。");
    }

    private void BeginBunnySearchFight(DateTimeOffset now)
    {
        bunnySearchDismounts = 0;
        nextBunnySearchDismount = now;
        SetBunnyPhase(BunnyPhase.SearchFight, now, "已到线索终点，准备下坐骑清怪。");
    }

    private void BeginBunnySearchRemount(DateTimeOffset now)
    {
        bunnySearchMount.Reset();
        SetBunnyPhase(BunnyPhase.SearchRemount, now, "已脱战，正在召唤坐骑。");
    }

    private bool ReleaseBunnySearchCombat()
    {
        if (!bunnySearchCombatOwned) return true;
        if (!combat.Disable()) return false;
        bunnySearchCombatOwned = false;
        bunnySearchCombatTarget = 0;
        return true;
    }

    private void TickBunnySearchFight(IPlayerCharacter player, bool mounted, bool inCombat, DateTimeOffset now)
    {
        if (!inCombat)
        {
            if (!ReleaseBunnySearchCombat())
            { BunnyAutomationStatus = "已脱战，正在还原寻宝战斗控制。"; return; }
            BeginBunnySearchRemount(now);
            return;
        }
        if (player.IsCasting) return;
        if (mounted)
        {
            if (now < nextBunnySearchDismount) return;
            if (bunnySearchDismounts >= 3) { StopBunnyAutomation("到达线索终点后无法下坐骑。"); return; }
            bunnySearchDismounts++;
            nextBunnySearchDismount = now.AddSeconds(1);
            mounts.Dismount();
            return;
        }
        var target = arrivalGame.ReadCleanupTarget(player.Position, targets.Target?.GameObjectId ?? 0, includeFateMobs: true);
        if (target == 0)
        {
            if (bunnySearchCombatOwned && targets.Target?.GameObjectId == bunnySearchCombatTarget)
                targets.Target = null;
            BunnyAutomationStatus = "已到线索终点，等待读取交战目标。";
            return;
        }
        if (!bunnySearchCombatOwned)
        {
            if (combat.CleanupPending) return;
            if (combat.PrepareStart() is { } error) { StopBunnyAutomation(error); return; }
            bunnySearchCombatOwned = true;
        }
        if (!combat.Target(target)) return;
        bunnySearchCombatTarget = target;
        if (!combat.Healthy && !combat.Enable())
        { StopBunnyAutomation(combat.Error ?? "无法开启寻宝战斗的 RSR 和 BossMod。"); return; }
        BunnyAutomationStatus = "已到线索终点，正在清怪并躲避机制。";
    }

    private void BeginBunnyChest(DateTimeOffset now, string status)
    {
        bunnyTravel.Stop();
        bunnyChestTarget = null;
        bunnyChestDismounts = bunnyChestInteractions = 0;
        bunnyChestApproachStarted = false;
        nextBunnyChestAction = now;
        SetBunnyPhase(BunnyPhase.Chest, now, status);
    }

    private unsafe void TickBunnyChest(WorldContext world, IPlayerCharacter player, BunnySnapshot snapshot,
        bool mounted, bool inCombat, DateTimeOffset now)
    {
        if (!bunnyTreasureCounted)
        {
            // The system discovery message can arrive just after the treasure buff disappears.
            if (now - bunnyPhaseAt < TimeSpan.FromSeconds(2)) return;
            if (bunnySearchOnly) StopBunnyAutomation("指引财宝已结束，未收到发现财宝的提示。");
            else SetBunnyPhase(BunnyPhase.Waiting, now, "本轮未发现财宝，等待下一场幸福兔 FATE。");
            return;
        }

        if (bunnyChestInteractions == 0) bunnyChestTarget = snapshot.TreasureChest;
        if (bunnyChestTarget is not { } sighting) { bunnyTravel.Stop(); return; }
        var chest = objects.FirstOrDefault(obj => obj.GameObjectId == sighting.Id
            && (obj.ObjectKind is ObjectKind.EventObj or ObjectKind.Treasure) && BunnyRules.IsChestBaseId(obj.BaseId));
        if (chest is not { IsTargetable: true, Address: not 0 })
        {
            if (bunnyChestInteractions > 0 && Vector3.DistanceSquared(player.Position, sighting.Position) <= 16)
            {
                if (bunnySearchOnly || bunnyLimit > 0 && BunnyFoundCount >= bunnyLimit)
                    StopBunnyAutomation($"已打开宝箱，本次挖出 {BunnyFoundCount} 个。");
                else SetBunnyPhase(BunnyPhase.Waiting, now, "已打开宝箱，等待下一场幸福兔 FATE。");
            }
            return;
        }
        if (player.IsCasting || inCombat) return;

        var position = chest.Position;
        if (!NavigationRules.Finite(position)) return;
        if (Vector3.DistanceSquared(player.Position, position) > 9)
        {
            if (!bunnyChestApproachStarted)
            {
                bunnyChestApproachStarted = true;
                bunnyTravel.Start(new(world.Territory, world.Map, position, "幸福兔宝箱"),
                    world, player.Position, [], now, useCrystals: false, waitWithoutFate: false,
                    arrivalRadius: .75f, unknownHeight: false,
                    groundFilter: p => Vector3.DistanceSquared(p, position) <= 4);
            }
            if (bunnyTravel.Active) bunnyTravel.Tick(world, player.Position, true, false, mounted, false, [], now);
            if (!bunnyTravel.Active && bunnyTravel.TakeArrival() == null)
                StopBunnyAutomation($"无法靠近宝箱：{bunnyTravel.Status}");
            else BunnyAutomationStatus = "正在靠近宝箱，进入交互范围后自动打开。";
            return;
        }
        if (bunnyTravel.Active) bunnyTravel.Stop();
        if (mounted)
        {
            if (now < nextBunnyChestAction) return;
            if (bunnyChestDismounts >= 3) { StopBunnyAutomation("无法下坐骑打开宝箱。"); return; }
            bunnyChestDismounts++;
            nextBunnyChestAction = now.AddSeconds(1);
            mounts.Dismount();
            BunnyAutomationStatus = "已到宝箱旁，正在下坐骑开箱。";
            return;
        }
        if (bunnyChestInteractions >= 3 || bunnyChestInteractions > 0 && now < nextBunnyChestAction) return;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null) return;
        targetSystem->SetHardTarget((GameObject*)chest.Address);
        if ((ulong)targetSystem->GetTargetObjectId() != sighting.Id)
        { StopBunnyAutomation("无法选中本轮宝箱，自动寻宝已停止。"); return; }
        targetSystem->InteractWithObject((GameObject*)chest.Address);
        bunnyChestInteractions++;
        nextBunnyChestAction = now.AddSeconds(1);
        BunnyAutomationStatus = "已交互宝箱，正在确认开箱结果。";
    }

    private unsafe void TickBunnyFight(WorldContext world, Vector3 player, Vector3 fateCentre, float fateRadius, DateTimeOffset now)
    {
        var enmity = TriggerGameReader.ReadEnmity();
        if (enmity == null)
        {
            BunnyAutomationStatus = "等待读取仇恨列表。";
            return;
        }

        var npcs = objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && !n.IsDead
            && NavigationRules.Finite(n.Position)).ToArray();
        var enemies = npcs.Where(n => n.IsTargetable && n.CurrentHp > 0
            && ((Character*)n.Address)->FateId == bunnyFate.Id
            && ActionManager.ClassifyTarget((Character*)n.Address) == ActionManager.TargetCategory.Enemy).ToArray();
        if (enemies.Length == 0)
        {
            bunnyTravel.Stop();
            bunnyCombatDestination = null;
            combat.ReleasePullAttack();
            PauseBunnyCombat();
            return;
        }

        if (!bunnyCombatOwned)
        {
            bunnyCombatOwned = true;
            var error = combat.BeginTrigger(needsApproach: false, useRotation: true, useBmr: false) ?? combat.PrepareStart();
            if (error != null) { StopBunnyAutomation(error); return; }
        }

        var unclaimed = enemies.Where(n => !enmity.Contains(n.EntityId)).ToArray();
        var pull = unclaimed.FirstOrDefault(n => n.GameObjectId == bunnyPullTarget)
            ?? unclaimed.MinBy(n => Vector3.DistanceSquared(player, n.Position));
        if (pull != null)
        {
            if (bunnyPullTarget != pull.GameObjectId)
            {
                bunnyTravel.Stop();
                bunnyCombatDestination = null;
                bunnyPullTarget = pull.GameObjectId;
            }
            if (!combat.PrepareTarget(pull.GameObjectId, false))
            { StopBunnyAutomation("无法选中未建立仇恨的幸福兔 FATE 小怪。"); return; }
            if (!EnsureBunnyRotation()) return;
            BunnyAutomationStatus = $"正在拉怪，已建立仇恨 {enemies.Length - unclaimed.Length}/{enemies.Length}。";
            var range = pull.HitboxRadius + 2.5f;
            if (NavigationRules.Distance(player, pull.Position) <= range)
            {
                bunnyTravel.Stop();
                bunnyCombatDestination = null;
                return;
            }
            var approach = ArrivalRules.Position(player, pull.Position, range - 0.5f);
            if (NavigationRules.Distance(approach, fateCentre) > fateRadius - 1)
            {
                bunnyTravel.Stop();
                bunnyCombatDestination = null;
                BunnyAutomationStatus = "等待未建立仇恨的小怪回到 FATE 范围内。";
                return;
            }
            MoveBunnyCombat(world, player, approach, now, "靠近幸福兔 FATE 小怪", 0.75f);
            return;
        }

        if (bunnyPullTarget != 0)
        {
            bunnyTravel.Stop();
            bunnyCombatDestination = null;
            bunnyPullTarget = 0;
            combat.ReleasePullAttack();
        }

        if (bunnyFightCandidates == null)
        {
            var rabbits = npcs.Where(n => n.NameId is HappyBunny or HappyBunnyVariant
                && ((Character*)n.Address)->FateId is var id && (id == 0 || id == bunnyFate.Id)
                && NavigationRules.Distance(n.Position, fateCentre) <= fateRadius).ToArray();
            if (rabbits.Length == 0) { BunnyAutomationStatus = "已拉怪，等待加载本场幸福兔坐标。"; return; }
            var centre = rabbits.Aggregate(Vector3.Zero, (sum, rabbit) => sum + rabbit.Position) / rabbits.Length;
            var startAngle = Random.Shared.NextSingle() * MathF.Tau;
            bunnyFightGround = point => NavigationRules.Distance(point, fateCentre) <= fateRadius - 2
                && MathF.Abs(NavigationRules.Distance(point, centre) - 25) <= 2 && Math.Abs(point.Y - centre.Y) <= 8;
            bunnyFightCandidates = new();
            for (var i = 0; i < 8; i++)
            {
                var angle = startAngle + i * MathF.PI / 4;
                var point = centre + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * 25;
                if (bunnyFightGround(point)) bunnyFightCandidates.Enqueue(point);
            }
        }

        if (bunnyFightPosition is not { } position || Vector3.Distance(player, position) > 2)
        {
            bunnyFightPosition = null;
            if (!PauseBunnyCombat()) return;
            if (!bunnyFightCandidates.TryPeek(out var candidate))
            { StopBunnyAutomation("幸福兔中心外 25 米处没有可到达的 FATE 内站位。"); return; }
            if (!bunnyTravel.Active)
            {
                if (mesh.Busy || mesh.Route.Count > 0) { BunnyAutomationStatus = "等待其他导航结束。"; return; }
                bunnyTravel.Start(new(world.Territory, world.Map, candidate, "幸福兔击杀点"), world, player, [], now,
                    useCrystals: false, waitWithoutFate: false, arrivalRadius: 1.5f, unknownHeight: false,
                    groundFilter: bunnyFightGround, verifiedEndpointTolerance: 1.5f);
            }
            bunnyTravel.Tick(world, player, true, false, false, false, [], now, inCombat: true);
            BunnyAutomationStatus = $"幸福兔击杀点：{bunnyTravel.Status}";
            if (bunnyTravel.Active) return;
            if (bunnyTravel.TakeArrival() == null)
            {
                bunnyFightCandidates.Dequeue();
                return;
            }
            bunnyFightPosition = player;
        }

        bunnyTravel.Stop();
        bunnyCombatDestination = null;
        var target = enemies.MinBy(n => Vector3.DistanceSquared(player, n.Position))!;
        if (!combat.Target(target.GameObjectId)) return;
        if (EnsureBunnyRotation()) BunnyAutomationStatus = "在幸福兔中心外 25 米击杀已拉到的 FATE 小怪。";
    }

    private bool EnsureBunnyRotation()
    {
        if (!bunnyCombatEnabled)
        {
            bunnyCombatEnabled = combat.EnableStationary();
            if (!bunnyCombatEnabled) StopBunnyAutomation(combat.Error ?? "无法启动 RSR。");
        }
        else if (!combat.StationaryHealthy) StopBunnyAutomation("RSR 控制已变化。");
        return bunnyCombatEnabled;
    }

    private void MoveBunnyCombat(WorldContext world, Vector3 player, Vector3 destination, DateTimeOffset now, string label, float radius)
    {
        if (bunnyTravel.Active && bunnyCombatDestination is { } current && NavigationRules.Distance(current, destination) <= 4)
        {
            bunnyTravel.Tick(world, player, true, false, false, false, [], now, inCombat: true);
            if (bunnyTravel.Active) BunnyAutomationStatus = bunnyTravel.Status;
            else if (bunnyTravel.TakeArrival() == null) StopBunnyAutomation(bunnyTravel.Status);
            return;
        }

        if (now < nextBunnyMove) return;
        bunnyTravel.Stop();
        nextBunnyMove = now.AddSeconds(1);
        bunnyCombatDestination = destination;
        bunnyTravel.Start(new(world.Territory, world.Map, destination, label), world, player, [], now,
            useCrystals: false, waitWithoutFate: false, arrivalRadius: radius, unknownHeight: false);
        if (!bunnyTravel.Active) StopBunnyAutomation(bunnyTravel.Status);
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
        var movementLimit = BunnyRules.MovementLimit(clue.Distance, bunnyVeryNearClues);
        var unknownHeight = true;
        var unique = snapshot.Candidates.Count == 1 ? snapshot.Points.FirstOrDefault(p => snapshot.Candidates.Contains(p.Id)) : null;
        var refineNearby = unique != null && clue.Direction != null && !bunnyTravel.Active && bunnyGoal is { } previousGoal
            && NavigationRules.Distance(previousGoal, MapProjection.MapToWorld(new(unique.X, unique.Y), island)) <= 1
            && NavigationRules.Distance(player, previousGoal) <= NavigationRules.BunnyNearbyGroundRadius + 3;
        if (!bunnyDirectionOnly && !refineNearby && unique != null)
        {
            goal = MapProjection.MapToWorld(new(unique.X, unique.Y), island);
            if (unique.Height is { } height)
            {
                goal.Y = height;
                unknownHeight = false;
            }
        }
        else if (clue.Direction is { } direction)
        {
            var (min, max) = BunnyRules.Range(clue.Distance);
            var distance = float.IsFinite(max) ? (min + max) / 2 : 200;
            if (float.IsFinite(movementLimit)) distance = movementLimit;
            var angle = (int)direction * MathF.PI / 4;
            goal = clue.Position + new Vector3(MathF.Sin(angle) * distance, 0, -MathF.Cos(angle) * distance);
            var uv = MapProjection.ToUv(MapProjection.WorldToMap(goal, island), island.SizeFactor);
            goal = MapProjection.MapToWorld(MapProjection.ToMap(Vector2.Clamp(uv, new(.01f), new(.99f)), island.SizeFactor), island);
        }
        else return;
        var limited = BunnyRules.LimitMovement(player, goal, movementLimit);
        if (limited != goal) unknownHeight = true;
        goal = limited;
        var arrivalRadius = float.IsFinite(movementLimit) ? .5f : 3;
        if (bunnyTravel.Active && bunnyGoal is { } old && NavigationRules.Distance(old, goal) <= arrivalRadius) return;
        bunnyTravel.Stop();
        bunnyGoal = goal;
        bunnySearchAtGoal = NavigationRules.Distance(player, goal) <= arrivalRadius;
        if (bunnySearchAtGoal) return;
        bunnyTravel.Start(new(world.Territory, world.Map, goal, "幸福兔宝箱线索"), world, player, [], now,
            summonMount: EnsureTravelMount(), useCrystals: !float.IsFinite(movementLimit),
            waitWithoutFate: false, unknownHeight: unknownHeight, preferCrystals: true,
            arrivalRadius: arrivalRadius, nearbyGroundRadius: NavigationRules.BunnyNearbyGroundRadius,
            groundFilter: float.IsFinite(movementLimit) ? p => NavigationRules.Distance(player, p) <= movementLimit + .1f : null,
            verifiedEndpointTolerance: float.IsFinite(movementLimit) ? .5f : null, bypassCrystalsInCombat: true);
        BunnyAutomationStatus = bunnyTravel.Active ? "正在前往宝箱线索。" : bunnyTravel.Status;
    }
}
