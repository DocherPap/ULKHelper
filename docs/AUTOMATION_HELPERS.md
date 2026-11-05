# 自动化独立 helper（首批）

最新整合见 [跟随伐木](FELLING_FOLLOW.md)：用户已授权开始/停止跟随、所选坐骑、头目目标、等级同步、RSR与BossMod联动。下面的独立helper仍保留测试；生产入口当前由FellingFollowHelper协调，后续说明以新文档为准。

打开 `/ulk` → 自动化，先选择玩家、设为车头。单独开启监听只标记有效坐标；“开始跟随伐木”才允许新坐标自动触发导航，单次“开始导航到目的地”也会在到达后完成战斗准备与战后上坐骑。停止导航会终止当前任务。

## 模块

- `PlayerListHelper.GetPlayers`：框架线程读取 `IObjectTable`，返回名字、原始服务器、位置与水平距离的托管快照，每秒刷新。包括自己；按名字和原始服务器去重。对象表只包含客户端已加载玩家，无法枚举地图远端尚未加载的玩家。
- `IslandRosterHelper` / `IslandRosterGameSource`：通过副本成员名单取得全岛成员，每30秒更新、`/ulk refreshplayers` 手动刷新；与每秒更新的已加载位置合并。`IslandTravelHelper` / `IslandCrystalRoutes` / `IslandCrystalGameAdapter`：水晶路线、原生交互与落地续走。当前导航入口已使用该包装器，详情见 [岛内水晶与玩家名单](ISLAND_TRAVEL.md)。
- `LeaderListenerHelper.Select / SetListening / Receive`：用名字＋原始服务器识别车头，记录最近 50 条消息；默认关闭，不持久化车头。说话、呼喊、喊话（Say / Yell / Shout）都监听。尚未增加开车时间、怪物名称、战斗指令等分析。
- `DestinationNavigationHelper.Start / Tick / Stop`：独立导航状态机，输入目的地、当前玩家、区域/地图/实例、进行中的 FATE 快照；时间由调用方提供，等待不阻塞游戏线程。
- `NavigationRules`：距离、最近 FATE、25/30 米站位与路线归属规则，可离线测试。
- `VnavmeshAdapter`：可选 IPC，不改变 vnavmesh 设置。仅导航依赖它，玩家列表、车头监听和物品统计都不依赖它。
- `AutomationHelpers`：服务宿主。UI 和聊天回调提交意图/托管数据，框架线程执行读取、地图标记与移动；UI 只读不可变快照。

## 坐标处理

使用消息的原始 sender / message，避免显示名或翻译插件影响。先读取 `PlayerPayload` 判断原始服务器，不凭可见名字猜服务器；无法识别发送者的消息跳过。

优先使用首个真实 `MapLinkPayload`。没有链接时，识别半角或全角括号内的 `(x, y)` / `（x，y）`，按当前地图转换；该回退只是坐标格式识别，没有语义分析。地图原始坐标除以 1000 转为世界 X/Z，距离全部按世界水平米数计算。纯文字坐标沿用 Dalamud 的默认 0.05 地图单位修正。

只接受当前区域、当前地图的坐标；距玩家当前位置 **≤25 米**跳过。有效坐标调用 `IGameGui.OpenMapWithMapLink`，保存为手动导航目的地。标记失败不替换目的地。未发现坐标或跳过新坐标时保留上次有效目的地。

## 导航规则

1. 必须手动开始，目的地须在当前区域和地图，玩家须可移动，vnavmesh 网格须就绪且无其他寻路/移动任务。
2. 在目的地水平 50 米内寻找最近的进行中优雷卡 NM（FATE）；同距离用 ID 稳定排序。用本机 `Fate.EurekaFate == 1 && Rule == 1` 筛选，已核实为 67 个 NM（包括兵武塔调查支援）；5 个幸福兔事件使用 Rule=4，排除。筹备中、结束中的事件不计入。
3. 找到 FATE 时，取“FATE 中心朝当前玩家”的单位方向，在中心前方 25 米处设终点并寻路。没有 FATE 时等满 2 秒，再向地图坐标出发。
4. 玩家距当前终点 ≤50 米时重新查询一次目的地附近 FATE。找到后改为中心朝玩家方向 30 米处；此时已在中心 30 米内则结束导航，避免向外折返。只重设一次，避免反复寻路。
5. 无高度的地图坐标用 `PointOnFloor` 从 Y=1024 向下找地面（与 vnavmesh 地图旗标解析一致）；FATE 站位用中心高度附近的 `NearestPoint`。不能找到地面或完整路径就结束，不直线强行移动。多层地图最高地面可能不等于期望楼层，需要游戏内确认。
6. 距终点水平 ≤3 米且高度差 ≤5 米时完成；路径提前结束则提示未到达。最长 5 分钟，不自动重试或恢复。

手动停止、地图/区域/实例/角色改变、登出、死亡、战斗、过场/事件占用、网格失效或依赖异常均停止本次导航。切区或切角色还清除车头、监听与目的地；停止监听本身不停止已经单独开始的导航。

寻路使用 `Nav.PathfindCancelable`，只取消自己的 token；结果仅在框架线程检查后才调用 `Path.MoveTo`，停止后的晚到结果不会触发移动。不调用全局 `PathfindCancelAll`。停止移动前比较当前路线是否为本插件原始路线的未消耗后缀，外部已替换的路线不停止。IPC 没有所有者句柄，其他插件提交完全相同的路径无法可靠区分；仍应避免同时运行多个移动插件。

## 到达处理（无新增 UI）

