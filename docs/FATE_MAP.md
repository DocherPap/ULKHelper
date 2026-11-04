# 四岛地图与 FATE 计时

打开 `/ulk` → 地图 / FATE。目前顶部为自动化、物品统计、天气预报、地图 / FATE 四个等宽页签。

## 界面

四岛可自由切换，也可跟随当前岛屿。左边为游戏本地地图、固定点位及玩家位置，右边为可搜索的 FATE 列表与计时；中间分隔线可拖动。默认包括幸福兔，可隐藏幸福兔或点位简称。

地图滚轮缩放 1–4 倍，按住左键拖动平移，点击点位选择对应事件。选中后可查看全名、等级、坐标、出现记录、参考冷却和天气/夜间条件，也可在游戏地图标记坐标。标记不会触发导航。

绿色：筹备/进行中。橙色：预计冷却或等待天气/夜间。黄色：已记录的预计冷却结束且条件可用，仍须触发。灰色：没有记录或活动观测已过期。没有记录时不猜服务器历史。

右边有“分钟前出现”输入与“补记出现时间”，只允许在当前所在岛屿补记，支持 0–240 分钟；可以清除单条记录。进行中的事件不能用手动时间覆盖，游戏观测优先。

## 出现通知

地图页顶部新增“发送通知频道”勾选框、频道选项与文字框。默认关闭，默认频道默语 `/e`，默认文本 `在<位置>发现<名字><se.1>`。目前共26个选项：喊话、呼喊、说话、小队、团队、部队、默语 `/e`、调试默语、新人频道、PvP战队、通信贝1–8和跨服通信贝1–8。下方说明：`<位置> 代表位置，<名字> 代表 FATE 缩写`。

默语与调试默语仅在本机显示；选中时界面明确提示。默语走游戏 `/e`；调试默语通过 `IChatGui.Print` 的 `XivChatType.Debug=1` 输出，没有游戏命令前缀，不会对外发言，也不执行 `<se.1>` 等游戏宏（保留原文）。其余频道仍受游戏自身的频道权限/成员资格限制。扩展频道命令 `/e`、`/b`、`/pt`、`/l1`–`/l8`、`/cwl1`–`/cwl8` 已核对本机中文 TextCommand 表；调试类型已反射核对本机API15。

勾选后，只对随后客户端新观察到的 FATE 提交一次通知，包括幸福兔。发送时将游戏旗标设为该 FATE 实际中心，用原生 `<flag>` 展开为可点击地图链接（会替换当前旗标）；名字使用地图已有缩写。调试默语通过标准 `SeString.CreateMapLink` 生成可点击位置，不执行宏或修改旗标。`<se.1>` 原样交由游戏处理，实际提示音/频道可用性服从游戏规则。

开启或进岛时建立已有事件基线，不补报正在发生的事件，也不为众包/手动历史、晚加载的旧事件、筹备转进行中重复发言。同ID一分钟内额外去重；后续新一场同名事件可再报。多个事件至少间隔2秒发一条；超过30秒或已经结束的队列项丢弃。取消勾选、切岛、修改文本/频道清理待发队列；失败不自动重发。设置仅本次插件会话保留，重载默认关闭。

`Fates/FateNotificationHelper.cs` 管理基线与队列；`GameInterop/FateChatSender.cs` 在框架线程调用本机API15 `AgentMap.SetFlagMapMarker`，通过 `UIModule.ProcessChatBoxEntry` 提交固定频道前缀与 `<flag>`，当前帧创建并释放 Utf8String。拒绝空白、换行/控制字符或超过400 UTF-8字节的模板；最终编码超过500字节不发送。

最新FATE/地图检查共76项通过，涵盖通知默认值、占位符、原生flag命令、实际位置与缩写、基线不补报、转换/重复去重、新一场再报、关闭/切岛丢弃、发送间隔/过期/失败不重发，以及26个频道的索引、命令和本地输出分类。均使用模拟发送器，没有向真实玩家发送测试消息；游戏内频道发送、链接和提示音效果尚待验证。

地图页已移除颜色说明与“本次实例……”状态行，保留众包查询结果和通知状态；默认及最小尺寸离线布局、地图点位点击已检查。

## 计时口径

- 进行中 FATE：框架线程每 0.5 秒读取 `IFateTable` 的出现时间、剩余时间、进度、筹备/运行/结算状态；UI 持有托管快照，剩余时间按现实时间递减。
- 预计冷却：参照 ACT cactbot，由 **出现时间** 起算，常规 NM 为 120 分钟；UFO 与兵武塔调查支援为 20 分钟；恒冰/涌火幸福兔 8 分钟、丰水幸福兔 10.5 分钟。不是“击杀后统一两小时”，也不是保证刷新时间。
- 中途加入时，优先用游戏 FATE 的真实 `StartTimeEpoch`，不能取得有效时间时才以首次看到为估计。筹备中没有剩余时间则只显示筹备状态。
- 从客户端 FATE 表消失不等于击杀。继续保留原记录，超过 5 秒未更新标为活动数据过期；只有实际结算/结束/失败状态，或已知活动截止时间过去，才切换为非活动计时。
- 已记录的冷却结束后，按已有天气 helper 查找所需天气窗口，并检查 ET 19:00–06:00 夜间条件；同时存在的条件取交集。名称是“条件可用 / 待触发”，不会写成“已经刷新”。

刷怪进度、房间其他玩家行为、幸福兔寻宝持有状态，以及失败/取消事件可能改变实际触发情况；目前没有读取这些隐藏条件。ACT 的固定时间规则在这里也是估计值。

