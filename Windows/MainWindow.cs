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
    internal static readonly Vector2 MinimumSize = new(1000, 520);
    private readonly ItemStatisticsPage itemStatistics;
    private readonly AutomationPage automation;
    private readonly WeatherPage weather;
    private readonly FateMapPage fateMap;
    private readonly SettingsStore? settings;
    private readonly LogosPage logos;
    private readonly CommonPage common;
    private FloatingWindow? floating;
    private const int TabCount = 5;
    private bool firstDraw = true;
    private int selectedTab;
    private int? requestedTab;
    private DateTimeOffset nextLayout;

    public MainWindow(IStatisticsProvider statistics, AutomationHelpers helpers, IReadOnlyList<WeatherZone> weatherZones,
        IFateMapProvider fateProvider, IMapTextures mapTextures,
        string? weatherError = null, SettingsStore? settings = null, LogosPage? logosPage = null, BunnyPage? bunnyPage = null) : base("优雷卡助手###ULKHelper.Main")
    {
        logos = logosPage!;
        common = new(helpers, fateProvider);
        itemStatistics = new(statistics);
        weather = new(weatherZones, weatherError);
        fateMap = new(fateProvider, mapTextures, weatherZones, helpers, bunnyPage);
        automation = new(helpers, fateMap);
        this.settings = settings;
        selectedTab = settings?.Get("window.tab",0)??0;
        var schema=settings?.Get("window.tabSchema",0)??0;
        if(schema==0&&selectedTab>=3)selectedTab++;
        if(schema<2&&selectedTab>=3)selectedTab++;
        if(schema<3)selectedTab=selectedTab==3?2:selectedTab>3?selectedTab-1:selectedTab;
        if(schema<4 && (settings?.Get("window.tab",-1)??-1)>=0)selectedTab++;
        if(schema<5)selectedTab=selectedTab==5?3:selectedTab==6?5:selectedTab;
        selectedTab=Math.Clamp(selectedTab,0,TabCount - 1);
        if (settings != null) { itemStatistics.Load(settings); weather.Load(settings); fateMap.Load(settings, "map"); automation.Load(settings); }
        Size = settings?.Get("window.size", new Vector2(1280, 780)) ?? new Vector2(1280, 780);
        SizeCondition = ImGuiCond.Once;
        if (settings != null) { Position = settings.Get("window.position", new Vector2(80, 80)); PositionCondition = ImGuiCond.Once; }
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MainWindow.MinimumSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        // The page handles scrolling; the tab bar stays visible above it.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public FloatingWindow CreateFloatingWindow(SettingsStore store) =>
        floating = new(store, common, weather, fateMap, index =>
        {
            requestedTab = index;
            IsOpen = true;
        });

    public override void Draw()
    {
        if (firstDraw && settings != null) LayoutSettings.Restore(settings);
        var restoreTab = requestedTab ?? (firstDraw ? selectedTab : -1);
        if (requestedTab != null) ImGui.SetWindowFocus();
        var tabWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemInnerSpacing.X * (TabCount - 1)) / TabCount;
        // Old cimgui ShrinkWidths can loop when the previous frame's tab widths are fractional.
        // Whole pixels keep equal-width tabs without triggering the unassignable remainder.
        tabWidth = MathF.Floor(tabWidth);
        const ImGuiTabBarFlags fitting = ImGuiTabBarFlags.FittingPolicyResizeDown;
        if (!ImGui.BeginTabBar("##MainPages", fitting)) return;
        try
        {
            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("常用功能", restoreTab == 0 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 0;
                try { DrawPage(DrawCommonPage); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("物品统计", restoreTab == 1 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 1;
                try { DrawPage(DrawItemStatisticsPage); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("天气预报", restoreTab == 2 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 2;
                try { DrawPage(weather.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("地图 / 自动化", restoreTab == 3 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 3;
                try { DrawPage(automation.Draw); }
                finally { ImGui.EndTabItem(); }
            }

            ImGui.SetNextItemWidth(tabWidth);
            if (ImGui.BeginTabItem("文理技能", restoreTab == 4 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectedTab = 4;
                try { DrawPage(logos.Draw); } finally { ImGui.EndTabItem(); }
            }
        }
        finally
        {
            ImGui.EndTabBar();
            firstDraw = false;
            requestedTab = null;
            if (settings != null)
            {
                itemStatistics.Save(settings); weather.Save(settings); fateMap.Save(settings, "map"); automation.Save(settings);
                settings.Put("window.tabSchema",5); settings.Put("window.size", ImGui.GetWindowSize()); settings.Put("window.position", ImGui.GetWindowPos()); settings.Put("window.tab", selectedTab);
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

    private void DrawItemStatisticsPage() => itemStatistics.Draw();
    private void DrawCommonPage() => common.Draw(drawWindowSettings: floating == null ? null : floating.DrawSettings);
}
