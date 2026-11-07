using System.Text;
using ULKHelper.Fates;

namespace ULKHelper.Weather;

internal static class WeatherReply
{
    public static string Build(IReadOnlyList<WeatherZone> zones, DateTimeOffset now, uint island = 0, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var end = now.AddHours(12);
        var result = new StringBuilder($"未来12小时天气（现实时间，UTC{TimeZoneInfo.ConvertTime(now, timeZone):zzz}）\n");
        foreach (var zone in zones.Where(z => island == 0 || z.TerritoryId == island))
        {
            var periods = WeatherForecastHelper.GetForecast(zone, now, 32).Where(p => p.Start < end).ToArray();
            var special = periods.Where(p => FateSeeds.All.Any(f => f.Territory == zone.TerritoryId
                && !f.Bunny && f.WeatherId != 0 && f.WeatherId == p.WeatherId)).ToArray();
            result.AppendLine($"【{zone.Name}】");
            if (island != 0)
            {
                result.AppendLine("全部天气：");
                foreach (var period in periods) Append(period, false);
                result.AppendLine("特殊触发天气：");
            }
            if (special.Length == 0) result.AppendLine("未来12小时无特殊触发天气。");
            foreach (var period in special) Append(period, true);
            void Append(WeatherPeriod period, bool withFates)
            {
                var from = TimeZoneInfo.ConvertTime(period.Start < now ? now : period.Start, timeZone);
                var to = TimeZoneInfo.ConvertTime(period.End > end ? end : period.End, timeZone);
                var names = withFates ? " · " + string.Join("、", FateSeeds.All.Where(f => f.Territory == zone.TerritoryId
                    && !f.Bunny && f.WeatherId == period.WeatherId).Select(f => f.ShortName)) : "";
                result.AppendLine($"{from:MM-dd HH:mm}–{to:MM-dd HH:mm} {period.WeatherName}{names}");
            }
        }
        return result.ToString().TrimEnd();
    }
}
