# Discord 远程进岛

在 `main` 正式构建启用。Discord 机器人通过 Gateway 接收指定账号的私聊，桥接脚本与游戏在同一 Windows 用户下运行，通过仅当前用户可访问的命名管道通信。插件加载时默认关闭，在「常用功能 → Discord 远程进岛」点击「开启 Discord 接入」，也可输入 `/ulk discord on`。 `/ulk discord off` 会关闭接入并停止正在运行的 Discord 任务。

## 从常用功能连接

1. 在 Discord Developer Portal 创建应用，从 Bot 页面获取 Token。在 Discord 客户端开启开发者模式，复制自己的用户 ID。让自己的账号和 Bot 共处一个允许私聊的 Discord 服务器。
2. 加载带桥接脚本的 插件 DLL。打开「常用功能 → Discord 远程进岛」，填入用户 ID 和 Bot Token，点「连接 Discord Bot」。输入框提交后清空。桥接启动时，用户 ID 和 Token 一起用当前 Windows 用户的 DPAPI 加密，保存到插件配置目录的 discord-credentials.dat。下次两个输入框留空即可连接；「清除已保存凭据」删除本机文件，已连接的桥接仍可通过「关闭 Discord 接入」停止。
3. 看到 Bot ID 后，私聊该机器人发送「状态」。关闭接入或卸载插件会关闭常用功能启动的桥接进程。若已用命令行启动了同一个 Bot，请先结束那个桥接进程。

构建输出会随 DLL 生成 bridge.py。默认使用 py 与其已安装的 discord.py。可在 DLL 旁放置本机专用的 discord-python.txt，第一行指定 Python 可执行文件路径，第二行可指定依赖目录。该文件只存运行环境路径。

## 使用独立命令行桥接

目前没有预设机器人。Bot ID 来自登录成功的桥接程序，每 15 秒上报，超过 45 秒未更新时常用功能显示心跳超时。

1. 在 Discord Developer Portal 创建 Bot。让发送指令的账号与机器人共处一个允许私聊的服务器，打开和机器人的私聊。此方案使用 DM 的消息内容例外，只订阅私聊消息，无需启用特权 Message Content Intent。
2. 在 Discord 开发者模式复制自己的用户 ID。游戏电脑安装 Python 3.10 或更高版本，使用运行游戏的 Windows 用户打开 PowerShell，并执行以下命令。Token 只在该进程环境中传入。

```powershell
py -m pip install "discord.py>=2,<3"
$env:ULK_DISCORD_OWNER_ID = Read-Host "Discord 用户 ID"
$env:ULK_DISCORD_TOKEN = Read-Host "Discord Bot Token" -MaskInput
py DiscordBridge/bridge.py
Remove-Item Env:ULK_DISCORD_TOKEN
```

3. 加载 插件 DLL，游戏内登录、选好坐骑，确认本地寻路和战斗依赖可用。等常用功能显示「已收到桥接心跳」和 Bot ID，再发送 `状态` 验证真实私聊收发，再发 `进岛 你的服务器 恒冰`。四岛可用简称或完整岛名。机器人回复的是接单结果，之后可以再次发送 `状态` 查询进度。
4. 发 `停止`、`取消`、`停`、`stop` 或 `cancel`，或在游戏内使用 `/ulk stop`，停止插件当前的自动化任务。已经提交给游戏的报名或传送可能继续完成。

## 天气查询

私聊发送「天气」查询四岛未来十二小时的特殊触发天气。使用「天气风」「天气冰」「天气火」「天气水」可查看单岛完整天气与特殊时段。回复标注现实时间与时区，较长内容自动分段。

## 指令与任务流程

- 忽略标点、空格和数字，支持全角转半角与英文大小写。例「沃仙＋恒冰之地＋ＫＸ」。「进岛」前缀可省略。「状态」查询。四岛全称与简称、游戏表 FATE 名称、常用中文名和拼音均可使用。重名或缩写冲突需要补充岛名。
- 国服世界来自本机 World 表，29 个已核对世界的 IsPublic 均为 false。陆行鸟可写「鸟」，沃仙曦染可写「沃仙」，莫古力可写「猪」。只发大区时，优先选择角色在该区的原始服务器，其次选择该区的当前服务器，否则取表中该区首个已核对服务器。跨服通过本机国服 Lifestream 2.6.0.5 已核对的 ChangeWorldById、Instance、IsBusy 与 Abort IPC，抵达后校验实际世界。
- 只发 FATE、岛或两者时会询问服务器，记住待补充目标 15 分钟。只发服务器时先去罗德尼，等待岛或 FATE 确定报名目标。服务器加岛时直接报名，并询问要打哪个 FATE。在进岛或监听期间可补充与当前任务一致的目标。
- 进岛后最多监听 15 分钟。已指定目标时，进岛后直接前往。目标已出现时使用现场坐标，否则前往已核对的地图点位，在约 15 米处等刷新；途中刷新时切换至实际场次。没有指定目标时，只找特殊名单里的已出现活动，多个候选按平面距离选最近。只参加一场。锁定后复用跟随的导航、靠近头目、开怪、同步、战斗与清理。整单上限 55 分钟，跨服上限 10 分钟。
- 特殊名单按本机国服 Fate 表核对。常风 pzz（1329）；恒冰 kx/lx/px（1365/1364/1360）；涌火 pnz/狗子/yy/gbl（1404/1403/1402/1398）；丰水 ml/mw/水晶龙/lk/kt/bl/ufo（1414/1418/1423/1419/1421/1420/1424）。
- 常用功能两个组队选项默认关闭。启用喊话时，锁定目标且未组队会喊一次「111」。启用自动组队时，在本单岛内复用现有邀请和接受规则。
- 战斗流程结束后留出 8 秒收集结算，报告本机观察到的场次完成/失败及结算窗口内符合国服 LogMessage「获得了」模板、带物品链接的个人获取消息。消息与 FATE 的因果关系无法独立确认。缺失结果或奖励按未捕获报告。
- 监听超时或结算后，用本机 API 15 的 CanLeaveCurrentContent 校验退岛条件，再调用 ContentsFinderMenu 单次离开事件。事件参数经本机 SimpleTweaksPlugin 1.15.0.8 对照，游戏实际退出待验。最多等 30 秒确认离岛，未确认时请手动退岛。结果由 Bot 主动私聊白名单账号。
- 任务可通过 Discord 停止指令或游戏内停止操作取消。

[Discord Gateway 与消息权限](https://docs.discord.com/developers/events/gateway#message-content-intent) · [Lifestream IPC](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/IPC/IPCProvider.cs) · [Simple Tweaks 命令记录](https://github.com/Caraxi/SimpleTweaksPlugin/blob/main/changelog.md)
