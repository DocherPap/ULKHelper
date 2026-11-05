# 真实库存统计

更新：2026-11-03。用户已授权读取库存与交互时更新；游戏内示例模式已移除，离线 UI 工具仍可用模拟数据。

## 使用与来源

重载插件，输入 `/ulk` → 物品统计。登录后读取已加载的背包、穿戴、兵装库；打开其他来源及各个雇员后逐个补齐缓存。

| 来源 | 获取条件与范围 |
| --- | --- |
| 背包 | 四个普通背包页全部加载后整体替换，按物品堆叠数量累加 |
| 穿戴装备 | 玩家穿戴容器，独立记录 |
| 兵装库 | 主手、副手、头、身体、手、腿、脚；仅记录本插件关心的装备 |
| 陆行鸟背包 | 普通两页全部加载 |
| 高级陆行鸟背包 | 高级两页全部加载；没有该功能时可保持未获取 |
| 收藏柜 | `UIState.Cabinet.IsCabinetLoaded()` 为真，使用 Cabinet 表 RowId 查询对应物品 |
| 幻化柜 | `MirageManager.PrismBoxLoaded` 为真，扫描箱子；套装按 `MirageStoreSetItem` 展开且用 `IsSetSlotUnlocked` 检查实际存放部件，不把缺失部件当作拥有 |
| 雇员背包 | 按雇员 ID 隔离，7 页全部加载后替换该雇员的背包记录 |
| 雇员穿戴 | 独立记录当前雇员的穿戴容器 |
| 雇员货架 | 当前雇员的出售列表打开、货架容器加载后记录；关闭后只能使用上次获取时的缓存 |

数量归一到基础 Item RowId（包含 HQ/NQ 合并），仅保存目录中的 665 个相关物品，不读取他人库存，不保存无关物品。不是读取物品的投影外观 ID；装备实际 ItemId 才算持有。武器复制品不算原制作阶段。

## 更新和隔离

- `IFramework.Update` 中每 750 毫秒检查客户端已经加载的数据，UI 关闭时也更新；库存变动事件以及背包、鸟包、兵装库、收藏柜、幻化柜、雇员相关窗口的打开/关闭事件将检查提前到约 250 毫秒后。
- 不阻塞游戏线程等待数据，不主动请求服务器、不打开窗口、不移动或兑换物品。由于输入与服务器回应异步，打开来源后可能需要稍等数据加载。
- 原生指针只在当次回调中使用，不跨帧保存。扫描时缺页、未加载或无效来源不发布新快照，也不清空之前的有效缓存。
- 雇员需同时匹配 `RetainerManager.LastSelectedRetainerId` 与 `ItemOrderModule.ActiveRetainerId`。切换后等待 1.5 秒，再检查加载状态，避免把旧雇员的容器归到新雇员。已就绪的名册用于移除被解雇雇员的缓存。
- 数据变化或本会话首次读取时更新快照；内容未变的周期读取不持续失效 UI、不反复写盘。角色切换/登出先保存原角色，再清空当前统计；绝不把上一个角色的库存累加到新角色。

## 界面与计材

- 左侧每个来源分别显示：本会话已读为绿色“已更新”；只有历史记录为灰色“已缓存”；从未获取为灰色“未获取”。悬停状态查看最后获取时间。
- 雇员在左侧按雇员 ID 合并成一条“雇员 名称”，不分别列背包/穿戴/货架；任一部分本会话已读即显示“已更新”，仅历史记录显示“已缓存”，全部未读显示“未获取”。悬停显示最近获取时间；内部仍独立维护各部分库存。
- 库存尚未获取时不把未知数量显示成零；任一来源获取后显示已知库存合计，并提示其他来源可能未获取。历史缓存仍计入，但不声称为实时库存；货架物品可能已售出，需再次打开雇员出售列表刷新。
- 同一装备不同阶段实际都存在时保留多个亮绿色点。配方前置链用于推算之前完成的阶段（浅绿色），不凭材料数量猜测装备已制作。
- 同一阶段在多个来源中出现不重复增加制作预算；同一共享防具在多个职业下重复展示时也只计一次。
- 材料库存按来源数量求和，和推算投入分开：目前进度为 `持有 H / 后续需求 (T-S)`，总体进度为 `(H+S)/全部需求 T`。
- 已投入是依据当前实际装备推算的最低已完成消耗，不是角色终身制作流水；已经丢弃或卖掉、在未获取来源中的装备无法据此确认制作进度。
- 骑士剑盾作为一条路线，读取二者的真实 ID，通过成对产出的配方去重，避免把一把剑和一面盾重复算成两套消耗。

