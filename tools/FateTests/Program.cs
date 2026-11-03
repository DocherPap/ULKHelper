using System.Numerics;
using Lumina.Excel.Sheets;
using ULKHelper.Fates;
using ULKHelper.Weather;
using ULKHelper.Crowdsource;
using System.Text.Json;

var count = 0;
void Check(bool valid, string label) { if (!valid) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
if (args.Length == 0) throw new ArgumentException("需要本机 sqpack 路径。");
using var game = new Lumina.GameData(args[0], new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
var islands = FateCatalogLoader.Load(game.Excel.GetSheet<TerritoryType>()!, game.Excel.GetSheet<Fate>()!);
var weather = WeatherCatalogLoader.Load(game.Excel.GetSheet<TerritoryType>()!);
Check(islands.Count == 4 && islands.Select(i => i.Fates.Count).SequenceEqual(new[] { 20, 19, 19, 14 }), "四岛72个点位数量");
Check(islands.SelectMany(i => i.Fates).Select(f => f.Seed.Id).Distinct().Count() == 72, "FATE ID唯一且覆盖本机优雷卡表");
Check(islands.SelectMany(i => i.Fates).Count(f => f.Seed.Bunny) == 5, "5个幸福兔点位");
foreach (var island in islands)
{
    Check(game.GetFile<Lumina.Data.Files.TexFile>(island.TexturePath) != null, island.Name + "游戏地图纹理存在");
    Check(island.Fates.All(f => !string.IsNullOrWhiteSpace(f.Name)), island.Name + "中文FATE名称解析");
    Check(island.Fates.All(f => {
        var p = new Vector2(f.Seed.X, f.Seed.Y);
        return Vector2.Distance(MapProjection.WorldToMap(MapProjection.MapToWorld(p, island), island), p) < .001f;
    }), island.Name + "全部点位投影往返");
}
Check(Vector2.Distance(MapProjection.ToUv(new(21.5f), 100), new(.5f)) < .000001f, "地图中心投影");
var hydatos = islands.Single(i => i.Territory == 827);
Check(hydatos.OffsetY == 475 && Math.Abs(MapProjection.MapToWorld(new(21.5f), hydatos).Z + 475) < .001f, "丰水地图偏移475米正确处理");
var fate = islands[0].Fates.First(f => !f.Seed.Bunny && f.Seed.WeatherId == 0 && !f.Seed.Night);
var now = DateTimeOffset.Parse("2026-11-03T06:00:00Z");
var ledger = new FateLedger(islands);
Check(FateTiming.Describe(fate, null, weather[0], now).Text.StartsWith("未知"), "没有观察记录时不假报冷却");
ledger.Observe([new(fate.Seed.Id, now.AddMinutes(-10), now.AddMinutes(10), 20, FateActivity.Running)], now);
var seenRecord = ledger.Snapshot[fate.Seed.Id];
Check(seenRecord.Active && seenRecord.Appeared == now.AddMinutes(-10), "中途加入使用实际出现时间");
Check(FateTiming.Describe(fate, seenRecord, weather[0], now).Text == "进行中 20%", "实时进度展示");
ledger.Record(fate.Seed.Id, 0, now);
Check(ledger.Snapshot[fate.Seed.Id].Appeared == seenRecord.Appeared, "手动补记不会覆盖进行中事件");
ledger.Observe([], now.AddSeconds(2)); Check(ledger.Snapshot[fate.Seed.Id].Active, "单次短暂缺失不结束活动");
ledger.Observe([], now.AddSeconds(6)); Check(ledger.Snapshot[fate.Seed.Id].Active, "缺失观测不伪造击杀事件");
ledger.Observe([new(fate.Seed.Id, seenRecord.Appeared, now.AddSeconds(6), 100, FateActivity.Ended)], now.AddSeconds(6)); seenRecord = ledger.Snapshot[fate.Seed.Id];
Check(!seenRecord.Active && FateTiming.Describe(fate, seenRecord, weather[0], now).Until == seenRecord.Appeared.AddMinutes(120), "结算后按出现时间估计冷却");
Check(FateTiming.Describe(fate, seenRecord, weather[0], now.AddMinutes(120)).Text.Contains("待触发"), "倒计时结束不宣称已刷新");
Check(FateTiming.Describe(fate, seenRecord with { Active = true }, weather[0], now.AddSeconds(12)).Text.Contains("过期"), "过期活动数据明确标注");
ledger.Record(fate.Seed.Id, 30, now); Check(ledger.Snapshot[fate.Seed.Id].Manual && ledger.Snapshot[fate.Seed.Id].Appeared == now.AddMinutes(-30), "手动补记经过时间");
ledger.Record(fate.Seed.Id, double.NaN, now); Check(ledger.Snapshot[fate.Seed.Id].Appeared == now.AddMinutes(-30), "拒绝非有限手动时间");
ledger.Clear(fate.Seed.Id); Check(ledger.Snapshot.Count == 0, "可清除单个计时");
ledger.Record(fate.Seed.Id, 0, now); ledger.Reset(); Check(ledger.Snapshot.Count == 0, "离开实例清空记录");
Check(islands.SelectMany(i => i.Fates).Where(f => f.Seed.Id is 1422 or 1424).All(f => f.Seed.CooldownMinutes == 20), "UFO和支援20分钟例外");
Check(islands.SelectMany(i => i.Fates).Single(f => f.Seed.Id == 1425).Seed.CooldownMinutes == 10.5, "丰水幸福兔10.5分钟例外");
var nightSeed = fate.Seed with { Night = true };
var daytime = DateTimeOffset.FromUnixTimeSeconds(4200 * 500000L + 175 * 10);
Check(FateTiming.NextConditions(nightSeed, null, daytime) == DateTimeOffset.FromUnixTimeSeconds(4200 * 500000L + 175 * 19), "夜间窗口ET19点开启");
Check(FateTiming.NextConditions(nightSeed, null, daytime.AddSeconds(175 * 9)) == daytime.AddSeconds(175 * 9), "夜间窗口起点包含");
var weatherSeed = islands[0].Fates.First(f => f.Seed.WeatherId != 0).Seed;
var window = FateTiming.NextConditions(weatherSeed, weather[0], now);
Check(window != null && WeatherForecastHelper.GetForecast(weather[0], window.Value, 1)[0].WeatherId == weatherSeed.WeatherId, "等待所需天气窗口");
Check(FateTiming.Countdown(now.AddSeconds(3661), now) == "01:01:01" && FateTiming.Countdown(now.AddSeconds(-1), now) == "00:00:00", "倒计时显示并钳制负值");
Check(islands.SelectMany(i => i.Fates).All(f => !f.Trigger.Contains("待核实")), "72个FATE都有触发说明");
Check(islands[0].Fates.Single(f => f.Seed.Id == 1332).Trigger.Contains("仙人花"), "仙人刺对应触发小怪");
Check(hydatos.Fates.Single(f => f.Seed.Id == 1422).Trigger.Contains("兵武塔内部"), "特殊触发不编造小怪");
var scope = new CrowdScope(101, 3342336, 732);
var json = JsonSerializer.Serialize(new {
    schemaVersion = 1, dataCenterID = 101, instanceID = 3342336, territoryID = 732, instanceEpoch = 2, updatedAt = now.ToUnixTimeSeconds(),
    tracks = new[] {
        new { trackID = "Anemos-1", events = new[] { new { eventType = "FATE", eventID = 1332, lastSpawnedAt = (long?)now.AddMinutes(-100).ToUnixTimeSeconds() } } },
        new { trackID = "Anemos-2", events = new[] {
            new { eventType = "FATE", eventID = 1332, lastSpawnedAt = (long?)now.AddMinutes(-5).ToUnixTimeSeconds() },
            new { eventType = "CE", eventID = 1348, lastSpawnedAt = (long?)now.ToUnixTimeSeconds() },
            new { eventType = "FATE", eventID = 1348, lastSpawnedAt = (long?)null },
            new { eventType = "FATE", eventID = 1333, lastSpawnedAt = (long?)now.AddDays(1).ToUnixTimeSeconds() },
        } },
    },
});
var remote = CrowdsourceClient.ParseSnapshot(json, scope, now);
Check(remote.TrackId == "Anemos-2" && remote.Appearances.Count == 1 && remote.Appearances[1332] == now.AddMinutes(-5), "仅使用最新轨迹，忽略CE/未知/未来时间");
var rejected = false;
try { CrowdsourceClient.ParseSnapshot(json, scope with { InstanceId = 99 }, now); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "拒绝其他房间的快照");
rejected = false;
try { CrowdsourceClient.ValidateSnapshotUri("https://example.com/data.json", scope); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "拒绝非官方快照地址");
var ledger2 = new FateLedger(islands); ledger2.Import(remote.Appearances, 732, now);
Check(ledger2.Snapshot[1332].Crowdsourced && !ledger2.Snapshot[1332].Active, "众包发生历史不冒充当前活动状态");
ledger2.Observe([new(1332, now.AddMinutes(-1), now.AddMinutes(10), 1, FateActivity.Running)], now);
ledger2.Import(remote.Appearances, 732, now);
Check(ledger2.Snapshot[1332].Active && !ledger2.Snapshot[1332].Crowdsourced, "本地正在进行的事件优先");
ledger2.Import(new Dictionary<ushort, DateTimeOffset> { [1412] = now }, 732, now);
Check(!ledger2.Snapshot.ContainsKey(1412), "不混入其他岛屿FATE");
var calls = 0; var pending = new TaskCompletionSource<CrowdSnapshot>();
using var lookup = new EntryCrowdsourceLookup((s, token) => { calls++; return pending.Task; });
lookup.Start(scope, now); lookup.Start(scope, now.AddMinutes(1));
Check(calls == 1, "一次进岛仅启动一次网络查询");
lookup.Reset(); pending.SetResult(remote);
Check(lookup.TakeCompleted() == null, "离岛后旧请求结果不会写入新房间");
pending = new(); lookup.Start(scope, now.AddMinutes(2)); pending.SetResult(remote);
Check(calls == 2 && lookup.TakeCompleted() == remote && lookup.TakeCompleted() == null, "重新进岛可查询一次且结果只消费一次");
lookup.Reset(); pending = new(); lookup.Start(scope, now.AddMinutes(3)); pending.SetException(new HttpRequestException("test")); lookup.TakeCompleted();
lookup.Start(scope, now.AddMinutes(5)); Check(calls == 3 && lookup.Status.Contains("失败"), "查询失败后不会自动重复请求");
using var handler = new SubscriptionHandler(); using var http = new HttpClient(handler);
await new CrowdsourceClient(http).ReadOnce(scope, CancellationToken.None);
Check(handler.Posts == 1 && handler.Gets == 1, "一次查询只有一次订阅POST与一次快照GET");
var conditional = islands[0].Fates.First(f => f.Seed.WeatherId != 0);
var windows = WeatherForecastHelper.GetForecast(weather[0], now, 144);
var available = windows.First(p => p.WeatherId == conditional.Seed.WeatherId).Start.AddSeconds(1);
Check(FateTiming.Describe(conditional, null, weather[0], available).Text == "天气剩余", "未知冷却也显示已到天气窗口剩余");
var unavailable = windows.First(p => p.WeatherId != conditional.Seed.WeatherId).Start.AddSeconds(1);
Check(FateTiming.Describe(conditional, null, weather[0], unavailable).Text == "天气等待", "未到天气直接显示等待时间");
var until = FateTiming.CurrentWindowEnd(conditional.Seed, weather[0], available);
Check(until > available && WeatherForecastHelper.GetForecast(weather[0], until.Value, 1)[0].WeatherId != conditional.Seed.WeatherId, "天气倒计时合并连续同名时段");
var detail = FateTiming.Describe(conditional, ledger2.Snapshot[1332], weather[0], now);
Check(FateTiming.ForMarker(conditional, ledger2.Snapshot[1332], detail, now).Text == "刷新剩余", "已触发点位优先显示刷新倒计时");
var notifications = new FateNotificationHelper(islands);
Check(!notifications.Settings.Enabled && notifications.Settings.Channel == FateNotificationChannel.Echo && notifications.Settings.Template == "在<位置>发现<名字><se.1>", "默认默语、关闭通知并填入指定文本");
var enabled = FateNotificationSettings.Default with { Enabled = true };
notifications.Configure(enabled);
notifications.Observe(732, [new(1332, now.AddMinutes(-2), now.AddMinutes(10), 10, FateActivity.Running)], now);
var sends = 0;
bool Send(FateNotification n, FateNotificationSettings s) { sends++; return true; }
notifications.SendNext(now, Send); Check(sends == 0, "启用或入场时已有FATE不补报");
notifications.Observe(732, [new(1348, now.AddSeconds(1), now.AddMinutes(10), 0, FateActivity.Preparing, new Vector2(30, 27))], now.AddSeconds(1));
notifications.SendNext(now.AddSeconds(1), (n, s) => { Check(n.Position == new Vector2(30, 27) && n.Fate.Seed.ShortName == "章鱼", "通知使用实际点位和缩写"); return Send(n, s); });
Check(sends == 1, "新FATE通知一次");
notifications.Observe(732, [new(1348, now.AddSeconds(2), now.AddMinutes(10), 1, FateActivity.Running)], now.AddSeconds(2));
notifications.SendNext(now.AddSeconds(3), Send); Check(sends == 1, "筹备转进行中不重复通知");
notifications.Observe(732, [], now.AddSeconds(4));
notifications.Observe(732, [new(1348, now.AddSeconds(2), now.AddMinutes(10), 1, FateActivity.Running)], now.AddSeconds(5));
notifications.SendNext(now.AddSeconds(5), Send); Check(sends == 1, "同场事件暂时消失再出现不重报");
notifications.Observe(732, [new(1348, now.AddSeconds(2), now, 100, FateActivity.Ended)], now.AddMinutes(5));
notifications.Observe(732, [new(1348, now.AddMinutes(10), now.AddMinutes(20), 0, FateActivity.Running)], now.AddMinutes(10));
notifications.SendNext(now.AddMinutes(10), Send); Check(sends == 2, "后续新一场同名FATE可通知");
notifications.Observe(732, [new(1333, now.AddMinutes(11), now.AddMinutes(20), 0, FateActivity.Running)], now.AddMinutes(11));
notifications.Configure(enabled with { Enabled = false }); notifications.SendNext(now.AddMinutes(11), Send);
Check(sends == 2, "取消勾选清空待发送通知");
notifications.Configure(enabled); notifications.Observe(732, [], now.AddMinutes(12));
notifications.Observe(732, [new(1335, now.AddMinutes(-1), now.AddMinutes(20), 0, FateActivity.Running)], now.AddMinutes(12).AddSeconds(1));
notifications.SendNext(now.AddMinutes(13), Send); Check(sends == 2, "晚加载的旧FATE不当成新出现");
Check(FateNotificationText.Format(enabled.Template, "仙人掌", "常风之地 (13.9, 21.9)") == "在常风之地 (13.9, 21.9)发现仙人掌<se.1>", "替换占位符并保留提示音标记");
Check(!FateNotificationText.Valid(enabled with { Template = "\n/echo test" }) && !FateNotificationText.Valid(enabled with { Template = "" }), "拒绝换行指令和空通知");
Check(FateNotificationText.Command(FateNotificationChannel.Shout) == "/sh" && FateNotificationText.Command(FateNotificationChannel.Party) == "/p", "使用明确频道前缀");
notifications.Reset(); notifications.Observe(732, [], now.AddMinutes(20));
notifications.Observe(732, [new(1332, now.AddMinutes(20).AddSeconds(1), now.AddMinutes(30), 0, FateActivity.Running)], now.AddMinutes(20).AddSeconds(1));
notifications.Reset(); notifications.SendNext(now.AddMinutes(21), Send); Check(sends == 2, "切岛丢弃待发消息");
var spacingGate = new FateNotificationHelper(islands); spacingGate.Configure(enabled); spacingGate.Observe(732, [], now);
spacingGate.Observe(732, [new(1332, now.AddSeconds(1), now.AddMinutes(10), 0, FateActivity.Running),
    new(1348, now.AddSeconds(1), now.AddMinutes(10), 0, FateActivity.Running)], now.AddSeconds(1));
var paced = 0;
bool PacedSend(FateNotification n, FateNotificationSettings s) { paced++; return true; }
spacingGate.SendNext(now.AddSeconds(1), PacedSend); spacingGate.SendNext(now.AddSeconds(2), PacedSend);
Check(paced == 1, "同时出现的FATE发送间隔至少2秒");
spacingGate.SendNext(now.AddSeconds(3), PacedSend); Check(paced == 2, "排队通知按间隔继续发送");
spacingGate.Reset(); spacingGate.Observe(732, [], now.AddMinutes(1));
spacingGate.Observe(732, [new(1332, now.AddMinutes(1).AddSeconds(1), now.AddMinutes(10), 0, FateActivity.Running)], now.AddMinutes(1).AddSeconds(1));
spacingGate.SendNext(now.AddMinutes(2), PacedSend); Check(paced == 2, "超过30秒的排队通知丢弃");
spacingGate.Reset(); spacingGate.Observe(732, [], now.AddMinutes(3));
spacingGate.Observe(732, [new(1332, now.AddMinutes(3).AddSeconds(1), now.AddMinutes(10), 0, FateActivity.Running)], now.AddMinutes(3).AddSeconds(1));
var failedSends = 0;
spacingGate.SendNext(now.AddMinutes(3).AddSeconds(1), (n, s) => { failedSends++; return false; });
spacingGate.SendNext(now.AddMinutes(3).AddSeconds(4), (n, s) => { failedSends++; return true; });
Check(failedSends == 1, "通知发送失败不自动重发");
Check(FateNotificationText.ChannelNames.Length == Enum.GetValues<FateNotificationChannel>().Length, "26个通知频道与选项索引一致");
Check(FateNotificationText.Command(FateNotificationChannel.Echo) == "/e" && FateNotificationText.IsLocal(FateNotificationChannel.Echo)
    && FateNotificationText.IsLocal(FateNotificationChannel.Debug) && !FateNotificationText.IsLocal(FateNotificationChannel.Shout), "默语和调试默语仅本地输出");
Check(FateNotificationText.Command(FateNotificationChannel.Linkshell8) == "/l8"
    && FateNotificationText.Command(FateNotificationChannel.CrossLinkshell8) == "/cwl8", "通信贝及跨服通信贝频道编号正确");
Check(FateNotificationText.Command(FateNotificationChannel.NoviceNetwork) == "/b"
    && FateNotificationText.Command(FateNotificationChannel.PvPTeam) == "/pt", "新人和PvP战队命令与本机文本指令表一致");
Check(FateNotificationText.FlagCommand(enabled, "仙人掌") == "/e 在<flag>发现仙人掌<se.1>", "位置使用游戏原生flag占位而不是文字坐标");
Console.WriteLine($"{count} FATE/map checks passed.");

sealed class SubscriptionHandler : HttpMessageHandler
{
    public int Posts, Gets;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string body;
        if (request.Method == HttpMethod.Post)
        {
            Posts++;
            body = JsonSerializer.Serialize(new { dataCenterID = 101, serverTime = now,
                subscriptions = new[] { new { instanceID = 3342336, territoryID = 732,
                    snapshotURL = "https://ce-data.atmoomen.top/v1/events/101/3342336/732.json.js?revision=test", expiresAt = now + 1800 } } });
        }
        else { Gets++; body = "{\"schemaVersion\":1,\"dataCenterID\":101,\"instanceID\":3342336,\"territoryID\":732,\"instanceEpoch\":2,\"tracks\":[],\"updatedAt\":0}"; }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

