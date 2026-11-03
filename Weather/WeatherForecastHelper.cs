namespace ULKHelper.Weather;

internal sealed record WeatherChance(uint WeatherId, string Name, int Rate);
internal sealed record WeatherZone(uint TerritoryId, string Name, IReadOnlyList<WeatherChance> Chances);
internal sealed record WeatherPeriod(DateTimeOffset Start, DateTimeOffset End, uint WeatherId, string WeatherName);

internal static class WeatherForecastHelper
{
    public const int PeriodSeconds = 1400; // Eight Eorzean hours, each 175 real seconds.

    public static DateTimeOffset PeriodStart(DateTimeOffset time)
    {
        var seconds = time.ToUnixTimeSeconds();
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(time), "天气预报不支持 Unix 纪元之前的时间。");
        return DateTimeOffset.FromUnixTimeSeconds(seconds / PeriodSeconds * PeriodSeconds);
    }

    public static int CalculateTarget(DateTimeOffset time)
    {
        var seconds = time.ToUnixTimeSeconds();
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(time));
        var hours = seconds / 175;
        var nextBlock = (hours + 8 - hours % 8) % 24;
        // Preserve the unsigned 32-bit shift/xor behaviour used by the game weather algorithm.
        var seed = unchecked((uint)(seconds / 4200 * 100 + nextBlock));
        var mixed = unchecked((seed << 11) ^ seed);
        return (int)(((mixed >> 8) ^ mixed) % 100);
    }

    public static IReadOnlyList<WeatherPeriod> GetForecast(WeatherZone zone, DateTimeOffset time, int count = 12)
    {
        if (count is < 1 or > 144) throw new ArgumentOutOfRangeException(nameof(count));
        if (zone.Chances.Count == 0 || zone.Chances.Any(c => c.Rate <= 0 || string.IsNullOrWhiteSpace(c.Name))
            || zone.Chances.Sum(c => c.Rate) != 100)
            throw new ArgumentException("天气概率必须为正数且合计 100%。", nameof(zone));
        var start = PeriodStart(time);
        var result = new WeatherPeriod[count];
        for (var i = 0; i < count; i++)
        {
            var from = start.AddSeconds((long)i * PeriodSeconds);
            var target = CalculateTarget(from);
            var cumulative = 0;
            var weather = zone.Chances.First(c => target < (cumulative += c.Rate));
            result[i] = new(from, from.AddSeconds(PeriodSeconds), weather.WeatherId, weather.Name);
        }
        return result;
    }
}
