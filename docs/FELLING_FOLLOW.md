# 跟随伐木、坐骑与战斗联动

更新于2026-11-03。用户已明确授权将车头监听、坐骑、岛内水晶导航、开怪判断、目标选择、等级同步、自动循环和BossMod整合为显式开始的流程。默认不运行。最新自动化页左侧为地图及自动触发预览，右侧为车头/右旁紧凑岛民名单、导航和可折叠消息；坐骑下拉放在导航区，名单请求状态行已移除。

## 使用与当前行为

最新变更（2026-11-03）：主窗口最后的自动化页暂时置灰，保留实现等待重新开放；地图单独导航和入口营地按钮仍可用。跟随准备阶段开怪后只确认下坐骑/头目/等级同步并开启RSR/BMR，不再发起15/25米新目的地，移动交给AI。确认事件结束/头目死亡时，同一更新周期关闭自己的控制；若已脱战且没有施法，立即尝试召唤所选坐骑，否则在首次满足条件时尝试，不额外等待固定秒数。地图不可见仍按原保守规则等待确认，不能伪造FATE结束。新增对应回归，当前150项自动化检查通过。

所选坐骑、玩家搜索和按角色车头已改为持久设置；只恢复选择，监听/跟随/战斗开关不恢复，切区仍停止任务。完整保存机制见docs/SETTINGS.md。

1. 自动化页选择已拥有的可移动坐骑，在玩家名单中选车头。
2. 点击“开始跟随伐木”，自动开始监听车头的说话/呼喊/喊话。不会使用启动前的旧坐标自行出发。
3. 收到当前岛屿有效新坐标后，未在战斗时开始导航。已在坐骑上不重复操作；否则召唤所选坐骑，确认上坐骑后才移动。岛内水晶传送后也重新检查坐骑。
4. 到达后无论NM是否已出现，都先下坐骑等待。满足原有三种开怪条件之一后，持续选中确认的头目；同步需求按元素等级判断。需要时先靠近15米并请求等级同步，等待SyncedFateId确认；无需同步则在中心25米以内准备。
5. 确认下坐骑、头目可选中及同步就绪后，开启Rotation Solver Reborn的Manual模式（自动执行当前目标循环，避免自动换到小怪），同时开启BossMod/BossMod Reborn AI移动。目标每250ms检查，不使用镜头锁定或自动更换焦点目标。
6. 头目死亡或FATE进入结束状态后立即关闭本插件启用的控制，脱战且施法结束后召唤所选坐骑。战斗中收到新坐标只保留最新一个；战后确认上坐骑，附近有新NM则前往，没有则靠近最近水晶待机，不在战斗中转向。

## 自动触发预览与水晶待机

自动化左侧复用原地图的本机底图、缩放/平移、玩家点位、NM点位及悬浮说明。点击点位后“加入列表”，最多3个本岛NM，支持移除；列表显示已有触发小怪说明。切换岛屿清空列表，幸福兔不加入。自动化地图使用紧凑简称，原地图/FATE页保留完整计时。列表有独立滚动，最小窗口下仍可查看3项。

“开始触发（未完成）”位于左侧触发列表标题旁且禁用，不发起小怪选择/击杀/导航，不编造触发所需数量。`TriggerSelection` 只管理本会话界面选择；它不会影响跟随的车头坐标或FATE记录。右侧保留跟随、导航；“当前目标设为车头”通过框架线程读取当前选中的玩家，NPC/无目标不替换车头；名单选择的“设为车头”位于刷新按钮旁。

### 未触发NM的小怪击杀记录

`TriggerKillGameTracker` 每250ms只读已加载战斗NPC，`TriggerKillLedger` 保存托管ID/等级/生命状态，不保存原生指针。仅相同对象ID、名称ID和等级在2秒内从活到死计数；首次看到尸体、消失、重复尸体与采样长间断均不推算。没有攻击归属限制，因此数值是本地看到的死亡数，而非本人击杀或全岛实际触发进度。

名称和等级来自既有官方MIT触发说明，按本地BNpcName精确解析名称ID集合；本机游戏数据已核验65条普通NM规则全部可解析。UFO/支援塔等特殊条件不猜测名称或数量。规则不用于执行自动攻击。

