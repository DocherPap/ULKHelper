using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ULKHelper.Inventory;
using ULKHelper.Automation;
using ULKHelper.Weather;
using ULKHelper.Fates;

namespace ULKHelper.Windows;

internal sealed class MainWindow : Window
{
    private readonly ItemStatisticsPage itemStatistics;
    private readonly AutomationPage automation;
    private readonly WeatherPage weather;
    private readonly FateMapPage fateMap;

    public MainWindow(IStatisticsProvider statistics, AutomationHelpers helpers, IReadOnlyList<WeatherZone> weatherZones,
        IFateMapProvider fateProvider, IMapTextures mapTextures,
        string? weatherError = null) : base("优雷卡助手###ULKHelper.Main")
    {
        itemStatistics = new(statistics);
        automation = new(helpers, new FateMapPage(fateProvider, mapTextures, weatherZones));
        weather = new(weatherZones, weatherError);
        fateMap = new(fateProvider, mapTextures, weatherZones);
        Size = new Vector2(1280, 780);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1000, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        // The page handles scrolling; the tab bar stays visible above it.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void Draw()
    {
        var tabWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemInnerSpacing.X * 3) / 4;
        if (!ImGui.BeginTabBar("##MainPages", ImGuiTabBarFlags.FittingPolicyResizeDown)) return;
        try
        {
            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("物品统计"))
            {
                try { DrawPage(DrawItemStatisticsPage); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("天气预报"))
            {
                try { DrawPage(weather.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("地图 / FATE"))
            {
                try { DrawPage(fateMap.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("自动化"))
            {
                try { DrawPage(DrawAutomationPage); }
                finally { ImGui.EndTabItem(); }
            }
        }
        finally { ImGui.EndTabBar(); }
    }

    private static void DrawPage(Action drawContent)
    {
        // Zero size fills the remaining width and height on every frame.
        // Overflow scrolls inside this child without moving the tabs.
        var visible = ImGui.BeginChild("##PageContent", Vector2.Zero);
        try
        {
            if (!visible) return;
            ImGui.PushTextWrapPos(0);
            try { drawContent(); }
            finally { ImGui.PopTextWrapPos(); }
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawAutomationPage() => automation.Draw();

    private void DrawItemStatisticsPage() => itemStatistics.Draw();
}
