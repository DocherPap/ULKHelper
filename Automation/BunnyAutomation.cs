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
    // Local CN BNpcName rows for Happy Bunny.
    private const uint HappyBunny = 7248, HappyBunnyVariant = 11309;
    private ulong bunnyPullTarget;
    private Vector3? bunnyCombatDestination, bunnyFightPosition;
    private readonly IslandTravelHelper bunnyTravel;
    private readonly HashSet<(ushort Id, int Start)> bunnyHandled = [];
    private IBunnyTracker? bunnyTracker;
    private WorldContext? bunnyWorld;
    private HashSet<ushort> bunnySelected = [];
    private (ushort Id, int Start) bunnyFate;
    private enum BunnyPhase { Waiting, Travel, Fight, Reward, Search, Chest }
    private BunnyPhase bunnyPhase;
    private DateTimeOffset bunnyStarted, bunnyPhaseAt, nextBunnyTick, nextBunnyCarrot, lastBunnyClue, nextBunnySync, nextBunnyMove;
    private Vector3? bunnyGoal, lastFailedClueOrigin;
    private bool bunnyCombatOwned, bunnyCombatEnabled, bunnyDirectionOnly;
    private int bunnySyncAttempts, bunnyCarrotFailures;
    private int bunnyLimit;
    private int bunnyMinimumSeconds;
    private bool bunnyTreasureCounted, bunnySearchOnly, bunnyStandbyIssued, bunnyStandbyArrived;
    private DateTimeOffset bunnyHuntStarted;
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
        BunnyAutomationStatus = status;
        if (bunnySearchOnly) commonStatus = status;
        bunnySearchOnly = false;
    }

    private void BeginBunnySearch(DateTimeOffset now)
    {
        bunnyGoal = lastFailedClueOrigin = null;
        bunnyDirectionOnly = false;
        bunnyTreasureCounted = false;
        bunnyHuntStarted = now;
        bunnyCarrotFailures = 0;
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
                        world, player.Position, [], now, summonMount: mounts.Selected != 0, waitWithoutFate: false,
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
                world, player.Position, [], now, mounts.Selected != 0, waitWithoutFate: false, nearbyGroundRadius: Math.Clamp(next.Radius - 3, 0, NavigationRules.BunnyNearbyGroundRadius), preferCrystals: true);
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
        if (bunnyPhase == BunnyPhase.Chest)
        {
            if (bunnySearchOnly)
            {
                if (!hasBuff && now - bunnyPhaseAt >= TimeSpan.FromSeconds(1))
                    StopBunnyAutomation("指引财宝已结束，自动寻找宝藏已停止。");
                return;
            }
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
            if (bunnyTravel.Active) BunnyAutomationStatus = bunnyTravel.Status;
            else if (bunnyTravel.TakeArrival() == null)
                BunnyAutomationStatus = $"前往线索失败，等待下次提示：{bunnyTravel.Status}";
        }
        if (bunnyTravel.Transferring) return;
        var clue = snapshot.Active && snapshot.Territory == world.Territory ? snapshot.Clues.LastOrDefault() : null;
        if (clue != null && clue.Time > lastBunnyClue)
        {
            lastBunnyClue = clue.Time;
            if (clue.Distance == BunnyDistance.Here)
            {
                bunnyTravel.Stop();
                if (bunnySearchOnly) StopBunnyAutomation("已到宝藏附近，请手动打开宝箱。");
                else SetBunnyPhase(BunnyPhase.Chest, now, "已找到宝箱，请手动打开。");
                return;
            }
            UpdateBunnyGoal(world, player.Position, snapshot, clue, now);
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

        if (bunnyFightPosition == null)
        {
            var rabbits = npcs.Where(n => n.NameId is HappyBunny or HappyBunnyVariant
                && ((Character*)n.Address)->FateId is var id && (id == 0 || id == bunnyFate.Id)
                && NavigationRules.Distance(n.Position, fateCentre) <= fateRadius).ToArray();
            if (rabbits.Length == 0) { BunnyAutomationStatus = "已拉怪，等待加载本场幸福兔坐标。"; return; }
            var centre = rabbits.Aggregate(Vector3.Zero, (sum, rabbit) => sum + rabbit.Position) / rabbits.Length;
            var startAngle = Random.Shared.NextSingle() * MathF.Tau;
            for (var i = 0; i < 8; i++)
            {
                var angle = startAngle + i * MathF.PI / 4;
                var point = centre + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * 25;
                if (NavigationRules.Distance(point, fateCentre) > fateRadius - 2) continue;
                if (mesh.Ground(point, false) is not { } ground || NavigationRules.Distance(ground, fateCentre) > fateRadius - 2
                    || MathF.Abs(NavigationRules.Distance(ground, centre) - 25) > 2) continue;
                bunnyFightPosition = ground;
                break;
            }
            if (bunnyFightPosition == null)
            { StopBunnyAutomation("幸福兔中心外 25 米处没有可用的 FATE 内站位。"); return; }
        }

        var position = bunnyFightPosition.Value;
        if (NavigationRules.Distance(player, position) > 2)
        {
            if (!PauseBunnyCombat()) return;
            BunnyAutomationStatus = "已建立仇恨，前往幸福兔中心外 25 米的击杀点。";
            MoveBunnyCombat(world, player, position, now, "幸福兔击杀点", 1.5f);
            return;
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
        bunnyTravel.Start(new(world.Territory, world.Map, goal, "幸福兔宝箱线索"), world, player, [], now,
            summonMount: mounts.Selected != 0, waitWithoutFate: false, unknownHeight: unknownHeight,
            nearbyGroundRadius: NavigationRules.BunnyNearbyGroundRadius);
        BunnyAutomationStatus = bunnyTravel.Active ? "正在前往宝箱线索。" : bunnyTravel.Status;
    }
}