## 本地数据核实

`GameCatalogLoader` 用游戏自带 Lumina 表解析真实物品：按精确名称匹配防具/材料，按禁地兵装描述、武器类型和品级唯一匹配武器阶段；405 阶段区分 `·优雷卡` 与 `·改`。不会把中文名称当作库存主键，也不会凭连续 ID 猜测物品。

防具、兑换成品、武器最初三步的配方来自本机 `SpecialShop`；其余武器强化属于自定义兑换，材料采用前期已交叉核实的 Wiki 阶段表。每条路线均对比本机解析结果与既有目录的全部材料总量，不一致时报告错误并保留已有快照。

已在本机国服 `2026.10.06.0000.0000` 游戏数据上验证：132 条装备路线、652 个装备阶段物品 ID（含骑士盾牌）、13 种材料，共 665 个相关 ID。物品名称解析针对国服中文数据验证，未声称适配其他语言或未来补丁。

## 缓存

同一角色访问其他服务器时继续按角色 ContentId 计入装备和材料缓存。过图或角色数据暂时未加载时保留当前角色快照，已读来源标为“已缓存”。数据恢复后按来源更新。雇员实时读取和名册清理仅在原服务器执行。

路径：卫月提供的本插件配置目录下 `inventory/<角色 ContentId 的十六进制>.json`。只包含该角色相关的来源、雇员标识/显示名、最后获取时间和相关 ItemId/数量。

变化后最多每 5 秒保存一次，并在切换角色、登出、卸载时保存；使用临时文件后原子替换。缓存格式和角色标识不匹配或文件损坏时不覆盖原文件，界面提示，当前会话仍可读取。所有数据仅本地保存，没有上传或外部 IPC。

## 验证

```powershell
dotnet build -c Debug --nologo
dotnet build -c Release --nologo
dotnet run --project tools/InventoryTests -- "D:\Program Files (x86)\FFXIV\game\sqpack"
dotnet run --project tools/UiPreview -- inventory-live-ui.png --live --job
```

24 项校验通过：本地物品/配方覆盖、排除复制品、未知/已读空来源、数量累加、输入快照复制、无变化不失效、多个装备阶段、前置链、跨岛成本、共享防具/剑盾去重、缓存保存恢复、角色切换/错角色拒绝、雇员移除与损坏文件保留。生产数据接口 UI 另用测试提供者离线绘制，未使用真实角色数据。

未进行游戏内交互验证（本次没有运行中的游戏进程）。后续重点在实际打开各来源、切换两个雇员、穿戴/移动装备、存取鸟包、收藏柜和幻化柜（含部分套装）后核对数量；离线校验不能替代这些验证。

## 接口依据

- 本机 API 15 的 `Dalamud.xml`、`FFXIVClientStructs.xml` 与程序集反射校验。
- [IGameInventory](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/Services/IGameInventory.cs)：库存变动与公开服务接口。
- [InventoryType](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InventoryType.cs)、[InventoryManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InventoryManager.cs)：容器与读取接口。
- [Cabinet](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/UI/Cabinet.cs)、[MirageManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/MirageManager.cs)、[RetainerManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/RetainerManager.cs)：已加载标记与状态生命周期。
- [官方 /itemsearch 范围](https://na.finalfantasyxiv.com/lodestone/playguide/db/text_command/107573661a8/)：拥有情况范围的参考；本插件不执行这个命令。
- [嘉拉汀·常风的剑盾成对兑换](https://ff14.huijiwiki.com/wiki/物品:嘉拉汀·常风)：骑士成对产出和前置材料的交叉核对。
