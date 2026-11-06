# 文理与触发设置

2026-11-03。用户授权新增文理页面、Universalis报价、自动触发魔素与RSR阶段设置。

六页顺序：物品统计、天气预报、地图/FATE、文理技能、自动化、调试测试，等宽。原保存的自动化/调试索引首次迁移到新位置。

## 文理

Logos/catalog.json：56技能/115配方/9碎晶，来源、日期及AGPL数据许可见ThirdPartyNotices/README.md和XivEureka-data-LICENSE.txt。全部技能与原料ID已与本机中文数据核对。显示全部配方、鉴定结果；收藏★/☆保存在settings.json的logos.favorites，收藏优先、组内按原编号。

Universalis依据当前服务器World ID调用 `GET /api/v2/aggregated/{world}/{9个碎晶ID}`，单次获得nq.minListing.world/dc/region。官方接口模型：https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Views/V2/AggregatedMarketBoardData.cs 。官方控制器支持China/中国范围，并优先建议聚合接口：https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/AggregatedMarketBoardDataController.cs 。每次手动刷新一次，30秒节流、20秒超时、无周期刷新/自动重试/上传；切服不写入旧结果，卸载取消。缺失及零报价为未知。联网真实服务当前环境无法连通，离线验证官方结构，仍需玩家主城测试。

成本是按每份记忆一颗来源碎晶价格累加的采购基准，重复原料累加；不假定鉴定概率，未计随机鉴定损耗，不是保底或数学期望。真实概率未核实，页面明确说明这一限制。

## 自动触发设置

按正在拉怪/击杀的实际小怪等级与AgentHUD元素等级：低级所有职业攻，同级到高2级坦克（ClassJob.Role=1）攻、其他防，高3级以上全防。原生/magiaattack、/magiadefense本机TextCommand行10/11已核对，通过框架线程ProcessChatBoxEntry执行。EurekaState只读六属性分配；按当前投入最高属性旋转，不要求达到5颗。本次没有已核实的野外重新分配接口，不能自动把散开的魔石分配为5，避免写猜测回调。命令发送不代表服务端旋转成功，需游戏内验证；充能不足可能不能还原。正常结束尝试旋转回原六属性分布，最多6次、每3秒一次；离岛/死亡丢弃恢复任务，不在其他岛继续。

RSR本机7.5.7.15及官方源码交叉核对：Service.Config.HostileType（AllTargetsCanAttack / TargetsHaveTarget）、DataCenter.TargetingTypeOverride=HighHP、ChangeOperatingMode Manual / Auto。拉怪Manual/全部，原地击杀Auto/最高血量/已交战；自动阶段不每帧把RSR选出的高血量目标重新改成最近目标。快照保留原目标范围、排序覆盖、开关模式；还原时检查提供者身份及最后写入值，插件替换或用户覆盖不写回旧实例。原运行模式按Off/Manual/Auto恢复，复杂AutoDuty等子模式尚未专项验证。trigger-waves.13起普通模式用BossMod Reborn AI自动靠近，记录路线模式仍保留原地击杀。见docs/AUTO_TRIGGER.md。

官方参考：https://github.com/FFXIV-CombatReborn/RotationSolverReborn/blob/main/RotationSolver.Basic/Service.cs 、同目录DataCenter.cs。反射工具需本机依赖统一加载以避免Lumina版本差异导致内部Service类型被反射过滤。

## 地面反馈修复

本次收到“无法在目的地找到可行走路面”。不能仅据此确认具体地形。现已扩宽未知高度floor查询水平范围到5米；已知高度先查附近网格，失败时尝试目标Y+3向下找地（最多8米高度差），自动触发另外在2/4米环上有限采样，候选始终在原目标5米内。普通巡查单点无网格跳过，连续8点失败停止；特殊记录路线仍失败即停，不擅自改变路线。错误报告带目标/玩家XYZ与查询模式，仍要求完整路径结果，不直接直线移动。vnavmesh官方IPC/算法来源：https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/IPCProvider.cs 、NavmeshQuery.cs。

## 验证

207项自动化规则/模拟检查（新增地面回退、无网格拒绝与魔素等级职业分支），10项文理本机数据/API解析/估价检查。真实魔素旋转及还原、RSR运行、当前服务器API报价、失败地形复测均未游戏内验证。主城可测文理、收藏和价格；低级NM每批1只可单人测刷怪与设置，详见TEST_TODO.md。开发版本未重新打包/提交/推送。

## 文理两栏与数量（logos-ui.5）

2026-11-03用户要求两栏。碎晶页左侧碎晶/三范围价格，右侧所选鉴定结果；技能页左侧技能/收藏，右侧所选技能全部组合，按220像素最小列宽响应为1–3列。左右栏可拖动并独立滚动。

LogosInventoryReader在框架线程每秒只读四背包，碎晶名称后显示当前背包数量，不包含雇员等离线来源。记忆数量仅在EurekaMagiciteItemShardList可见时读取本机NumberArrayType.EurekaLogosShardList，读取布局参考已公开现场记录：https://github.com/Pop115/XivEureka/blob/main/XivEureka/Logos/ShardReader.cs 。复制到托管快照后显示，关闭窗口后显示本地缓存。已读取的数量保存在插件配置目录的 logos-stock.json，按角色 ContentId 隔离，重新登录同一角色时恢复。来源未加载或校验失败时保留缓存，成功读取到零时覆盖旧值。插件不主动打开窗口或发送物品请求。解码验证容量、ID白名单、重复ID、数量范围，失败不覆盖缓存。合成器可见时读取已知15–20槽加入放置中未消耗的记忆，避免同一次新鲜快照漏计。该布局尚需国服实际界面验证。

拥有对应基础记忆的技能（例如斗士的记忆）显示记忆数；已合成的存储技能数量尚无本机已确认接口，不能把笔记中的解锁状态当作拥有数量。未读取为“未获取”，已加载无库存才显示0。第六页玩家报告不上传任何数量数据。

文理石肤12991的4配方完整：24035；24022+24023；24022×3；24023×3。24035与24023来自碎晶24013，24022来自24008。原成本遇到缺报价只显示未知，用户无法知道原因；现在每条组合列出缺报价碎晶，并在有完整报价的替代组合中显示最低采购基准，不跨价格范围混用或用0补空。19项文理检查覆盖四配方完整/完整报价/缺价提示/替代组合及记忆列表有效和坏数据。
