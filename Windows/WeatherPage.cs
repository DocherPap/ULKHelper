using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Weather;
using ULKHelper.Fates;

namespace ULKHelper.Windows;

internal sealed class WeatherPage(IReadOnlyList<WeatherZone> zones, string? error = null)
{
    private int countIndex;
    private bool utc;
    private DateTimeOffset cachedStart;
    private int cachedCount;
    private readonly Dictionary<uint, IReadOnlyList<WeatherPeriod>> forecasts = new();
    private static readonly int[] Counts = [6, 12, 24];
    public void Load(SettingsStore settings) { countIndex = Math.Clamp(settings.Get("weather.countIndex", 0), 0, 2); utc = settings.Get("weather.utc", false); }
    public void Save(SettingsStore settings) { settings.Put("weather.countIndex", countIndex); settings.Put("weather.utc", utc); }

    public void Draw()
    {
        ImGui.TextUnformatted("优雷卡天气预报");
        ImGui.TextWrapped("每个天气持续 23 分 20 秒。");
        if (error != null || zones.Count == 0)
        {
            ImGui.TextWrapped(error ?? "无法读取天气数据。");
            return;
        }
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("当前及后续 23 个天气").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
        ImGui.Combo("预报时段", ref countIndex, ["当前及后续 5 个天气", "当前及后续 11 个天气", "当前及后续 23 个天气"], 3);
        ImGui.SameLine(); ImGui.Checkbox("显示 UTC 时间", ref utc);
        ImGui.TextDisabled(utc ? "时间：UTC" : $"时间：系统本地（UTC{DateTimeOffset.Now:zzz}）");
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
            ImGui.Separator();
            var periods = forecasts[zone.TerritoryId];
            ImGui.TextUnformatted($"{zone.Name}  ·  当前：{periods[0].WeatherName}  ·  下段 {remaining:mm\\:ss}");
            DrawPeriods(zone, periods, now);
        }
    }

    private void DrawPeriods(WeatherZone zone, IReadOnlyList<WeatherPeriod> periods, DateTimeOffset now)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = ImGui.GetContentRegionAvail().X;
        var minCardWidth = ImGui.CalcTextSize("00:00:00 – 00:00:00").X + ImGui.GetStyle().FramePadding.X * 4;
        var columns = Math.Clamp((int)((width + spacing) / (minCardWidth + spacing)), 1, periods.Count);
        var cardWidth = Math.Min(minCardWidth * 1.5f, (width - (columns - 1) * spacing) / columns);
        var height = ImGui.GetTextLineHeightWithSpacing() * 3 + ImGui.GetStyle().FramePadding.Y * 2;
        for (var i = 0; i < periods.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine();
            var period = periods[i];
            var from = utc ? period.Start : period.Start.ToLocalTime();
            var to = utc ? period.End : period.End.ToLocalTime();
            var current = period.Start <= now && now < period.End;
            var special = FateSeeds.All.Where(f => f.Territory == zone.TerritoryId && f.WeatherId != 0 && f.WeatherId == period.WeatherId).ToArray();
            var pos = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(pos, pos + new Vector2(cardWidth, height), special.Length > 0 ? 0xFF324D62u : current ? 0xFF354B32u : 0xFF292929u, 4);
            if (special.Length > 0) draw.AddRect(pos, pos + new Vector2(cardWidth, height), 0xFF57D4F0u, 4, ImDrawFlags.None, 2);
            ImGui.BeginGroup();
            ImGui.Dummy(new Vector2(cardWidth, ImGui.GetStyle().FramePadding.Y));
            CenterText(current ? $"{period.WeatherName} · 当前" : period.WeatherName, pos.X, cardWidth);
            CenterText($"{from:HH:mm:ss} – {to:HH:mm:ss}", pos.X, cardWidth);
            CenterText($"{from:MM-dd}  ET {period.Start.ToUnixTimeSeconds() / 175 % 24:00}:00", pos.X, cardWidth);
            ImGui.EndGroup();
            ImGui.SetCursorScreenPos(pos);
            ImGui.InvisibleButton($"##Weather{zone.TerritoryId}.{i}", new(cardWidth, height));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{zone.Name} · {period.WeatherName}");
                ImGui.TextUnformatted($"{from:yyyy-MM-dd HH:mm:ss zzz} → {to:yyyy-MM-dd HH:mm:ss zzz}");
                if (special.Length > 0) ImGui.TextUnformatted("特殊触发天气：" + string.Join("、", special.Select(f => f.ShortName)));
                ImGui.EndTooltip();
            }
        }
        ImGui.Spacing();
    }

    private static void CenterText(string text, float x, float width)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, x + (width - ImGui.CalcTextSize(text).X) / 2 - ImGui.GetCursorScreenPos().X));
        ImGui.TextUnformatted(text);
    }
}
