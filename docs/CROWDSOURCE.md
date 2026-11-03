# 进岛众包快照与触发小怪

用户授权接入 [Critical Encounter Crowdsourcing](https://ce-crowdsource.atmoomen.top/)，并明确要求 **只在进岛时读取一次**。本实现严格按单次入场查询：一次区域订阅 POST，随后一次快照 GET；没有周期拉取、续期、失败重试、WebSocket、心跳或观测上传。

## 官方协议与实测

使用网站官方的 [外部调用指引](https://ce-crowdsource.atmoomen.top/articles/0002)，源码文档位于 [0002_chs.md](https://github.com/Dalamud-DailyRoutines/CriticalEncounter.CrowdSource/blob/c2bf132ca39c1467c60bb87b97c3281160ae3c80/web/public/assets/articles/0002_chs.md)。不是其他插件的私有桥接 API。

1. `POST https://ce-crowdsource.atmoomen.top/v1/subscriptions`，JSON 为 `dataCenterID` 与单个 `{ instanceID, territoryID }`。
2. 校验返回大区、实例、区域及订阅有效期。
3. GET 返回的 `snapshotURL`，仅接受 HTTPS `ce-data.atmoomen.top` 和当前大区/实例/岛屿路径；拒绝重定向到其他地址，保持正常 TLS 验证。解析 schemaVersion=1，全部时间戳按 UTC Unix 秒处理。
4. `tracks` 按服务端轨迹序号递增，取最后一条最新轨迹，不把候选轨迹混合。只导入 `FATE`、本岛已知 ID、过去24小时内且非未来的发生时间。null 表示未记录，不补成现在。

已实际完成一次官方区域订阅，返回200，随后读取返回地址取得200 JSON快照；样例房间在该区域的 `tracks=[]`。这证明接口与格式可用，不代表该样例房间存在常风事件。首页浏览器验证后可以正常查看官方文档；公开 `/v1/subscriptions` 可直接访问。未使用其他插件的上传凭据。

服务限制持续读取至少间隔30秒；本实现每次入场只读取一次，因此不会使用该持续读取流程。跨入场的订阅尝试至少间隔6秒。失败会显示原因，本次不重复请求。订阅有效期只用于本次下载校验；下载后保存的是“截至进岛读取时已知的历史发生时间”，并非持续更新的在线状态，不复用过期下载地址。

## 副本 ID 与会话隔离

网站 `instanceID` 实际是 Zone Server ID，不是 `IClientState.Instance` 的拥挤区域分线编号。按官方说明，由 Zone Init 中 ushort Server ID（高16位）和 ushort Instance ID（低16位）拼成 uint。

读取来源：网站官方 [InstanceFinder](https://github.com/Dalamud-DailyRoutines/CriticalEncounter.CrowdSource/blob/c2bf132ca39c1467c60bb87b97c3281160ae3c80/InstanceFinder/GameProcess.cs) 与作者 [OmenTools GameState](https://github.com/AtmoOmen/OmenTools/blob/main/OmenService/Implementations/Infos/GameState.Internal.cs)。本机国服游戏文件 `2026.10.06.0000.0000` 离线核对：当前 manager 签名和字段签名分别唯一匹配，字段偏移 `0x5D0`；旧 manager 签名无匹配。代码每次启动动态解析，不把偏移硬编码为执行依据。

`ZoneServerIdReader` 只保存托管 RVA/字段偏移，每次在框架线程重建本帧地址，通过本进程只读读取6字节；没有原生 hook 或游戏内写入。匹配/范围校验失败时返回0并在UI解释，不猜房间号，不发无范围查询。

进入当前岛屿后等待2秒，再确认房间号至少稳定1秒，取得当前世界对应的大区后启动单次请求。大区/房间/岛屿是完整请求范围；不发送角色名字、Content ID、玩家位置或库存。Content ID 只在本地用于角色会话隔离，短暂缺少玩家对象不会重新启动入场查询。

离岛、地图/区域/公开分线/角色变更时取消请求、丢弃晚到结果、清空本次记录并复位单次查询资格。即使再进同一个房间号，也是新的一次入场。没有跨角色/跨房间持久化。

## 合并与显示

众包快照只有“最近一次已观测发生时间”，不会据此假装 FATE 当前正在进行。当前活动以游戏内 FATE 表为准。本地活动记录优先；对于非活动事件仅用比现有记录更新的众包时间补入，并标注来源“进岛众包快照”。手动记录和本地观察机制继续可用。

地图点位始终显示计时，名称可用“显示简称”切换，标签自动尝试避让并用引线连接点位：

- 已记录出现且还在参考冷却内：`刷新剩余 HH:mm:ss`。正在进行的事件在点位上也优先显示下一次预计刷新，右侧列表仍显示本次活动剩余时间和进度。
- 无冷却阻挡且需要天气、天气未到：`天气等待 HH:mm:ss`，未掌握历史冷却时同样可以展示独立的天气预报。
- 所需天气已到：`天气剩余 HH:mm:ss`，连续相同天气段合并成一个窗口。
- 仅夜间条件：显示夜间等待/剩余。没有记录且没有时段限制的仍显示未记录，不能断言刷新。

“天气剩余”描述天气窗口，夜间准备/刷怪要求另看触发说明；并不宣称隐藏刷怪条件已完成。

## 触发小怪数据

`Fates/FateTriggers.cs` 按72个FATE ID整理中文触发说明，来自网站官方 [dynamic-event-catalog.json](https://github.com/Dalamud-DailyRoutines/CriticalEncounter.CrowdSource/blob/c2bf132ca39c1467c60bb87b97c3281160ae3c80/web/public/assets/dynamic-event-catalog.json)，版本 `c2bf132ca39c1467c60bb87b97c3281160ae3c80`。包括小怪等级、名字、准备天气/夜间要求；例如仙人刺需要6级仙人花。UFO为特殊触发，兵武塔支援由塔内触发，五个幸福兔没有指定触发小怪，均明确说明，不编造击杀数量。

右侧每条FATE、选中详情、地图悬浮提示都显示说明。采用 MIT 数据来源，许可证和出处在 `ThirdPartyNotices/`，构建时一同复制。

## 模块与验证

- `Crowdsource/CrowdsourceClient.cs`：官方订阅/单次读取、响应体上限、范围/地址/格式校验。
- `Crowdsource/EntryCrowdsourceLookup.cs`：一次入场单次查询、取消、晚到结果隔离；没有后台周期任务。
- `Crowdsource/ZoneServerIdReader.cs`：本机只读房间 ID。
- `FateMapTracker`：框架线程采集上下文、启动请求、合并托管结果。HTTP在异步任务中运行，不读取游戏指针。

`dotnet run --project tools/FateTests -- "D:\Program Files (x86)\FFXIV\game\sqpack"`：54项通过，包含最新轨迹/空历史、非法范围/地址/未来时间、合并优先级、单次查询/失败不重试/离岛取消、一次POST+一次GET、72条触发说明，以及天气等待/剩余与刷新标记。

`dotnet run --project tools/InventoryInspect -- --zone-image "D:\Program Files (x86)\FFXIV\game\ffxiv_dx11.exe"`：只读游戏磁盘文件核对签名。

地图预览在 `artifacts/ui/fate-map-crowd*.png`，使用真实底图和模拟记录；接口下载分析在忽略的 `artifacts/crowdsource-reference/`，明确排除这些参考源码进入编译。Debug/Release及原有回归检查通过。尚未游戏内验证原生房间号的实时值、真实进岛自动查询与实际记录对应关系。
