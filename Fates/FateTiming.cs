using ULKHelper.Weather;

namespace ULKHelper.Fates;

internal sealed record FateTimingResult(string Text, DateTimeOffset? Until, uint Color);

internal static class FateTiming
{
    public const uint ActiveColor = 0xFF70F080, CoolingColor = 0xFF70B0FF, UnknownColor = 0xFFAAAAAA, ReadyColor = 0xFF80DEDF;
    public static FateTimingResult Describe(FateDefinition fate, FateRecord? record, WeatherZone? zone, DateTimeOffset now)
    {
        if (record is { Active: true })
        {
            if (now - record.LastSeen >= TimeSpan.FromSeconds(5)) return new("活动数据已过期", null, UnknownColor);
            return new(record.Activity == FateActivity.Preparing ? "筹备中" : $"进行中 {record.Progress}%", record.Ends, ActiveColor);
        }
        var ready = record?.Appeared.AddMinutes(fate.Seed.CooldownMinutes);
        if (ready > now) return new("刷新剩余", ready, CoolingColor);
        if (fate.Seed.Bunny) return record == null ? new("未知 · 未记录", null, UnknownColor) : new("预计冷却已到 · 待触发", null, ReadyColor);
        if (zone == null && fate.Seed.WeatherId != 0) return new("天气数据不可用", null, UnknownColor);
        var displayCondition = fate.Seed.WeatherId != 0 ? fate.Seed with { Night = false } : fate.Seed;
        var next = NextConditions(displayCondition, zone, now);
        if (next == null) return new("条件窗口未知", null, UnknownColor);
        if (next > now)
            return new(fate.Seed.WeatherId != 0 ? "天气等待" : "夜间等待", next, CoolingColor);
        var close = CurrentWindowEnd(displayCondition, zone, now);
        if (close != null) return new(fate.Seed.WeatherId != 0 ? "天气剩余" : "夜间剩余", close, ReadyColor);
        return record == null ? new("未知 · 未记录", null, UnknownColor) : new("条件可用 · 待触发", null, ReadyColor);
    }

    public static FateTimingResult ForMarker(FateDefinition fate, FateRecord? record, FateTimingResult detail, DateTimeOffset now)
    {
        if (fate.Seed.Bunny && record is { Active: true }) return detail;
        var ready = record?.Appeared.AddMinutes(fate.Seed.CooldownMinutes);
        return ready > now ? new("刷新剩余", ready, detail.Color) : detail;
    }

    public static DateTimeOffset? CurrentWindowEnd(FateSeed fate, WeatherZone? zone, DateTimeOffset now)
    {
        DateTimeOffset? end = null;
        if (fate.WeatherId != 0)
        {
            if (zone == null) return null;
            var periods = WeatherForecastHelper.GetForecast(zone, now, 144);
            if (periods[0].WeatherId != fate.WeatherId) return null;
            // Consecutive identical weather periods form one continuous opportunity window.
            var firstDifferent = periods.FirstOrDefault(p => p.WeatherId != fate.WeatherId);
            end = firstDifferent?.Start;
        }
        if (fate.Night)
        {
            var seconds = now.ToUnixTimeSeconds();
            var hour = seconds / 175 % 24;
            if (hour is >= 6 and < 19) return null;
            var dayStart = seconds / 4200 * 4200;
            var dawn = DateTimeOffset.FromUnixTimeSeconds(dayStart + (hour >= 19 ? 4200 : 0) + 6 * 175);
            if (end == null || dawn < end) end = dawn;
        }
        return end;
    }

    // Cactbot's night window is ET 19:00–06:00. Weather AND night are checked together.
    public static DateTimeOffset? NextConditions(FateSeed fate, WeatherZone? zone, DateTimeOffset after)
    {
        if (fate.WeatherId == 0 && !fate.Night) return after;
        var periods = zone == null ? null : WeatherForecastHelper.GetForecast(zone, after, 144);
        for (var i = 0; i < 144 * 8; i++)
        {
            var time = i == 0 ? after : DateTimeOffset.FromUnixTimeSeconds((after.ToUnixTimeSeconds() / 175 + i) * 175);
            var hour = time.ToUnixTimeSeconds() / 175 % 24;
            if (fate.Night && hour is >= 6 and < 19) continue;
            if (fate.WeatherId != 0)
            {
                var index = (time.ToUnixTimeSeconds() - WeatherForecastHelper.PeriodStart(after).ToUnixTimeSeconds()) / 1400;
                if (periods == null || index >= periods.Count) break;
                if (periods[(int)index].WeatherId != fate.WeatherId) continue;
            }
            return time;
        }
        return null;
    }

    public static string Countdown(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling((until - now).TotalSeconds));
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }
}
