# 岛内水晶导航与玩家名单

2026-11-03。用户已授权将水晶传送加入目的地导航，并通过副本玩家名单取得全岛玩家，每30秒或手动刷新。保留四页，不新增控件；已有玩家列表显示名单与已加载位置，命令 `/ulk refreshplayers` 提供手动刷新。没有自动开始跟车，也没有额外网络服务或Lifestream依赖。

## 水晶路线

`IslandCrystalRoutes` 保存四岛14个水晶（3/4/4/3）的真实PlaceName ID及参考世界X/Z。数据参考 [Lifestream CustomAethernet](https://github.com/NightmareXIV/Lifestream/blob/b36320170faafc8315f7585edb45cfbcd34e7046/Lifestream/Systems/Custom/CustomAethernet.cs)，14个ID均已读取本机2026.10.06游戏表核对中文名称。高度不猜测，由现有vnavmesh地面接口解析；实际对象在交互前必须已加载并在玩家5米内、参考点10米内。

`Choose` 比较直走水平距离与“到源水晶+120米等效传送开销+目标水晶到目的地”的估算成本，额外节省超过150米才选择。此为保守启发式，不是精确时间或完整地形最短路；尤其恒冰悬崖/楼层需游戏内验证。实际移动仍需完整路径与正确高度校验，源水晶不可达时，在没有其他导航占用的情况下回退原目标直走。

`IslandTravelHelper` 管理源水晶→目的地菜单→确认落地→最终导航。源水晶一段不搜索附近NM，只有最后一段才使用既有25/30米导航规则，并且只有真正完成最后一段才发出到达信号，衔接开怪/15或25米站位。总时限5分钟，菜单15秒，传送落地45秒。

`IslandCrystalGameAdapter` 直接使用本机 `TargetSystem.SetHardTarget/InteractWithObject` 与 `AddonSelectString`。检查目标对象ID、菜单可见/Ready/ID、条目数量、组件长度、文字唯一精确匹配以及禁用状态；仅选当前岛屿可用目的地。未解锁或没有可用目标则取消自己打开的菜单并回退直走，不执行共鸣/解锁操作。上坐骑到源水晶后最多每3秒尝试一次下坐骑，最多3次，确认已下后才交互。

菜单仅交互一次，不反复重开；外部对话已打开时不接管。停止只关闭自己目标对应的菜单、取消自己的寻路，不调用其他插件全局Abort。已向服务器提交的传送无法撤回；手动停止后即使传送落地也不会自动恢复移动。

预期同岛传送时允许过图黑屏及短暂缺少本地对象，按稳定的角色ContentId保持会话，允许区域/地图短暂为0；真实切岛、地图/公开分线/角色变化仍取消。只有玩家回到有效状态、施法结束，并在所选目标水晶水平15米内至少等待1秒后，才继续原目的地。超时不在错误位置继续，也不误报到达。

## 全岛名单

`IslandRosterGameSource` 读取 `InfoProxyContentMember` 的 `CharData` 与 `MemberData`，原始数据不受对象表远距离生成范围限制。调用游戏原有 `RequestData()`，无需打开玩家名单窗口。每30秒自动请求，手动刷新最短间隔5秒；战斗中可更新，过图、死亡或占用等不适合读取时延后。

分页由 `InfoProxyPageInterface.AddPage` 管理。使用 `IGameInteropProvider` 创建 `EndRequest` 虚函数槽10的hook，先调用原函数，仅当接收者是ContentMember代理时递增完成计数。回调只记录计数，名单复制在框架线程完成；不猜等待几秒就等于已收齐。hook按需创建、卸载释放。没有跨帧保存成员/对象指针。

复制前验证200人上限、原生容量、非空数据；按区域筛选（未提供区域的0值保留）、名字+原始服务器去重，并要求名单含当前玩家以拒绝明显错误/旧列表。请求最长等待15秒，失败保留上次缓存；切区/角色清空托管快照，旧请求未完成时不立即并发发新请求。原生请求长期无返回时30秒后允许再请求，仍由游戏分页协议处理请求ID。

名单、实体ID和时间仅保存在内存，不持久化或上传。名单并不提供实时位置、HP或攻击状态。已加载对象每秒更新位置；名单中远处成员显示“位置未加载”，仍可按名字/原始服务器选为车头。

开怪比例分母改为90秒内有效的缓存全岛人数，排除自己。若新出现的可观测玩家更多，则分母至少不小于附近已观测人数；攻击分子继续使用附近存活玩家的NM动作证据。名单未知/过期时禁用四分之一比例条件，低于80%血量或两名队友攻击两项仍可独立成立。名单无法确认远处成员是否存活，因此分母保留他们，宁可保守等待。

## 验证与来源

- `tools/AutomationTests`：102项通过（原有68项，加名单刷新/手动节流/缓存/超时/合并/全岛人数条件，以及水晶选择/菜单/落地/超时/停止/坐骑/不可达回退）。所有动作均为模拟。
- `tools/InventoryInspect --travel-api` 反射核对本机InfoProxy、SelectString、AtkUnitBase方法与结构；传入sqpack并加 `--crystal-data` 核对14个中文水晶名称。
- Debug/Release构建通过；原页面最小窗口离线绘制 `artifacts/ui/automation-roster.png`，使用模拟玩家。
- 游戏内名单请求及hook、菜单/目标交互、传送和落地续走未实测；本次不更新已准备好的0.0.1发布ZIP。

协议依据：[InfoProxyContentMember](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyContentMember.cs)、[InfoProxyPageInterface](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyPageInterface.cs)、[InfoProxyInterface](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyInterface.cs)、[AddonSelectString](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/AddonSelectString.cs)。实际调用以本机国服API15依赖为准。
