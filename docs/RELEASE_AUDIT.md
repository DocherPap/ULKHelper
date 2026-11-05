# 0.3.0 功能同步核对

本轮从 codex/testing 的工作区迁入 main，覆盖已提交分支差异和未提交的正式功能。文案以迁入前 main 的用户版本为合并依据，新增功能保留对应实现中的说明。

| 正式功能 | main 中的位置 |
| --- | --- |
| 吃 FATE、单轮自动寻找宝藏 | Windows/CommonPage.cs、Automation/CommonActions.cs |
| 兔子 FATE 未刷新时前往待机、最少剩余时间、骑乘寻宝 | Windows/BunnyPage.cs、Automation/BunnyAutomation.cs |
| 兔箱参考点范围筛选、挖箱后新增或校准位置 | Bunnies/BunnySearch.cs、Bunnies/BunnyTracker.cs |
| 兔子计时、结算冷却、排除幸福兔出现通知 | Fates/FateMapTracker.cs、FateLedger.cs、FateTiming.cs、FateNotificationHelper.cs |
| 洞内使用活动 FATE 和头目高度、附近落点与完整路径检查 | Automation/DestinationNavigationHelper.cs、IslandTravelHelper.cs |
| 实际导航路长比较水晶收益、水晶前遇战处理 | Automation/IslandCrystalRoutes.cs、IslandTravelHelper.cs、CombatAutomationAdapter.cs |
| 开怪条件使用附近参与者三分之二、实际交战后魔素攻势 | Automation/ArrivalRules.cs、FellingFollowHelper.cs、TriggerGameReader.cs |
| 原版 BossMod 与 Reborn 识别、任务提供者绑定 | Automation/CombatAutomationAdapter.cs |
| Discord 私聊、跨服进岛、指定 FATE、结果报告和凭据保存 | Automation/DiscordAutomation.cs、DiscordCommandRules.cs、DiscordCredentials.cs、RemoteCommandPipe.cs |
| Discord 正式界面与部署文件 | 常用功能的 Discord 远程进岛区、DiscordBridge/ |

测试页、单人现场测试、现场记录、模拟库存、离线检查和 UI 预览保留在 testing，不编译到正式 DLL。

验证使用 testing 中的离线用例副本，依赖路径指向 main 的生产源码。282 项自动化与 174 项地图/兔子检查通过。测试副本位于忽略的临时审查目录，不进入正式提交或安装包。Discord 通信、原生游戏操作和双 BossMod 切换需游戏内复测。
