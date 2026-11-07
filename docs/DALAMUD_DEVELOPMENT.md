# Dalamud 插件开发流程速记

游戏正在从主工作区 bin/Debug 加载测试 DLL 时，main Debug 验证必须隔离输出：dotnet build -c Debug -p:OutputPath=artifacts/main-debug-validation/。不要用默认输出覆盖正在运行的测试 DLL，以免热重载中断任务和调试记录。

查阅日期：2026-11-03。以下是开发要点，本项目当前运行基准为本机国服 API 15。

## 从骨架到运行

1. 建立 C# 类库、插件清单和唯一 `IDalamudPlugin` 入口；DLL、清单文件名和 InternalName 保持一致。InternalName 影响配置目录，确定后应保持稳定。
2. 注入所需 Dalamud 服务。初始化注册命令、窗口与必要事件；`Dispose` 反注册并释放自己创建的资源。
3. UI 使用 `WindowSystem` / `Window` 与 `Dalamud.Bindings.ImGui`。UI 绘制只负责显示和提交用户意图，业务流程独立。
4. 本机编译后在 Dev Plugin Locations 加入插件 DLL，启用并用 `/ulk` 验证加载。通过 `/xllog` 看异常；重新构建后验证热重载及卸载无残留。
5. 数据查询、规则计算、UI 展示、自动化动作逐层实现。可独立计算的累计材料/停止规则可做单元测试；原生 UI、寻路和游戏操作需要游戏内验证。
6. 完成功能后检查清单版本与 DLL 版本，打包插件自身文件。不要分发 Dalamud 自带依赖。发布方式确定后再配置仓库元数据。

官方当前 SamplePlugin 使用 `Dalamud.NET.Sdk/15.0.0`，通常自动生成关键清单字段和包。本项目为了匹配同级 MakoMaster 及本地离线构建，暂用 `Microsoft.NET.Sdk`，手工维护清单；此构建不自动产出发布 ZIP。

参考：[入门](https://dalamud.dev/plugin-development/getting-started/)、[项目结构与入口](https://dalamud.dev/plugin-development/project-layout/)、[清单字段](https://dalamud.dev/plugin-development/plugin-metadata/)、[官方模板项目](https://github.com/goatcorp/SamplePlugin/blob/master/SamplePlugin/SamplePlugin.csproj)、[开发插件加载说明](https://dalamud.dev/faq/getting-started/)。

## 与游戏交互

- `IDataManager` / Lumina：本地物品及其他表数据。优先本地数据，缓存稳定查询结果；不每帧扫描整张表。
- `IClientState`、`ICondition`、`IObjectTable` 等：按实际 API 版本观察区域、角色、条件与对象；实现时核对签名。
- `IFramework.Update`：推进有节流的状态机；不使用 `Thread.Sleep` 等阻塞游戏线程，不从任意后台线程调用游戏 API。
- IPC：可选插件能力通过独立适配层接入，检查存在性、加载状态、端点及异常。缺失依赖时解释哪些操作不可用。
- FFXIVClientStructs / 原生 UI：只有公开服务无法满足时再引入。结构随版本变化，使用卫月配套程序集，限定指针生命周期，调用前检查状态。

官方建议普通窗口使用 Windowing API，游戏表数据优先使用 Lumina，详见 [技术建议](https://dalamud.dev/plugin-development/technical-considerations/)。

## 验证重点

- 构建：Debug、Release，无缺失引用；依赖目录错误时给出明确提示。
- 生命周期：命令只注册一次，重复加载/卸载后没有遗留事件或任务。
- 装备数据：RowId 对应正确物品，各阶段与累计材料有来源，未登录时查询也能合理运行。
- 自动化：开始、停止、完成、超时、切区、死亡、登出、依赖失效及动作结果不匹配均有定义。
- 报告区分“编译成功”“游戏内已加载”“业务已验证”，不把前者描述为后者。
