# 优雷卡天气预报

天气名称及卡片增加浅色：强风土黄、暴雪蓝白、热浪橙红、打雷/雷雨紫、薄雾蓝绿、灵风黄绿。主窗与悬浮窗使用同一 WeatherPalette；特殊天气金色边框继续保留。

用户参考页：[优雷卡小工具天气查询](https://ulk.tunnel.tidebyte.com:8129/weather)。已读取页面及其公开前端脚本，确认天气在浏览器本地计算，无须调用天气预报 HTTP 接口。插件使用相同的天气时间规则，本机 Lumina 表提供区域、天气名称和概率，运行时不请求该网站。

## 使用与边界

打开主窗「天气预报」查看常风、恒冰、涌火、丰水四岛天气。可选择当前及后续时段，横向滚动或勾选「集中展示」换行。悬浮窗提供精简天气卡片与倒计时，各岛特殊触发天气继续高亮。

默认显示系统本地时间及 UTC 偏移，也可切换 UTC；悬浮卡片显示完整日期与时区。不会假设用户系统位于 UTC+8。倒计时是距离下一天气时段开始的剩余时间，即使下一段天气名称相同也不合并。每段 1400 秒（23 分 20 秒，ET 8 小时），ET 00/08/16 时换段。

预报取决于本机系统时钟，不是实时服务器天气回读。系统时间不准会导致预报偏差。尚未接入 NM 出现条件、提醒或自动化任务触发。

## 模块

- `Weather/WeatherForecastHelper.cs`：不依赖 Dalamud 的纯计算 helper。`PeriodStart(time)` 取当前段起点；`CalculateTarget(time)` 计算 0–99 的天气目标值；`GetForecast(zone, time, count)` 按累计概率返回时段，支持 1–144 段，供未来业务独立调用。
- `Weather/WeatherCatalogLoader.cs`：启动时从 `TerritoryType → WeatherRate → Weather` 读取四岛数据，检查非空天气、正权重及概率合计 100%。失败时天气页显示错误，其他功能照常使用。
- `Windows/WeatherPage.cs`：显示和查询参数；同一时段缓存结果，只在跨段、系统时间跨段变更或预报段数改变时重新生成，倒计时每帧显示。

算法使用 Unix 秒、每 ET 小时 175 秒和每 ET 天 4200 秒构造 seed，再进行 uint32 的移位/异或。使用整数时间保证同一段内稳定，并保持跨 2038 年及 uint32 溢出时的规则。参考页面也是按对齐的时段起点生成卡片；对照样本均按其实际调用方式计算。

## 数据来源

本机中文游戏 `2026.10.06.0000.0000`，Dalamud API 15 对应 Lumina.Excel。四岛全部概率和参考页面一致。以下数值为核对记录，生产代码从本地游戏表读取概率，不维护重复的静态天气表。

| 岛屿 | TerritoryType | WeatherRate | 天气与概率 |
| --- | --- | --- | --- |
| 常风 | 732 | 91 | 晴朗 30%、强风 30%、暴雨 30%、小雪 10% |
| 恒冰 | 763 | 94 | 晴朗 10%、薄雾/热浪/小雪/打雷/暴雪各 18% |
| 涌火 | 795 | 96 | 晴朗 10%、热浪/打雷/暴雪/灵风/小雪各 18% |
| 丰水 | 827 | 100 | 晴朗 12%、暴雨/妖雾/雷雨/小雪各 22% |

来源：[参考页面](https://ulk.tunnel.tidebyte.com:8129/weather)，其当前脚本 `/_next/static/chunks/a1dcbfa9d001f1b8.js` 中的天气模块与时间段生成逻辑；辅助对照 [Asvel/ffxiv-weather](https://github.com/Asvel/ffxiv-weather/blob/master/src/Weather.ts)。前端脚本文件名会随部署变化，分析快照在忽略的 `artifacts/weather-reference/`，仅供本地检查，不进入插件产物。

本地表复核：

```powershell
dotnet run --project tools/InventoryInspect -- "D:\Program Files (x86)\FFXIV\game\sqpack" --weather-data
```

## 验证

```powershell
dotnet run --project tools/WeatherTests -- "D:\Program Files (x86)\FFXIV\game\sqpack"
dotnet run --project tools/UiPreview -- --weather weather.png
dotnet run --project tools/UiPreview -- --weather --small --utc weather-small.png
dotnet run --project tools/UiPreview -- --weather --weather-long --small weather-long.png
```

22 项检查通过，包含 300 个独立 JS 参考值（跨日期、换段前后 1 秒、2038 年前后及更远日期）、1400 秒连续时段、时区不变性、同段一致性、无效参数与四岛游戏数据。默认/最小窗口、24 段换行与滚动布局离线检查完成；库存与自动化原有校验仍通过。Debug / Release 构建通过。

尚未游戏内确认实际天气与系统时钟的对应、跨段自动刷新和热重载；离线截图使用真实游戏表与系统时间，并非游戏内截图。
# 特殊触发天气高亮（2026-11-03）

天气页按已核实FateSeeds的Territory与非零WeatherId匹配当前及未来天气段；符合任一特殊触发NM所需天气时用金色边框和底色高亮，悬停列出对应NM简称。只说明天气满足，不代表冷却/刷怪/夜间条件也满足；不接入自动移动。默认/最小窗口离线检查通过。