未触发NM的累计数列在右侧历史上方，左侧选中列表同步展示；每批击杀以“小怪击杀”写入原车头历史，共用最近50条上限。观察到NM筹备/运行/结束状态时归档本轮数量并清零，活动期间不累计，事件消失后开始下一轮。切区/角色重置，不因换车头清空岛内计数。天气与冷却不作为本地死亡统计的有效触发保证。

字段依据：Dalamud `ICharacter.NameId`、`Level` 与 `IBattleChara.CurrentHp/IsDead`，本机API15实际构建；名称取本机游戏 `2026.10.06.0000.0000` BNpcName，触发说明来源和许可证沿用 `docs/CROWDSOURCE.md`。

持续跟随在开始击杀后/战后发现车头最新坐标50米内没有新运行中NM时，转入水晶待机流程。战斗中仅缓存需求，保持当前头目，结束脱战后执行。已完成NM以ID与开始时间保存在本次会话内，相同或更早时间不算新NM，同ID较晚开始的新场次允许继续。

待机水晶按玩家当前位置的世界水平距离选择本岛最近者，先确认上坐骑，再通过地面/完整路径验证走到水晶旁7米处。不打开菜单、不传送、不自动解锁。路径不可达、外部导航占用、状态异常或5分钟超时会停止，不强行直线移动。已到位则保持空闲，不开启战斗插件。

待机中保留车头最新坐标的监视点，每个框架更新周期检查运行中的NM；该点出现新NM或车头发来有新NM的坐标时可继续原流程。新的车头坐标优先于旧监视点。单次导航/战斗不进入持续水晶待机。

单次“开始导航到目的地”现在也走相同坐骑/战斗流程，打完上坐骑即结束；持续跟随只由新增按钮开启。停止导航会停止当前流程，停止跟随还会停止监听；关闭窗口不等于停止。更换车头、死亡、登出、真实换岛/角色或异常都会停止，不自动恢复。

相同目的地1米内重复坐标忽略；距玩家25米内的坐标仍按已有监听规则跳过。筹备阶段若尚未进入战斗，可以响应更新的坐标。导航中进入目标NM中心60米内的战斗时转入准备；其他途中战斗停止本次导航。

## 模块

- `MountGameAdapter`：本机Mount表+PlayerState.IsMountUnlocked列出角色坐骑，ActionType.Mount按实际Mount RowId召唤。每30秒且角色状态稳定时刷新，选择仅本次会话保存。不能移动的坐骑仍列出但禁用选择；选定坐骑不可用时不悄悄换随机坐骑执行。
- `FellingFollowHelper`：Waiting / Travel / Prepare / Fight / Remount状态机；复用IslandTravelHelper与独立的接近路径。所有状态只由框架线程推进，不阻塞等待。
- `CombatAutomationAdapter`：战斗依赖、循环/AI启停、配置恢复、头目目标和原生等级同步。
- `AutomationHelpers`：命令/聊天意图入队、角色会话与名单、托管快照；UI不执行游戏动作。

坐骑、下坐骑、同步最多各3次、间隔3秒。行程总5分钟、准备5分钟、单场战斗20分钟、战后脱战/上坐骑40秒、跟随会话2小时。超限停止并解释原因。

## 本机接口与所有权

已核对本机国服API15、Rotation Solver Reborn **7.5.7.13** 和 BossMod Reborn **7.5.7.3**；也按BossMod当前官方接口实现原版适配，但本机没有原版安装可做同版本反射核验。

RSR：`RotationSolverReborn.AutorotationActive` 读取实际运行状态，`ChangeOperatingMode` 以提供者真实 `RotationSolver.Basic.Data.StateCommandType` 枚举调用Manual/Off，无硬编码枚举数字、无编译引用插件DLL。通过当前已注册 `/rotation` 的处理程序集确定类型，支持卸载/重载，不缓存旧程序集类型。

`AutorotationActive` 在玩家暂时不可用时会返回false，不能据此认定开关已关。因此同时只读核验本机已确认的DataCenter.State/IsManual开关；停止时显式请求Off并检查开关。结构或接口不兼容时拒绝开始，不猜状态。

