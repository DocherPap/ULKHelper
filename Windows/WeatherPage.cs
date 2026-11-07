using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Weather;
using ULKHelper.Fates;

namespace ULKHelper.Windows;

internal sealed class WeatherPage(IReadOnlyList<WeatherZone> zones, string? error = null)
{
    private int countIndex;
    private bool utc;
    private bool concentrated;
    private DateTimeOffset cachedStart;
    private int cachedCount;
    private readonly Dictionary<uint, IReadOnlyList<WeatherPeriod>> forecasts = new();
    private static readonly int[] Counts = [6, 12, 24];
    public void Load(SettingsStore settings)
    {
        countIndex = Math.Clamp(settings.Get("weather.countIndex", 0), 0, Counts.Length - 1);
        utc = settings.Get("weather.utc", false);
        concentrated = settings.Get("weather.concentrated", false);
    }

    public void Save(SettingsStore settings)
    {
        settings.Put("weather.countIndex", countIndex);
        settings.Put("weather.utc", utc);
        settings.Put("weather.concentrated", concentrated);
    }

    public void Draw() => Draw(compact: false);

    public void Draw(bool compact, bool readOnly = false)
    {
        if (!compact)
        {
            ImGui.TextUnformatted("优雷卡天气预报");
            ImGui.TextWrapped("按本机时间和游戏天气表计算。每段持续 23 分 20 秒；倒计时为距离下一时段的时间。");
        }
        if (error != null || zones.Count == 0)
        {
            ImGui.TextWrapped(error ?? "无法读取天气数据。");
            return;
        }
        if (!compact)
        {
            ImGui.SetNextItemWidth(ImGui.CalcTextSize("当前及后续 23 段").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
            ImGui.Combo("预报时段", ref countIndex, ["当前及后续 5 段", "当前及后续 11 段", "当前及后续 23 段"], 3);
            ImGui.SameLine(); ImGui.Checkbox("显示 UTC 时间", ref utc);
            ImGui.SameLine(); ImGui.Checkbox("集中展示", ref concentrated);
            ImGui.TextDisabled(utc ? "时间：UTC" : $"时间：系统本地（UTC{DateTimeOffset.Now:zzz}）");
        }
        var now = DateTimeOffset.UtcNow;
        var start = WeatherForecastHelper.PeriodStart(now);
        var count = Counts[countIndex];
        if (cachedStart != start || cachedCount != count)
        {
            forecasts.Clear();
            foreach (var zone in zones) forecasts[zone.TerritoryId] = WeatherForecastHelper.GetForecast(zone, now, count);
            cachedStart = start; cachedCount = count;
        }
        var remaining = start.AddSeconds(WeatherForecastHelper.PeriodSeconds) - now;
        foreach (var zone in zones)
        {
            if (!compact) ImGui.Separator();
            var periods = forecasts[zone.TerritoryId];
            var currentWeather = $"{zone.Name}  ·  当前：{periods[0].WeatherName}";
            ImGui.TextColored(WeatherPalette.Color(periods[0].WeatherName) ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
                $"{currentWeather}  ·  {(compact ? "剩余" : "下段")} {remaining:mm\\:ss}");
            if (compact) DrawCompactPeriods(zone, periods, now, readOnly);
            else DrawPeriods(zone, periods, now);
        }
        // A short floating window can still scroll vertically over island headings or blank space.
        if (compact && !readOnly && ImGui.IsWindowHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0)
                ImGui.SetScrollY(Math.Clamp(ImGui.GetScrollY() - wheel * ImGui.GetTextLineHeightWithSpacing() * 3, 0, ImGui.GetScrollMaxY()));
        }
    }

