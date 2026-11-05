# 岛内水晶导航与玩家名单

0.3.2：常用功能与跟车共用最近水晶待机。未加载时地面查询只补高度；对象加载后按实际坐标和高度校正，旧寻路释放后重算。实际路线终点和到达位置均须满足水晶 10 米内及高度差 3 米。常用功能到达后结束单次任务，跟车继续等待新坐标。

2026-11-05 风岛待机校正：待机使用只读Locate取得实际水晶XYZ，不交互。未加载先从玩家当前层查询参考点地面，靠近加载后重新校正；待机落点水平距实际水晶不超过10米、垂直差不超过3米，通过NavigationEndpoint的候选过滤器拒绝屋顶和25米搜索漂移。通用导航仍保留25米搜索范围，功能约束作为额外过滤传入。

2026-11-05 终点统一检查：NavigationEndpoint.Start输入原终点及是否已知高度；Next发起异步完整路径查询，框架线程确认任务完成后TakeDestination返回可达坐标及路径。圆心落地但路径不完整时继续尝试25米内按水平距离排列的地面采样点，全部失败才报不可达，不移动残缺路径。已知高度保留8米楼层限制。普通目的地、兔子寻宝、跟车、单次FATE、自动触发普通寻路、常用行程均通过DestinationNavigationHelper接入；水晶路长预估共用同一检查。网格查询与任务完成处理均在框架线程，不阻塞等待。特殊骑乘记录路径仍保留记录顺序，短距离战斗技能地面检测保留其距离限制。

2026-11-03。用户已授权将水晶传送加入目的地导航，并通过副本玩家名单取得全岛玩家，每30秒或手动刷新。保留四页，不新增控件；已有玩家列表显示名单与已加载位置，命令 `/ulk refreshplayers` 提供手动刷新。没有自动开始跟车，也没有额外网络服务或Lifestream依赖。

## 水晶路线

### 最新修正与入口导航（2026-11-03）

重新核对Lifestream main的CustomAethernet：四岛均明确支持；不是“不支持优雷卡”。本插件继续使用自己的目标/交互/菜单，无需安装Lifestream。其Utils.IsAetheryte除普通Aetheryte外还识别EObj传送碎晶；此前本插件仅判断ObjectKind.Aetheryte，可能漏掉这些对象。现按本机EObjName基准2000151及Lifestream同义碎晶基准（只使用本机存在且名称非空的行）的精确中文名称解析碎晶BaseId集合，再结合EventObj类型、当前源点参考位置及5米实际交互距离识别，不盲选其他对象。该基准本机表已验证，真实场景仍需确认。

源水晶改走面向玩家的2米接近点，并使用1米到达容差，避免普通3米到达容差使交互过早。已在源点3米内则保持当前位置交互。菜单超时、未加载/超距、目标不在可用菜单、源点不可达等回退原因写入日志；路线仍仅在有明显预计收益时使用，短途不强制传送。

地图选中当前岛FATE后“自动前往”只移动，采用既有25/30米NM接近规则，不启用循环/AI；“自动前往入口营地”和 `/ulk entrance` 使用四岛入场水晶PlaceName 2415/2463/2531/2876附近7米点，非兵武塔入口，不离开副本。可选坐骑、传送规划、5分钟时限及停止/切区/死亡约束继续生效；没有选定坐骑时允许步行。`/ulk stop` 或地图停止按钮可取消，只停止本插件路线。自动化页暂时置灰时以上入口仍可使用，不需要RSR/BossMod。

最新模拟验证：源水晶接近容差、四岛入口映射及开怪后不追加站位寻路合计150项自动化检查通过；真实传送/对象类型/菜单交互尚未游戏内验证。

核对来源：[CustomAethernet](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/Systems/Custom/CustomAethernet.cs)、[Utils](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/Utils.cs)、[传送任务](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/Tasks/SameWorld/TaskAethernetTeleport.cs)。

