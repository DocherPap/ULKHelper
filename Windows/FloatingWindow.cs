using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ULKHelper.Windows;

internal sealed class FloatingWindow : Window
{
    private const string Title = "优雷卡助手悬浮窗";
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings;
    private static readonly Vector2 Minimum = new(400, 200);
    private readonly SettingsStore settings;
    private readonly CommonPage common;
    private readonly WeatherPage weather;
    private readonly FateMapPage map;
    private readonly Action<int> expand;
    private bool sizeLocked, clickThrough, drawn;
    private int transparency, tab;
    private Vector2 lockPosition, lockSize;
    private enum TitleIcon { Lock, Unlock, Expand, Close }

    public FloatingWindow(SettingsStore settings, CommonPage common, WeatherPage weather,
        FateMapPage map, Action<int> expand) : base(Title + "###ULKHelper.Floating")
    {
        this.settings = settings;
        this.common = common;
        this.weather = weather;
        this.map = map;
        this.expand = expand;
        Size = settings.Get("floating.size", MainWindow.MinimumSize / 2);
        Position = settings.Get("floating.position", new Vector2(100, 100));
        SizeCondition = PositionCondition = ImGuiCond.Once;
        SizeConstraints = new() { MinimumSize = Minimum, MaximumSize = new(float.MaxValue) };
        sizeLocked = settings.Get("floating.sizeLocked", false);
        clickThrough = settings.Get("floating.clickThrough", false);
        transparency = Math.Clamp(settings.Get("floating.transparency", 0), 0, 85);
        IsOpen = settings.Get("floating.visible", false);
        ShowCloseButton = AllowPinning = AllowClickthrough = RespectCloseHotkey = AllowBackgroundBlur = false;
        DisableFadeInFadeOut = true;
    }

    public void DrawSettings()
    {
        var visible = IsOpen;
        var changed = ImGui.Checkbox("小窗显示", ref visible);
        if (changed) IsOpen = visible;
        ImGui.SameLine();
        changed |= ImGui.Checkbox("锁定大小", ref sizeLocked);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("鼠标穿透", ref clickThrough);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        changed |= ImGui.SliderInt("透明度", ref transparency, 0, 85, "%d%%");
        if (changed) SavePreferences();
    }

    public override void PreDraw()
    {
        drawn = false;
        Flags = BaseFlags;
        if (sizeLocked || clickThrough) Flags |= ImGuiWindowFlags.NoResize;
        if (clickThrough) Flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (1 - transparency / 100f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(ImGui.GetStyle().WindowPadding.X, 3 * ImGuiHelpers.GlobalScale));
    }

