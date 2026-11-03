using System.Text.Json;
using ULKHelper.Weather;
using Lumina.Excel.Sheets;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
void Throws(System.Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception(name); }
var epoch = DateTimeOffset.UnixEpoch;
Check(WeatherForecastHelper.CalculateTarget(epoch) == 56, "Unix纪元参考值");
Check(WeatherForecastHelper.CalculateTarget(epoch.AddSeconds(1400)) == 12, "第二天气段参考值");
Check(WeatherForecastHelper.CalculateTarget(epoch.AddSeconds(2800)) == 0, "第三天气段参考值");
Check(WeatherForecastHelper.PeriodStart(epoch.AddSeconds(1399.999)) == epoch, "天气切换前一毫秒");
Check(WeatherForecastHelper.PeriodStart(epoch.AddSeconds(1400)) == epoch.AddSeconds(1400), "天气切换整点");
var now = DateTimeOffset.Parse("2026-11-03T03:45:12Z");
Check(WeatherForecastHelper.CalculateTarget(now) == WeatherForecastHelper.CalculateTarget(now.ToOffset(TimeSpan.FromHours(8))), "时区不改变天气");
Throws(() => WeatherForecastHelper.PeriodStart(epoch.AddSeconds(-1)), "拒绝纪元之前的时间");
var zone = new WeatherZone(732, "常风之地", [new(2, "晴朗", 30), new(6, "强风", 30), new(8, "暴雨", 30), new(15, "小雪", 10)]);
var forecast = WeatherForecastHelper.GetForecast(zone, now, 24);
Check(forecast.Count == 24 && forecast[0].Start <= now && forecast[0].End > now, "首段包含当前时间");
Check(forecast.All(p => (p.End - p.Start).TotalSeconds == 1400), "每段持续1400秒");
Check(forecast.Zip(forecast.Skip(1)).All(p => p.First.End == p.Second.Start), "相邻段连续且不合并同名天气");
Check(Enumerable.Range(0, 1400).All(i => WeatherForecastHelper.CalculateTarget(epoch.AddSeconds(i)) == 56), "同一天气段结果恒定");
Check(WeatherForecastHelper.GetForecast(zone, epoch, 1)[0].WeatherName == "强风", "权重分配");
Throws(() => WeatherForecastHelper.GetForecast(zone, now, 0), "拒绝无效预报长度");
Throws(() => WeatherForecastHelper.GetForecast(zone with { Chances = [new(2, "晴朗", 99)] }, now), "拒绝概率合计错误");

if (args.Length > 0)
{
    using var game = new Lumina.GameData(args[0], new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
    var zones = WeatherCatalogLoader.Load(game.Excel.GetSheet<TerritoryType>()!);
    Check(zones.Count == 4 && zones.Select(z => z.TerritoryId).SequenceEqual(new uint[] { 732, 763, 795, 827 }), "四岛区域映射");
    string[] patterns = ["晴朗:30,强风:30,暴雨:30,小雪:10", "晴朗:10,薄雾:18,热浪:18,小雪:18,打雷:18,暴雪:18",
        "晴朗:10,热浪:18,打雷:18,暴雪:18,灵风:18,小雪:18", "晴朗:12,暴雨:22,妖雾:22,雷雨:22,小雪:22"];
    for (var i = 0; i < zones.Count; i++)
        Check(string.Join(',', zones[i].Chances.Select(c => $"{c.Name}:{c.Rate}")) == patterns[i], $"{zones[i].Name}游戏天气表与参考网站一致");
    // Independent reference values from the supplied site's time/hash rule, evaluated at period
    // boundaries just as its UI does; stored as data so the C# implementation cannot mirror itself.
    var fixturePath = Path.Combine("tools", "WeatherTests", "reference-vectors.json");
    var vectors = JsonSerializer.Deserialize<ReferenceVector[]>(File.ReadAllText(fixturePath))!;
    Check(vectors.Length >= 100, "加载跨日期、边界与2038年后的参考样本");
    Check(vectors.All(v => WeatherForecastHelper.CalculateTarget(DateTimeOffset.FromUnixTimeSeconds(v.Seconds)) == v.Target), "全部算法参考样本一致");
    var observed = new HashSet<uint>();
    foreach (var area in zones)
        foreach (var period in WeatherForecastHelper.GetForecast(area, now, 144)) observed.Add(period.WeatherId);
    Check(zones.SelectMany(z => z.Chances).All(c => observed.Contains(c.WeatherId)), "长时预报覆盖全部四岛天气类型");
}
Console.WriteLine($"{count} weather checks passed.");
sealed record ReferenceVector(long Seconds, int Target);