当前连续入场期间记录本地观察、手动补记及进岛时读取的一次众包快照。地图/区域/公开实例编号/角色变化时清空全部计时，登出或重载同样不保留。已增加专用只读 Zone Server ID 获取和官方众包接口，详见 [进岛众包说明](CROWDSOURCE.md)；不是周期在线史书同步，不依赖 ACT 安装。

最新点位直接显示刷新剩余、天气等待或天气窗口剩余；触发小怪等级、名字及特殊触发说明已补入全部72个事件。没有历史冷却时也会展示天气预报，并保持无条件事件的历史未知状态。

## 模块

- `Fates/FateSeeds.cs`：四岛 72 个固定点位（20/19/19/14；67 个 NM、5 个幸福兔），简称、参考冷却与条件。
- `Fates/FateCatalogLoader.cs`：本机 Lumina 解析完整中文 FATE 名称、等级、地图参数与纹理路径；校验 ID 唯一、全量优雷卡 FATE 覆盖、幸福兔分类和坐标范围。
- `Fates/FateLedger.cs`：纯托管、单次入场的活动与手动记录。
- `Fates/FateTiming.cs`：计时展示、天气/夜间窗口、倒计时格式。UI 每秒刷新规则计算，倒计时可逐帧显示。
- `Fates/FateMapTracker.cs`：框架线程读取游戏状态和执行地图标记，向 UI 发布不可变记录快照；卸载时反注册 Update。
- `Windows/GameMapTextures.cs`：使用 `ITextureProvider.GetFromGame` 的共享纹理，wrap 只在当前 Draw 帧内使用。
- `Windows/FateMapPage.cs`：地图交互、搜索、选中项详情与手动补记。

## 来源与坐标

固定点位和参考条件来源：[cactbot ui/eureka](https://github.com/quisquous/cactbot/tree/8d12b650896aed42e56bcfe07c8580426ab2b9f8/ui/eureka)，版本 `8d12b650896aed42e56bcfe07c8580426ab2b9f8`。计时起点参考该版本 `eureka.ts` 的 `OnFatePop / RespawnTime`；夜间定义参考 `resources/weather.ts`。已将坐标/条件转换为本项目 C# 数据，并附 Apache-2.0 许可证和来源于 `ThirdPartyNotices/`，构建时一同复制。

本机中文游戏数据：`2026.10.06.0000.0000`；Dalamud API 15。全名/等级读取 Fate 表；底图读取游戏文件，未下载或分发 cactbot 图片，也不打包游戏地图。

| 岛屿 | TerritoryType | Map | 游戏纹理 |
| --- | --- | --- | --- |
| 常风 | 732 | 414 | `ui/map/z3fa/00/z3fa00_m.tex` |
| 恒冰 | 763 | 467 | `ui/map/z3fb/00/z3fb00_m.tex` |
| 涌火 | 795 | 484 | `ui/map/z3fc/00/z3fc00_m.tex` |
| 丰水 | 827 | 515 | `ui/map/z3fd/01/z3fd01_m.tex` |

四岛 SizeFactor=100；前三岛 OffsetX/OffsetY=0，丰水 OffsetY=475。地图坐标投影到纹理 UV 为 `(map - 1) * (SizeFactor / 100) / 41`；世界位置和标记坐标换算同时处理偏移，不能把丰水按零偏移计算。点位是参考发生中心附近，实际活动中以游戏 FATE 位置为准；当前底图点位保持固定参考点。

## 验证与限制

```powershell
dotnet run --project tools/FateTests -- "D:\Program Files (x86)\FFXIV\game\sqpack"
dotnet run --project tools/InventoryInspect -- "D:\Program Files (x86)\FFXIV\game\sqpack" --map-data
dotnet run --project tools/UiPreview -- --map --map-selected fate-map.png
dotnet run --project tools/UiPreview -- --map --hydatos --small fate-map-hydatos-small.png
dotnet run --project tools/UiPreview -- --map --map-click fate-map-click.png
dotnet run --project tools/UiPreview -- --map --map-scroll fate-map-zoom.png
dotnet run --project tools/UiPreview -- --map --map-pan fate-map-pan.png
```

36 项地图/计时检查通过，包括四岛本地纹理、72 个 ID、全部坐标往返、丰水偏移、未知历史、实际出现时间、活动过期、明确结束状态、手动补记和清除、夜间/天气窗口及例外冷却。ImGui 输入模拟核验点击选择、滚轮缩放、拖动平移且不会误选点位；默认/最小窗口与四岛实际底图均已离线目视检查。预览中的活动/计时记录是模拟数据。

Debug / Release 编译与原有库存、自动化、天气检查通过；游戏内实际 FATE 表覆盖范围、状态转换、地图标记、跨房间清空及真实计时仍须验证。没有自动发言、攻击或移动动作。

## 元灵点位

地图和自动化地图可显示元灵，实心菱形表示当前客户端可见，空心菱形表示过去实测的位置。鼠标悬停显示地图坐标和最后观察时间。每0.5秒读取当前岛已加载的存活 BattleNpc，约5米内合并，每岛保留最近128点。位置只按岛屿和地图保存到本地设置，离岛或重载后只显示为历史。历史位置不表示当前刷新。

本机国服 2026.10.06.0000.0000 的 BNpcName 表已核对 7184 常风元灵、7567 恒冰元灵、7764 涌火元灵、8131 丰水元灵。用 InventoryInspect 的 --elemental-data 可复核。出生点会变动的资料见 [Anemos Elementals](https://ffxiv.consolegameswiki.com/wiki/Anemos#Anemos_Elementals)。本功能从本地游戏观测收集点位，尚未导入静态候选点表。