    public override void Draw()
    {
        drawn = true;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2 * ImGuiHelpers.GlobalScale));
        try { DrawTitle(); }
        finally { ImGui.PopStyleVar(); }
        if (IsOpen && ImGui.BeginTabBar("##FloatingPages", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            try
            {
                DrawTab("常用功能", 0, () => common.Draw(compact: true));
                DrawTab("天气预报", 2, () => weather.Draw(compact: true, readOnly: clickThrough));
                DrawTab("地图", 3, map.DrawCompact);
            }
            finally { ImGui.EndTabBar(); }
        }
        settings.Put("floating.size", ImGui.GetWindowSize() / ImGuiHelpers.GlobalScale);
        settings.Put("floating.position", ImGui.GetWindowPos());
        settings.Flush();
    }

    private void DrawTitle()
    {
        var height = ImGui.GetTextLineHeight();
        var spacing = 2 * ImGuiHelpers.GlobalScale;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - 3 * (height + spacing));
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##FloatingTitleDrag", new Vector2(width, height));
        ImGui.GetWindowDrawList().AddText(start, ImGui.GetColorU32(ImGuiCol.Text), Title);
        if (!clickThrough && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        ImGui.SameLine(0, spacing);

        var wasClickThrough = clickThrough;
        var toggle = IconButton(wasClickThrough ? TitleIcon.Lock : TitleIcon.Unlock, "##Lock", height);
        lockPosition = ImGui.GetItemRectMin();
        lockSize = ImGui.GetItemRectSize();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("切换鼠标穿透");
        ImGui.SameLine(0, spacing);
        if (IconButton(TitleIcon.Expand, "##Expand", height))
        {
            expand(tab);
            IsOpen = false;
            SavePreferences();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("展开到主窗口");
        ImGui.SameLine(0, spacing);
        if (IconButton(TitleIcon.Close, "##Close", height))
        {
            IsOpen = false;
            SavePreferences();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("关闭悬浮窗");
        if (toggle && !wasClickThrough)
        {
            clickThrough = true;
            SavePreferences();
        }
    }

    private static bool IconButton(TitleIcon icon, string id, float size)
    {
        var pressed = ImGui.InvisibleButton(id, new Vector2(size));
        var centre = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered())
            draw.AddCircleFilled(centre, size / 2, ImGui.GetColorU32(ImGui.IsItemActive() ? ImGuiCol.ButtonActive : ImGuiCol.ButtonHovered));
        var ink = ImGui.GetColorU32(ImGuiCol.Text);
        var radius = size * .23f;
        var stroke = Math.Max(1, size * .09f);
        switch (icon)
        {
            case TitleIcon.Close:
                draw.AddLine(centre - new Vector2(radius), centre + new Vector2(radius), ink, stroke);
                draw.AddLine(centre + new Vector2(-radius, radius), centre + new Vector2(radius, -radius), ink, stroke);
                break;
            case TitleIcon.Expand:
                var tip = centre + new Vector2(radius, -radius);
                draw.AddLine(centre + new Vector2(-radius, radius), tip, ink, stroke);
                draw.AddLine(tip, centre - new Vector2(radius), ink, stroke);
                draw.AddLine(tip, centre + new Vector2(radius), ink, stroke);
                break;
            default:
                var bodyTop = centre.Y;
                draw.AddRectFilled(new(centre.X - radius, bodyTop), new(centre.X + radius, bodyTop + radius), ink, stroke);
                var shackle = radius * .65f;
                var top = centre.Y - radius;
                draw.AddLine(new(centre.X - shackle, bodyTop), new(centre.X - shackle, top), ink, stroke);
                draw.AddLine(new(centre.X - shackle, top), new(centre.X + shackle, top), ink, stroke);
                draw.AddLine(new(centre.X + shackle, top), new(centre.X + shackle,
                    icon == TitleIcon.Lock ? bodyTop : top + radius * .4f), ink, stroke);
                break;
        }
        return pressed;
    }

    private void DrawTab(string label, int index, Action draw)
    {
        if (!ImGui.BeginTabItem(label)) return;
        tab = index;
        try
        {
            var flags = clickThrough ? ImGuiWindowFlags.NoInputs : ImGuiWindowFlags.None;
            if (index is 2 or 3) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            var visible = ImGui.BeginChild("##FloatingPage", Vector2.Zero, false, flags);
            try
            {
                if (visible) draw();
            }
            finally { ImGui.EndChild(); }
        }
        finally { ImGui.EndTabItem(); }
    }

    public override void PostDraw()
    {
        try
        {
            if (!drawn || !IsOpen || !clickThrough) return;
            // The entire floating window ignores mouse input. Only this button-sized window can unlock it.
            ImGui.SetNextWindowPos(lockPosition);
            ImGui.SetNextWindowSize(lockSize);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            var visible = ImGui.Begin("###ULKHelper.Floating.Unlock", BaseFlags | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
            try
            {
                if (!visible) return;
                if (ImGui.InvisibleButton("##Unlock", lockSize))
                {
                    clickThrough = false;
                    SavePreferences();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("解除鼠标穿透");
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(2);
            }
        }
        finally { ImGui.PopStyleVar(2); }
    }

    private void SavePreferences()
    {
        settings.Put("floating.visible", IsOpen);
        settings.Put("floating.sizeLocked", sizeLocked);
        settings.Put("floating.clickThrough", clickThrough);
        settings.Put("floating.transparency", transparency);
        settings.Flush();
    }
}