    private void DrawCompactPeriods(WeatherZone zone, IReadOnlyList<WeatherPeriod> periods, DateTimeOffset now, bool readOnly)
    {
        var style = ImGui.GetStyle();
        var timeOffset = periods.Max(p => ImGui.CalcTextSize(p.WeatherName).X) + style.ItemInnerSpacing.X;
        var timeWidth = ImGui.CalcTextSize("ET 00:00").X;
        var cardWidth = MathF.Ceiling(timeOffset + timeWidth + style.FramePadding.X * 2);
        var height = MathF.Ceiling(ImGui.GetTextLineHeight() * 2 + style.FramePadding.Y * 2);
        var contentWidth = periods.Count * cardWidth + (periods.Count - 1) * style.ItemSpacing.X;
        ImGui.SetNextWindowContentSize(new Vector2(contentWidth, height));
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (readOnly) flags |= ImGuiWindowFlags.NoInputs;
        var visible = ImGui.BeginChild($"##CompactWeatherRow{zone.TerritoryId}", new Vector2(0, height), false, flags);
        try
        {
            if (!visible) return;
            if (!readOnly && ImGui.IsWindowHovered())
            {
                var io = ImGui.GetIO();
                var wheel = io.MouseWheelH != 0 ? io.MouseWheelH : io.MouseWheel;
                if (wheel != 0)
                    ImGui.SetScrollX(Math.Clamp(ImGui.GetScrollX() - wheel * cardWidth, 0, ImGui.GetScrollMaxX()));
            }
            DrawCards(zone, periods, now, periods.Count, cardWidth, height, timeOffset);
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawPeriods(WeatherZone zone, IReadOnlyList<WeatherPeriod> periods, DateTimeOffset now)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var minCardWidth = ImGui.CalcTextSize("00:00:00 – 00:00:00").X + style.FramePadding.X * 4;
        var columns = Math.Clamp((int)((width + spacing) / (minCardWidth + spacing)), 1, periods.Count);
        var cardWidth = Math.Min(minCardWidth * 1.5f, (width - (columns - 1) * spacing) / columns);
        var height = ImGui.GetTextLineHeightWithSpacing() * 3 + style.FramePadding.Y * 2;
        if (!concentrated && periods.Count > columns)
        {
            var contentWidth = periods.Count * cardWidth + (periods.Count - 1) * spacing;
            ImGui.SetNextWindowContentSize(new Vector2(contentWidth, height));
            var flags = ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysHorizontalScrollbar;
            var visible = ImGui.BeginChild($"##WeatherRow{zone.TerritoryId}", new Vector2(0, MathF.Ceiling(height) + style.ScrollbarSize), false, flags);
            try
            {
                if (visible) DrawCards(zone, periods, now, periods.Count, cardWidth, height);
            }
            finally { ImGui.EndChild(); }
        }
        else
        {
            DrawCards(zone, periods, now, columns, cardWidth, height);
        }
        ImGui.Spacing();
    }

    private void DrawCards(WeatherZone zone, IReadOnlyList<WeatherPeriod> periods, DateTimeOffset now, int columns, float cardWidth, float height,
        float timeOffset = 0)
    {
        var compact = timeOffset > 0;
        for (var i = 0; i < periods.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine();
            var period = periods[i];
            var from = !compact && utc ? period.Start : period.Start.ToLocalTime();
            var to = utc ? period.End : period.End.ToLocalTime();
            var current = period.Start <= now && now < period.End;
            var special = FateSeeds.All.Where(f => f.Territory == zone.TerritoryId && f.WeatherId != 0 && f.WeatherId == period.WeatherId).ToArray();
            var pos = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            var tint = WeatherPalette.Color(period.WeatherName);
            var background = tint is { } pastel ? ImGui.GetColorU32(Vector4.Lerp(new(.16f, .16f, .16f, 1), pastel, .22f))
                : ImGui.GetColorU32(special.Length > 0 ? 0xFF324D62u : current ? 0xFF354B32u : 0xFF292929u);
            draw.AddRectFilled(pos, pos + new Vector2(cardWidth, height),
                background, 4);
            if (special.Length > 0) draw.AddRect(pos, pos + new Vector2(cardWidth, height), ImGui.GetColorU32(0xFF57D4F0u), 4, ImDrawFlags.None, 2);
            var et = $"ET {period.Start.ToUnixTimeSeconds() / 175 % 24:00}:00";
            if (compact)
            {
                var minutesUntilStart = Math.Max(0, (int)Math.Ceiling((period.Start - now).TotalMinutes));
                var lineHeight = ImGui.GetTextLineHeight();
                var left = ImGui.GetStyle().FramePadding.X;
                var color = ImGui.GetColorU32(ImGuiCol.Text);
                draw.AddText(pos + new Vector2(left, (height - lineHeight) / 2), tint is { } foreground ? ImGui.GetColorU32(foreground) : color, period.WeatherName);
                draw.AddText(pos + new Vector2(left + timeOffset, (height - lineHeight * 2) / 2),
                    color, $"{minutesUntilStart / 60:00}:{minutesUntilStart % 60:00}\n{et}");
            }
            else
            {
                ImGui.BeginGroup();
                ImGui.Dummy(new Vector2(cardWidth, ImGui.GetStyle().FramePadding.Y));
                if (tint is { } textColor) ImGui.PushStyleColor(ImGuiCol.Text, textColor);
                CenterText(current ? $"{period.WeatherName} · 当前" : period.WeatherName, pos.X, cardWidth);
                if (tint != null) ImGui.PopStyleColor();
                CenterText($"{from:HH:mm:ss} – {to:HH:mm:ss}", pos.X, cardWidth);
                CenterText($"{from:MM-dd}  {et}", pos.X, cardWidth);
                ImGui.EndGroup();
                ImGui.SetCursorScreenPos(pos);
            }
            ImGui.InvisibleButton($"##Weather{zone.TerritoryId}.{i}", new(cardWidth, height));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{zone.Name} · {period.WeatherName}");
                ImGui.TextUnformatted(compact ? $"{from:yyyy-MM-dd HH:mm:ss zzz}" : $"{from:yyyy-MM-dd HH:mm:ss zzz} → {to:yyyy-MM-dd HH:mm:ss zzz}");
                if (special.Length > 0) ImGui.TextUnformatted("特殊触发天气：" + string.Join("、", special.Select(f => f.ShortName)));
                ImGui.EndTooltip();
            }
        }
    }

    private static void CenterText(string text, float x, float width)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, x + (width - ImGui.CalcTextSize(text).X) / 2 - ImGui.GetCursorScreenPos().X));
        ImGui.TextUnformatted(text);
    }
}
