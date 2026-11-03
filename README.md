# 优雷卡助手 / ULKHelper

基于 FF14 卫月（Dalamud）的优雷卡便捷插件，项目位于 `D:\project\ULKHelper`。

目标：提供优雷卡装备与对应材料速览，并逐步实现部分刷怪、刷材料及其他操作自动化。

当前提供插件入口、`/ulk` 命令、API 15 清单和可调整大小的主窗口。顶部为等宽的“物品统计”“天气预报”“地图 / FATE”“自动化”页签。

物品统计页支持真实库存：汇总背包、穿戴、兵装库、普通/高级陆行鸟背包、收藏柜、幻化柜和各雇员的库存，刷新装备阶段、材料持有量与缺口。

自动化页位于最后，顶部显示大号“施工中”，提供独立 helper 调试：当前已加载玩家列表、选定车头、监听说话/呼喊/喊话并标记有效坐标，以及基于附近 FATE 的目的地导航。收到坐标后需要单独点击开始导航；导航依赖可选插件 vnavmesh，尚未整合自动跟车。详见 [自动化 helper](docs/AUTOMATION_HELPERS.md)。

天气页显示四岛当前及后续天气、时间范围和下一时段倒计时，支持 6/12/24 段及本地/UTC 时间。按游戏数据和系统时间离线计算，无需外部天气服务。详见 [天气预报](docs/WEATHER.md)。

地图页显示四岛游戏底图与 72 个 FATE 点位，支持缩放、平移、玩家位置、点位选择、游戏地图标记、当前活动剩余时间和预计冷却。进入对应岛屿后自动观察记录，也可手动补记出现时间；未知历史不会猜测，离岛清空。无需 ACT，规则参考 cactbot。详见 [地图与 FATE 计时](docs/FATE_MAP.md)。

每次进岛按大区、真实房间ID与岛屿向官方众包网站查询一次发生历史，之后不定时拉取、不续期、不上传。地图点位直接显示刷新剩余、天气等待/剩余；每个FATE列出触发小怪或特殊触发说明。见 [进岛众包](docs/CROWDSOURCE.md)。

地图页可勾选“发送通知频道”，在26个频道中选择并编辑文本，含默语 `/e`、调试默语、通信贝/跨服通信贝。默认 `在<位置>发现<名字><se.1>`，位置通过游戏原生 `<flag>` 生成可点击地图链接（发送时更新旗标），名字是缩写；默认关闭、频道为默语 `/e`，只通知开启后新出现的 FATE，同场去重。默语和调试默语仅本地显示。

登录后自动读取可用库存；打开鸟包、收藏柜、幻化柜及各个雇员的背包/装备/货架时自动更新并按角色缓存。未获取的来源显示“未获取”，从上次记录恢复的显示“已缓存”，本次已读显示绿色“已更新”。柜子和雇员需要实际打开后才能获取新库存，未打开时显示的是上次缓存。

## 构建与加载

需要 .NET 10 SDK 和本机国服卫月 API 15 依赖。默认读取 `DALAMUD_HOME`，未设置时尝试 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`。

```powershell
Set-Location D:\project\ULKHelper
dotnet build -c Debug
# 手动指定依赖目录：
dotnet build -c Debug -p:DalamudLibPath="C:\path\to\Dalamud"
```

在卫月设置 → 实验性 → Dev Plugin Locations 添加构建产物 `bin\Debug\net10.0-windows\ULKHelper.dll`，启用开发插件后输入 `/ulk`。DLL 旁必须保留 `ULKHelper.json`；日志可通过 `/xllog` 查看。界面名称可能随国服版本不同。

当前沿用同级 MakoMaster 的本地程序集引用方式，没有 NuGet 包依赖。官方模板使用 Dalamud.NET.Sdk；后续如迁移到该 SDK，需同步调整清单生成和打包流程。

## 开发上下文

- [项目 context](docs/CONTEXT.md)：需求、环境、当前状态、设计方向、下一步。
- [Dalamud 开发流程](docs/DALAMUD_DEVELOPMENT.md)：生命周期、数据、调试与验证。
- [物品统计 UI 与数据来源](docs/ITEM_STATISTICS.md)：界面约定、装备范围、示例口径与离线预览方法。
- [真实库存统计](docs/INVENTORY.md)：来源、更新时机、缓存和验证范围。
- [自动化 helper](docs/AUTOMATION_HELPERS.md)：玩家列表、车头监听、目的地导航与验证范围。
- [天气预报](docs/WEATHER.md)：四岛概率来源、计算 helper、界面与验证范围。
- [地图与 FATE 计时](docs/FATE_MAP.md)：地图数据、ACT 参考规则、实例隔离和验证范围。
- [代理协作约定](AGENTS.md)：后续开发会话的入口。

0.0.1 本地发布包及共用库链已准备，上传步骤见 [发布说明](docs/RELEASE.md)。当前未上传 GitHub，也未安装到游戏目录。
