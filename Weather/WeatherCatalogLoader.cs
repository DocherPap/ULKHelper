using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ULKHelper.Weather;

internal static class WeatherCatalogLoader
{
    // Verified against local Chinese TerritoryType / WeatherRate, game 2026.10.06.0000.0000.
    private static readonly (uint Territory, string Name)[] Islands =
        [(732, "常风之地"), (763, "恒冰之地"), (795, "涌火之地"), (827, "丰水之地")];

    public static IReadOnlyList<WeatherZone> Load(ExcelSheet<TerritoryType> territories)
    {
        var result = new List<WeatherZone>();
        foreach (var (id, name) in Islands)
        {
            var territory = territories.GetRow(id);
            var rates = territory.WeatherRate.Value;
            var chances = new List<WeatherChance>();
            for (var i = 0; i < rates.Rate.Count; i++)
            {
                if (rates.Rate[i] == 0) continue;
                var weather = rates.Weather[i].Value;
                if (weather.RowId == 0) throw new InvalidDataException($"{name} 存在无效天气行。");
                chances.Add(new(weather.RowId, weather.Name.ExtractText(), rates.Rate[i]));
            }
            var zone = new WeatherZone(id, name, chances.ToArray());
            _ = WeatherForecastHelper.GetForecast(zone, DateTimeOffset.UnixEpoch, 1); // Validate once on load.
            result.Add(zone);
        }
        return result.ToArray();
    }
}