`ArrivalRules` 计算开怪与同步需求；`ArrivalGameAdapter` 在框架线程读取真实玩家/队伍/FATE/NPC，执行上/下坐骑；`DestinationArrivalHelper.Start/Tick/Stop` 为独立可测试状态机。现有手动导航的成功出口通过一次性 `TakeArrival()` 接入，取消、失败、切区不触发；没有新增按钮或自动跟车入口。

到达后等待目的地50米内的运行中优雷卡NM。绑定FATE ID和开始时间，满足以下任意条件即记为本场已开怪：附近至少四分之一玩家正在攻击；确认的NM血量严格低于80%；至少两名小队队友正在攻击。

人数口径：分母现在使用90秒内有效的缓存全岛名单人数，排除自己；名单未知/过期时不以附近人数替代比例条件。攻击人数来自FATE中心半径 `max(50米, Fate.Radius)` 内客户端已加载的存活玩家，按对象ID去重，排除自己；队友来自 `IPartyList` 的本小队，不含整个团队。空人数不满足比例条件。攻击证据为处于战斗状态，且对本FATE关联敌人施法或最近4秒有新的动作响应目标记录；仅选中怪物、拔刀或卷入其他战斗不会计数。第一次读到的旧动作仅建立基线。客户端看不到远端玩家的攻击状态，动作响应读取覆盖率仍须实测，不能声称等同服务器完整攻击人数。

血量只使用按本地FATE标题末尾NM名称匹配的唯一关联NPC；无法匹配、未加载或有歧义时血量未知，不拿FATE进度或小怪血量替代。攻击记录可包括对本FATE关联敌人的动作。

同步需求用 `AgentHUD.ExpContentLevel`（并验证InEureka标志）与 `IFate.MaxLevel` 比较，元素等级高于上限才需要；不使用职业等级。`FateManager.SyncedFateId` 已是本FATE时仍保持需要同步的15米分支，避免同步后误上坐骑。无法确认时暂停动作。本次只判断同步需求，不切换等级同步开关。

- 无需同步：已在坐骑上则不重复操作；否则尝试随机坐骑，再调整至FATE中心近侧25米。每3秒最多尝试一次，共3次；战斗中无法上坐骑或尝试用尽时仍调整站位。
- 需要同步：先下坐骑，确认已下坐骑后再靠近15米；3次下坐骑尝试仍未成功则停止，避免带坐骑进入。未在坐骑上直接靠近。
- 水平距离容差2米，高度差不超过5米；施法时停止自己的移动等待。后续偏离站位时重新调整，每次重新规划至少间隔2秒。使用原有地面/完整路径校验、可取消寻路和路线归属规则。
- 总时限5分钟；手动停止、切区/角色、死亡/过场占用、绑定NM消失或换场、依赖/路径失败时停止。到达阶段允许玩家进入战斗，前往目的地阶段仍保留原来的遇战停止规则；其他导航占用时释放控制，不操作坐骑或停止对方路线。

本机 `2026.10.06.0000.0000` GeneralAction 表核对：9=随机坐骑，23=跳下。调用 `ActionManager.GetActionStatus` 验证可用后 `UseAction(GeneralAction, ID)`；挂载状态前后校验，无跨帧原生指针。接口签名已按本机API15反射并编译核对，调用效果尚未游戏内实测。

来源：[AgentHUD](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentHUD.cs)、[CastInfo](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Character/CastInfo.cs)、[ActionManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/ActionManager.cs)，以及本机表和依赖。复核工具 `tools/InventoryInspect --arrival-api`、传入sqpack路径后加 `--arrival-data`。

## 原有接口基准与来源

本机：国服 Dalamud API 15（DLL 15.0.3.9），vnavmesh 1.2.3.14。使用本地 DLL 反射核对聊天事件签名、MapLinkPayload、IFate、客户端地图与 IPC 对应方法；同时确认安装包包含相关 IPC 端点。

NM 规则来源是本机 `2026.10.06.0000.0000` 中文 Fate 表，非猜测 ID 范围。`dotnet run --project tools/InventoryInspect -- "游戏目录/game/sqpack" --eureka-fates` 可复核 67 个 NM 与幸福兔 1367、1368、1407、1408、1425；正式逻辑读取行字段，不硬编码这些 ID。

- [IObjectTable 文档](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IObjectTable/)：对象表仅已生成对象。
- [Dalamud MapLinkPayload 源码](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Text/SeStringHandling/Payloads/MapLinkPayload.cs)：地图坐标换算与原始坐标精度。
- [vnavmesh IPCProvider](https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/IPCProvider.cs)：可取消寻路、地面查询、路线查询、开始与停止移动。
- [vnavmesh MapUtils](https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/MapUtils.cs)：无高度旗标的地面解析。

上游 master 会变化；实际编译以本机依赖为准，IPC 缺失时显示导航失败，不影响其余页面。

## 验证

`dotnet run --project tools/AutomationTests`：135项纯规则/模拟检查，包括原有规则、到达开怪/坐骑/同步/站位，全岛名单、水晶路线以及跟随/战斗启停/战后上坐骑与会话保护。原生部分均未在离线测试中实际执行。

`dotnet run --project tools/UiPreview -- --automation automation.png`，再加 `--small` 检查最小窗口。预览是模拟数据，不是真实玩家截图。

Debug / Release 编译与离线模拟检查完成。游戏内尚未验证：真实聊天 sender payload、地图标记、FATE/动作响应可见范围、元素等级读取、vnavmesh 当前优雷卡地图网格、终点及开始/停止移动、上/下坐骑。自动化没有发送聊天、攻击或传送动作；真实骑乘测试尚未执行。

testing.13：开怪比例使用本场附近存活且去重后的已加载玩家。具有目标 NM 攻击证据的人数达到三分之二即满足比例条件；血量低于80%与两名队友已攻击的独立条件保留。