BossMod：原版通过 `BossMod.AI.IsEnabled` 读状态，使用 `/vbm ai on/off` 和跟随自身slot1；Reborn通过 `/bmrai on/off`，从该现存命令的绑定AIManager对象只读Beh字段确认状态，未调用私有控制方法。本机已反射核对Beh和命令处理形态；兼容失败拒绝开始。

通过 `BossMod.Configuration` 查询/设置非保存配置，临时打开移动、禁止AI自己执行技能/自动选怪。原版打开GoalZoneFallback；Reborn使用自身FollowSlot、ManualTarget和FollowTarget，并将AI循环预设临时留空。技能仅由RSR提供，AI处理移动。结束后仅在值仍与本插件所写一致时恢复原值/AI预设，不覆盖运行期间用户的新修改。

按用户最新授权，显式开始时先关闭既有RSR循环、BossMod AI和活动循环预设，确认关闭后继续，不因原本已开启报错；关闭失败或接口缺失仍报告实际原因。原本的运行开关不会在结束后重新开启。开始之后仍只关闭本插件成功开启的控制；提供者的程序集/命令绑定对象变化、RSR从本插件Manual模式变为其他模式、Reborn AI行为对象被替换时释放相应旧所有权，不关闭替换实例。会话中用户另外开启的外部控制不被再次接管。关闭失败时保留清理状态、每秒重试并阻止重新开始。不会用全局Abort或ClearTransient清除其他插件的任务/配置。

等级同步仅在当前FateManager.GetCurrentFateId等于绑定FATE、尚未同步该FATE时调用LevelSync；待原生SyncedFateId匹配才允许开循环。同步丢失或重新上坐骑时立即关控制、回到准备阶段。无法确认头目时不拿小怪替代；短暂失去事件可见性时先关闭控制，5秒持续消失且已脱战则退出该事件并上坐骑，仍在战斗中15秒不可见则停止，不伪造击杀。

岛内水晶的预期黑屏允许短暂的区域/地图/角色字段为0，仍要求已登录、原公开分线一致；真实换岛、换角色、登出或非本任务传送不保留旧会话。

## 验证

- `dotnet run --project tools/AutomationTests`：**146项**通过，新增开始时关闭已有控制后继续、击杀历史记录。`tools/FateTests` **97项**通过，新增65条规则本地数据解析、死亡去重/首次尸体/消失/等级/间断/复活/活动重置/切区等校验。
- Debug/Release构建均通过，0警告0错误。
- 最新UI默认与1000×520离线渲染，三项触发列表、左右按钮和小岛民名单已目视检查；当前目标设车头、跟随按钮使用实际ImGui鼠标输入与模拟提供者核验，历史展开显示小怪和车头记录。截图在 `artifacts/ui/automation-kill*.png`、`automation-target-leader.png`、`automation-history-open.png`；不是游戏截图。
- `tools/InventoryInspect --combat-api` 反射核对本机Mount/等级同步/目标接口、IPC泛型构造及InvokeAction、RSR状态枚举和开关、Reborn AI状态字段。未调用真实插件控制或发起骑乘、同步、战斗、移动。
- 实际角色坐骑列表、原生同步/目标、RSR/BossMod AI启停、职业循环与NM战斗尚未游戏内实测；本轮不修改版本、不重新打包原0.0.1发布ZIP。

主要源码依据：[RSR IPC](https://github.com/FFXIV-CombatReborn/RotationSolverReborn/blob/main/RotationSolver/IPC/IPCProvider.cs)、[RSR DataCenter](https://github.com/FFXIV-CombatReborn/RotationSolverReborn/blob/main/RotationSolver/Basic/DataCenter.cs)、[BossMod IPC](https://github.com/awgil/ffxiv_bossmod/blob/master/BossMod/Services/IpcService.cs)、[Reborn IPC](https://github.com/FFXIV-CombatReborn/BossmodReborn/blob/main/BossMod/Framework/IPCProvider.cs)、[Reborn AIManager](https://github.com/FFXIV-CombatReborn/BossmodReborn/blob/main/BossMod/AI/AIManager.cs)。
