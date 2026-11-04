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
    private readonly FateMapPage triggerMap;
    private readonly SettingsStore? settings;
    private readonly LogosPage logos;
    private readonly CommonPage common;
    private const int TabCount = 6;
    private bool firstDraw = true;
    private int selectedTab;
    private DateTimeOffset nextLayout;

    public MainWindow(IStatisticsProvider statistics, AutomationHelpers helpers, IReadOnlyList<WeatherZone> weatherZones,
        IFateMapProvider fateProvider, IMapTextures mapTextures,
        string? weatherError = null, SettingsStore? settings = null, LogosPage? logosPage = null, BunnyPage? bunnyPage = null) : base("优雷卡助手###ULKHelper.Main")
    {
        logos = logosPage!;
        common = new(helpers, fateProvider);
        itemStatistics = new(statistics);
        triggerMap = new(fateProvider, mapTextures, weatherZones, helpers);
        automation = new(helpers, triggerMap);
        weather = new(weatherZones, weatherError);
        fateMap = new(fateProvider, mapTextures, weatherZones, helpers, bunnyPage);
        this.settings = settings;
        selectedTab = settings?.Get("window.tab",0)??0;
        var schema=settings?.Get("window.tabSchema",0)??0;
        if(schema==0&&selectedTab>=3)selectedTab++;
        if(schema<2&&selectedTab>=3)selectedTab++;
        if(schema<3)selectedTab=selectedTab==3?2:selectedTab>3?selectedTab-1:selectedTab;
        if(schema<4 && (settings?.Get("window.tab",-1)??-1)>=0)selectedTab++;
        selectedTab=Math.Clamp(selectedTab,0,TabCount - 1);
        if (settings != null) { itemStatistics.Load(settings); weather.Load(settings); fateMap.Load(settings, "map"); triggerMap.Load(settings, "triggerMap"); automation.Load(settings); }
        Size = settings?.Get("window.size", new Vector2(1280, 780)) ?? new Vector2(1280, 780);
        SizeCondition = ImGuiCond.Once;
        if (settings != null) { Position = settings.Get("window.position", new Vector2(80, 80)); PositionCondition = ImGuiCond.Once; }
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
        if (firstDraw && settings != null) LayoutSettings.Restore(settings);
        var restoreTab = selectedTab;
        var tabWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemInnerSpacing.X * (TabCount - 1)) / TabCount;
        if (!ImGui.BeginTabBar("##MainPages", ImGuiTabBarFlags.FittingPolicyResizeDown)) return;
        try
        {
            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("常用功能", firstDraw && restoreTab == 0 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 0;
                try { DrawPage(common.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("物品统计", firstDraw && restoreTab == 1 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 1;
                try { DrawPage(DrawItemStatisticsPage); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("天气预报", firstDraw && restoreTab == 2 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 2;
                try { DrawPage(weather.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("地图 / FATE", firstDraw && restoreTab == 3 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 3;
                try { DrawPage(fateMap.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("文理技能", firstDraw && restoreTab == 4 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 4;
                try { DrawPage(logos.Draw); } finally { ImGui.EndTabItem(); }
            }
            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("自动化", firstDraw && restoreTab == 5 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 5;
                try { DrawPage(DrawAutomationPage); }
                finally { ImGui.EndTabItem(); }
            }
        }
        finally
        {
            ImGui.EndTabBar();
            firstDraw = false;
            if (settings != null)
            {
                itemStatistics.Save(settings); weather.Save(settings); fateMap.Save(settings, "map"); triggerMap.Save(settings, "triggerMap"); automation.Save(settings);
                settings.Put("window.tabSchema",4); settings.Put("window.size", ImGui.GetWindowSize()); settings.Put("window.position", ImGui.GetWindowPos()); settings.Put("window.tab", selectedTab);
                if (DateTimeOffset.UtcNow >= nextLayout || ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { LayoutSettings.Capture(settings); nextLayout = DateTimeOffset.UtcNow.AddSeconds(1); }
                settings.Flush();
            }
        }
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
