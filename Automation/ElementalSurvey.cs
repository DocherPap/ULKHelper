using Dalamud.Game.ClientState.Conditions;
using System.Numerics;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal sealed partial class AutomationHelpers
{
    private readonly IslandTravelHelper surveyTravel;
    private readonly HashSet<uint> surveyedCrystals = [];
    private readonly HashSet<Vector2> surveyedElementals = [];
    private WorldContext lastSurveyWorld;
    private WorldContext? surveyWorld;
    private IslandCrystal? surveyCrystal;
    private DateTimeOffset surveyStarted, scanAt;
    private string surveyStatus = "";
    private bool quickElementalSurvey, surveyReturning;
    public IFateMapProvider? ElementalMaps { private get; set; }
    public bool QuickElementalSurvey => quickElementalSurvey;
    private bool SurveyActive => surveyWorld.HasValue;

    public void SetQuickElementalSurvey(bool enabled) => commands.Enqueue(() =>
    {
        quickElementalSurvey = enabled;
        settings?.Put("common.quickElementalSurvey", enabled);
        if (!enabled && SurveyActive) StopSurvey("已关闭快速检查元灵。");
    });

    private void TickSurvey(WorldContext world, bool loading, DateTimeOffset now)
    {
        if (!client.IsLoggedIn)
        {
            lastSurveyWorld = default;
            if (SurveyActive) StopSurvey("角色已离线，元灵巡查已停止。");
            return;
        }
        if (!loading && world.Territory != 0 && world.Map != 0 && world.PlayerId != 0)
        {
            if (SurveyActive && world != surveyWorld) StopSurvey("已离开原副本，元灵巡查已停止。");
            if (!SurveyActive && quickElementalSurvey && world.PlayerId == lastSurveyWorld.PlayerId
                && lastSurveyWorld.Territory != 0 && lastSurveyWorld.Territory != world.Territory
                && IslandCrystalRoutes.Entrance(world.Territory) is { } entrance)
            {
                surveyWorld = world;
                surveyStarted = now;
                surveyCrystal = entrance;
                scanAt = now.AddSeconds(2);
                surveyedCrystals.Clear();
                surveyedElementals.Clear();
                surveyStatus = "正在检查营地水晶附近的元灵。";
            }
            lastSurveyWorld = world;
        }
        if (!SurveyActive) return;
        if (now - surveyStarted >= TimeSpan.FromMinutes(surveyReturning ? 5 : 3))
        {
            if (surveyReturning) CompleteSurvey("返回营地超时。");
            else StopSurvey("元灵巡查超过3分钟，已停止。");
            return;
        }
        if (loading) return;
        if (objects.LocalPlayer is not { IsDead: false } player || conditions[ConditionFlag.InCombat])
        { StopSurvey("角色进入战斗或倒地，元灵巡查已停止。"); return; }
        if (BunnyAutomationActive || commonJourney.Active || navigation.Active || follow.Active || trigger.Active)
        { StopSurvey("已有其他任务，已停止元灵巡查。"); return; }
        var safe = SafeState(surveyTravel.Interacting || surveyTravel.Transferring);
        if (surveyTravel.Transferring && !safe) return;
        if (surveyCrystal is { } crystal)
        {
            if (!safe || player.IsCasting || now < scanAt) return;
            if (NavigationRules.Distance(player.Position, crystal.Position) > 35)
            { StopSurvey("未在预期水晶附近，元灵巡查已停止。"); return; }
            var island = ElementalMaps?.Islands.FirstOrDefault(map => map.Territory == world.Territory && map.MapId == world.Map);
            if (island != null && ElementalMaps?.Snapshot.Elementals is { } elementals)
                foreach (var point in elementals.Where(p => p.Visible && p.MapId == island.MapId
                    && now - p.LastSeen < TimeSpan.FromSeconds(2)
                    && NavigationRules.Distance(MapProjection.MapToWorld(p.Position, island), crystal.Position) <= 100))
                    if (!surveyedElementals.Any(p => Vector2.DistanceSquared(p, point.Position) <= .01f))
                        surveyedElementals.Add(point.Position);
            surveyedCrystals.Add(crystal.PlaceNameId);
            surveyCrystal = null;
            if (surveyedCrystals.Count == IslandCrystalRoutes.All.Count(c => c.Territory == world.Territory))
            { ReturnSurveyToCamp(world, player.Position, now); return; }
            surveyTravel.StartSurveyLeg(crystal, world, player.Position, surveyedCrystals, now);
            if (!surveyTravel.Active) StopSurvey(surveyTravel.Status);
            return;
        }

        surveyTravel.Tick(world, player.Position, safe, false, conditions[ConditionFlag.Mounted], player.IsCasting, [], now);
        surveyStatus = SurveyResult + (surveyReturning ? " 正在返回营地。" : " ") + surveyTravel.Status;
        if (surveyTravel.Active) return;
        if (surveyReturning)
        {
            CompleteSurvey(surveyTravel.TakeArrival() != null ? "已返回营地。" : $"返回营地失败：{surveyTravel.Status}");
            return;
        }
        if (surveyTravel.TakeArrival() is { } arrived)
        {
            surveyCrystal = IslandCrystalRoutes.Nearest(world.Territory, arrived.Position);
            scanAt = now.AddSeconds(2);
            surveyStatus = "等待水晶附近的元灵加载。";
        }
        else if (surveyTravel.SurveyExhausted) ReturnSurveyToCamp(world, player.Position, now);
        else StopSurvey(surveyTravel.Status);
    }

    private string SurveyResult => $"已检查 {surveyedCrystals.Count} 处水晶，发现 {surveyedElementals.Count} 处元灵。";

    private void ReturnSurveyToCamp(WorldContext world, Vector3 player, DateTimeOffset now)
    {
        var camp = IslandCrystalRoutes.Entrance(world.Territory)!;
        if (NavigationRules.Distance(player, camp.Position) <= 15)
        {
            CompleteSurvey("已在营地。");
            return;
        }
        surveyReturning = true;
        surveyStarted = now;
        surveyStatus = SurveyResult + " 正在返回营地。";
        var goal = ArrivalRules.Position(player, camp.Position, 7);
        surveyTravel.Start(new(world.Territory, world.Map, goal, "营地"), world, player, [], now, waitWithoutFate: false);
        if (!surveyTravel.Active) CompleteSurvey($"返回营地失败：{surveyTravel.Status}");
    }

    private void CompleteSurvey(string returnStatus)
    {
        var result = "元灵巡查完成。" + SurveyResult + returnStatus;
        StopSurvey(result);
        chat.Print(result);
    }

    private void StopSurvey(string status)
    {
        surveyWorld = null;
        surveyCrystal = null;
        surveyReturning = false;
        surveyTravel.Stop();
        commonStatus = status;
    }
}