`IslandCrystalRoutes` 保存四岛14个水晶（3/4/4/3）的真实PlaceName ID及参考世界X/Z。数据参考 [Lifestream CustomAethernet](https://github.com/NightmareXIV/Lifestream/blob/b36320170faafc8315f7585edb45cfbcd34e7046/Lifestream/Systems/Custom/CustomAethernet.cs)，14个ID均已读取本机2026.10.06游戏表核对中文名称。高度不猜测，由现有vnavmesh地面接口解析；实际对象在交互前必须已加载并在玩家5米内、参考点10米内。

`Choose` 比较直走水平距离与“到源水晶+120米等效传送开销+目标水晶到目的地”的估算成本，额外节省超过150米才选择。此为保守启发式，不是精确时间或完整地形最短路；尤其恒冰悬崖/楼层需游戏内验证。实际移动仍需完整路径与正确高度校验，源水晶不可达时，在没有其他导航占用的情况下回退原目标直走。

`IslandTravelHelper` 管理源水晶→目的地菜单→确认落地→最终导航。源水晶一段不搜索附近NM，只有最后一段才使用既有25/30米导航规则，并且只有真正完成最后一段才发出到达信号，衔接开怪/15或25米站位。总时限5分钟，菜单15秒，传送落地45秒。

`IslandCrystalGameAdapter` 直接使用本机 `TargetSystem.SetHardTarget/InteractWithObject` 与 `AddonSelectString`。检查目标对象ID、菜单可见/Ready/ID、条目数量、组件长度、文字唯一精确匹配以及禁用状态；仅选当前岛屿可用目的地。未解锁或没有可用目标则取消自己打开的菜单并回退直走，不执行共鸣/解锁操作。上坐骑到源水晶后最多每3秒尝试一次下坐骑，最多3次，确认已下后才交互。

菜单仅交互一次，不反复重开；外部对话已打开时不接管。停止只关闭自己目标对应的菜单、取消自己的寻路，不调用其他插件全局Abort。已向服务器提交的传送无法撤回；手动停止后即使传送落地也不会自动恢复移动。

预期同岛传送时允许过图黑屏及短暂缺少本地对象，按稳定的角色ContentId保持会话，允许区域/地图短暂为0；真实切岛、地图/公开分线/角色变化仍取消。只有玩家回到有效状态、施法结束，并在所选目标水晶水平15米内至少等待1秒后，才继续原目的地。超时不在错误位置继续，也不误报到达。

## 全岛名单

`IslandRosterGameSource` 读取 `InfoProxyContentMember` 的 `CharData` 与 `MemberData`，原始数据不受对象表远距离生成范围限制。调用游戏原有 `RequestData()`，无需打开玩家名单窗口。每30秒自动请求，手动刷新最短间隔5秒；战斗中可更新，过图、死亡或占用等不适合读取时延后。

玩家在社交窗口手动刷新产生的完整 ContentMember 回调也可更新快照，即使插件没有等待自己的请求。调试报告列出回调编号、原始条数、筛选后条数及是否包含自己，不记录玩家姓名。

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

测试分支 FATE 行程：优先水晶策略使用 60 米等效传送成本和 25 米额外收益门槛，选目标时仍核对实际可用菜单。距离源水晶参考点 3 米内可直接进入交互阶段。普通坐标导航保留原估算，原版和 Reborn 使用相同水晶策略。

测试分支：自动行程到达源水晶后如在战斗，暂停水晶菜单并用 RSR Auto 等待脱战。已有循环交由原使用方管理；插件仅关闭本次启动且所有权仍有效的循环。脱战后重新开启水晶菜单。单次等待上限 60 秒，停止或失败释放本次循环，元灵巡查仍按原规则遇敌停止。

测试分支 FATE 路线在发出移动前，用 vnavmesh 逐条规划直达、到最近两个可测源水晶，以及各候选落地后到目标的路径，以三维路径长度比较。传送仍计 60 米等效成本与 25 米收益门槛，目标需在实际菜单可用。规划最多 10 秒，无法测量时用原平面估算。接近源水晶优先使用唯一已加载水晶的实际坐标，保留原路线回退。

testing.13：活动 FATE 导航及开怪站位使用客户端 FATE / 已加载头目的实际世界 Y。已知高度同时用于 vnavmesh 水晶路长比较，避免把恒冰荷鲁斯、双牛、娄希的洞内目标投到上层。没有活动场次时的地图旗标缺少高度，无法据此确认洞层。

0.3.1：坐骑选择统一由 MountGameAdapter.EnsureTravelMount 处理，优先恢复用户设置，缓存因切区清空时可立即刷新。TravelMountHelper 供 Discord 等待及岛内行程复用，框架线程每三秒最多尝试一次、最多三次，并等待实际骑乘状态。

0.3.1：自动导航统一使用 NavigationGround，从目标中心搜索 25 米内可站立地面，候选按离终点的水平距离排序；已知高度保留上下层限制。没有完整路径时有界尝试其他落点。交互目标仍需满足实际对象距离。
