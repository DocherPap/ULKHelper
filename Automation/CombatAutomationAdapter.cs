using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace ULKHelper.Automation;

// RSR handles actions; BossMod AI handles movement with its own actions/auto-target disabled.
internal sealed class CombatAutomationAdapter(IDalamudPluginInterface plugin, ICommandManager commands,
    IObjectTable objects, ITargetManager targets) : IFellingCombat, ITriggerCombat
{
    private bool reborn, rotationOwned, aiOwned;
    private bool triggerAuto;
    private bool triggerSession, triggerUseRotation, triggerUseAi;
    private Assembly? triggerOwner;
    private object? overridesBefore, overridesWritten, targetingBefore, targetingWritten;
    private bool triggerSettingsWritten;
    private string? triggerModeBefore;
    private object? triggerBossOwner;
    private string? triggerPresetBefore;
    private bool triggerAiSuspended, triggerRotationSuspended, triggerEnding;
    private int triggerRestoreAttempts;
    public string? BeginTrigger(bool needsApproach, bool useRotation, bool useBmr)
    {
        try
        {
            Error = null;
            if (CleanupPending) return "等待上次战斗控制还原。";
            if (useBmr)
            {
                if (!commands.Commands.ContainsKey("/bmrai")) return "已开启 BMR AI，请先加载 BossMod Reborn。";
                if (needsApproach)
                    foreach (var (field, _) in TriggerApproachSettings) _ = Read(field);
            }
            triggerSession = true;
            triggerUseRotation = useRotation;
            triggerUseAi = useBmr;
            overridesWritten = targetingWritten = null;
            triggerSettingsWritten = false;
            triggerRestoreAttempts = 0;
            if (triggerUseRotation)
            {
                triggerOwner = CurrentRotationOwner();
                if (triggerOwner == null) return "已开启自动循环，请先加载 RSR。";
                overridesBefore = RsrStateProperty("IpcOverrides").GetValue(null);
                targetingBefore = RsrStateProperty("TargetingTypeOverride").GetValue(null);
                triggerModeBefore = RotationRequested() ? (ManualRequested() ? "Manual" : "Auto") : "Off";
            }
            return null;
        }
        catch(Exception ex){return "触发设置准备失败："+ex.GetBaseException().Message;}
    }
    private PropertyInfo RsrStateProperty(string name) =>
        RotationType("RotationSolver.Basic.DataCenter").GetProperty(name)
        ?? throw new InvalidOperationException("RSR状态接口不可用：" + name);

    private bool TriggerSettingsHealthy() => triggerModeBefore == null
        || (Equals(RsrStateProperty("IpcOverrides").GetValue(null), triggerSettingsWritten ? overridesWritten : overridesBefore)
            && Equals(RsrStateProperty("TargetingTypeOverride").GetValue(null), triggerSettingsWritten ? targetingWritten : targetingBefore));

    private void CaptureTriggerOverrides()
    {
        overridesWritten = RsrStateProperty("IpcOverrides").GetValue(null);
        targetingWritten = RsrStateProperty("TargetingTypeOverride").GetValue(null);
        triggerSettingsWritten = true;
    }

    private void ApplyTriggerMode(bool fighting)
    {
        var overrides = RsrStateProperty("IpcOverrides");
        var hostile = overrides.PropertyType.GetProperty("HostileType")!;
        var target = RsrStateProperty("TargetingTypeOverride");
        var rangeType = Nullable.GetUnderlyingType(hostile.PropertyType) ?? hostile.PropertyType;
        var range = Enum.Parse(rangeType, fighting ? "TargetsHaveTarget" : "AllTargetsCanAttack");
        var constructor = overrides.PropertyType.GetConstructor([hostile.PropertyType, typeof(bool?), typeof(bool?), typeof(bool?)])
            ?? throw new InvalidOperationException("当前RSR不支持临时目标范围。");
        var temporary = constructor.Invoke([range, fighting ? null : false, false, null]);
        RotationMode(fighting ? "Auto" : "Manual");
        CaptureTriggerOverrides();
        overrides.SetValue(null, temporary);
        overridesWritten = temporary;
        targetingWritten = fighting ? Enum.Parse(Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType, "HighHP") : targetingBefore;
        target.SetValue(null, targetingWritten);
    }

    public bool PrepareTarget(ulong id, bool fighting, IReadOnlyList<ulong>? eligible = null)
    {
        if (triggerUseRotation && (!ReferenceEquals(triggerOwner, CurrentRotationOwner()) || !TriggerSettingsHealthy())) return false;
        if ((!fighting || !rotationOwned || !triggerAuto || eligible?.Contains(targets.Target?.GameObjectId ?? 0) == false)
            && !Target(id)) return false;
        if (rotationOwned && triggerAuto != fighting) ApplyTriggerMode(fighting);
        triggerAuto = fighting;
        return true;
    }
    public void EndTrigger()
    {
        if (!triggerSession) return;
        triggerEnding = true;
        Disable();
    }
    private void RestoreTrigger()
    {
        var retry = false;
        try
        {
            if (triggerOwner != null && ReferenceEquals(triggerOwner, CurrentRotationOwner()))
            {
                var restore = triggerSettingsWritten && TriggerSettingsHealthy();
                if (triggerRotationSuspended && !RotationRequested()) RotationMode(triggerModeBefore!);
                if (restore)
                {
                    RsrStateProperty("IpcOverrides").SetValue(null, overridesBefore);
                    RsrStateProperty("TargetingTypeOverride").SetValue(null, targetingBefore);
                }
                triggerSettingsWritten = false;
            }
            triggerRotationSuspended = false;
        }
        catch (Exception ex) { retry = true; Error = "恢复RSR设置失败：" + ex.GetBaseException().Message; }
        try
        {
            if (triggerBossOwner != null && ReferenceEquals(triggerBossOwner, CurrentBossOwner()))
            {
                if (triggerAiSuspended && !AIActive())
                {
                    if (!commands.ProcessCommand(reborn ? "/bmrai on" : "/vbm ai on") || !AIActive())
                        throw new InvalidOperationException("无法恢复BossMod AI。");
                }
                triggerAiSuspended = false;
                if (triggerPresetBefore != null && string.IsNullOrEmpty(ActivePreset())
                    && !plugin.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(triggerPresetBefore))
                    throw new InvalidOperationException("无法恢复BossMod循环预设。");
            }
            triggerPresetBefore = null;
        }
        catch (Exception ex) { retry = true; Error = "恢复BossMod设置失败：" + ex.GetBaseException().Message; }
        if (retry && ++triggerRestoreAttempts < 3) return;
        triggerModeBefore = null; triggerOwner = null; overridesBefore = null; triggerAuto = false;
        triggerSession = triggerUseRotation = triggerUseAi = false;
        triggerBossOwner = null; triggerPresetBefore = null;
        triggerEnding = triggerAiSuspended = triggerRotationSuspended = triggerSettingsWritten = false;
    }
    private readonly Dictionary<string, (string Before, string Written)> settings = new();
    private string? presetBefore;
    private Assembly? rotationOwner;
    private object? bossOwner;
    private object? aiBehaviour;
    public string? Error { get; private set; }
    private bool OwnedCleanupPending => rotationOwned || aiOwned || settings.Count > 0 || presetBefore != null;
    public bool CleanupPending => OwnedCleanupPending || triggerEnding;
    private bool RotationActive() => plugin.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive").InvokeFunc();
    private Assembly? CurrentRotationOwner() => commands.Commands.TryGetValue("/rotation", out var c) ? c.Handler.Method.DeclaringType?.Assembly : null;
    private object? CurrentBossOwner() => commands.Commands.TryGetValue(reborn ? "/bmrai" : "/vbm", out var c)
        ? c.Handler.Target ?? (object?)c.Handler.Method.DeclaringType?.Assembly : null;
    private Type RotationType(string name) => commands.Commands.TryGetValue("/rotation", out var command)
        ? command.Handler.Method.DeclaringType?.Assembly.GetType(name, false) ?? throw new InvalidOperationException("自动循环状态接口不兼容。")
        : throw new InvalidOperationException("自动循环未加载。");
    private bool RotationRequested()
    {
        // The public active IPC returns false while the player is unavailable even if the switch is still on.
        var type = RotationType("RotationSolver.Basic.DataCenter");
        return (bool)(type.GetProperty("State", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("无法读取自动循环开关。"))
            || (bool)(type.GetProperty("IsManual", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? throw new InvalidOperationException("无法读取自动循环模式。"));
    }
    private bool ManualRequested() => (bool)(RotationType("RotationSolver.Basic.DataCenter")
        .GetProperty("IsManual", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
        ?? throw new InvalidOperationException("无法读取自动循环模式。"));
    private void RotationMode(string name)
    {
        // Use the provider's actual enum type: no guessed integer ABI and no compile-time plugin dependency.
        var type = RotationType("RotationSolver.Basic.Data.StateCommandType");
        var factory = typeof(IDalamudPluginInterface).GetMethods().Single(m => m.Name == "GetIpcSubscriber"
            && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 1);
        var subscriber = factory.MakeGenericMethod(type, typeof(object)).Invoke(plugin, ["RotationSolverReborn.ChangeOperatingMode"]);
        typeof(ICallGateSubscriber<,>).MakeGenericType(type, typeof(object)).GetMethod("InvokeAction")!
            .Invoke(subscriber, [Enum.Parse(type, name)]);
    }
    private object? RebornBehaviour()
    {
        // Reborn 7.5.7.3 has no AI-enabled IPC. Read its verified managed state, never invoke private methods.
        var instance = commands.Commands.TryGetValue("/bmrai", out var command) ? command.Handler.Target : null;
        var type = instance?.GetType() ?? throw new InvalidOperationException("BossMod Reborn AI未初始化。");
        var field = type.GetField("Beh", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("当前BossMod Reborn AI状态接口不兼容。");
        return field.GetValue(instance);
    }
    private bool AIActive() => reborn ? RebornBehaviour() != null : plugin.GetIpcSubscriber<bool>("BossMod.AI.IsEnabled").InvokeFunc();
    private List<string> Config(params string[] args)
    {
        try { return plugin.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration").InvokeFunc(args.ToList(), false); }
        catch { return plugin.GetIpcSubscriber<IReadOnlyList<string>, bool, List<string>>("BossMod.Configuration").InvokeFunc(args, false); }
    }
    private string Read(string field)
    {
        var result = Config("AIConfig", field);
        if (result.Count != 1) throw new InvalidOperationException("无法读取BossMod配置：" + field);
        return result[0];
    }
    private void Set(string field, string value)
    {
        var before = Read(field);
        if (before.Equals(value, StringComparison.OrdinalIgnoreCase)) return;
        settings[field] = (before, value);
        if (Config("AIConfig", field, value).Count != 0) throw new InvalidOperationException("无法设置BossMod配置：" + field);
    }
    private string? ActivePreset() => plugin.GetIpcSubscriber<string?>("BossMod.Presets.GetActive").InvokeFunc();
    private static readonly (string Field, string Value)[] TriggerApproachSettings =
    [
        ("FollowDuringCombat", "true"), ("FollowOutOfCombat", "true"), ("FollowDuringActiveBossModule", "true"),
        ("AutoAFK", "false"), ("FocusTargetMaster", "false"), ("FollowRSRDesiredPositional", "false"),
        ("DesiredPositional", "Any"),
    ];
    public string? CheckReady()
    {
        try
        {
            if (CleanupPending) return "等待上一次战斗控制恢复完成。";
            if (!commands.Commands.ContainsKey("/rotation")) return "请安装并启用Rotation Solver Reborn。";
            reborn = commands.Commands.ContainsKey("/bmrai");
            if (!reborn && !commands.Commands.ContainsKey("/vbm")) return "请启用BossMod或BossMod Reborn。";
            if (reborn && commands.Commands.ContainsKey("/vbmai")) return "请仅启用一种BossMod，避免IPC冲突。";
            if (RotationRequested() || RotationActive() || AIActive()) return "请先关闭已运行的自动循环和BossMod AI，再开始跟随。";
            var preset = plugin.GetIpcSubscriber<string?>("BossMod.Presets.GetActive").InvokeFunc();
            if (!string.IsNullOrEmpty(preset)) return "请先关闭BossMod已启用的循环预设。";
            _ = Read("ForbidActions"); _ = Read("ForbidMovement");
            return null;
        }
        catch (Exception ex) { return "战斗插件接口不可用：" + ex.Message; }
    }
    public string? PrepareStart()
    {
        try
        {
            if (CleanupPending && !Disable()) return "等待上一次战斗控制恢复完成。";
            var useRotation = !triggerSession || triggerUseRotation;
            var useAi = !triggerSession || triggerUseAi;
            if (useRotation && !commands.Commands.ContainsKey("/rotation")) return "请安装并启用Rotation Solver Reborn。";
            reborn = commands.Commands.ContainsKey("/bmrai");
            if (useAi && !reborn && !commands.Commands.ContainsKey("/vbm")) return "请启用BossMod或BossMod Reborn。";
            if (useAi && reborn && commands.Commands.ContainsKey("/vbmai")) return "请仅启用一种BossMod，避免IPC冲突。";
            // This explicit start is authorized to close existing controls before taking a new session.
            var rotationOn = useRotation && RotationRequested(); var aiOn = useAi && AIActive();
            if (useAi) { _ = Read("ForbidActions"); _ = Read("ForbidMovement"); }
            if (triggerSession)
            {
                triggerBossOwner = useAi ? CurrentBossOwner() : null;
                triggerPresetBefore = useAi ? ActivePreset() : null;
                triggerRotationSuspended = rotationOn;
                triggerAiSuspended = aiOn;
            }
            if (useRotation && (rotationOn || RotationActive()))
            {
                RotationMode("Off");
                if (triggerModeBefore != null) CaptureTriggerOverrides();
            }
            if (aiOn && !commands.ProcessCommand(reborn ? "/bmrai off" : "/vbm ai off")) return "无法关闭已运行的BossMod AI。";
            if (useAi) plugin.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc();
            return triggerSession ? CheckTriggerReady() : CheckReady();
        }
        catch (Exception ex) { return "初始化战斗控制失败：" + ex.GetBaseException().Message; }
    }
    public bool Target(ulong bossId)
    {
        var boss = objects.OfType<IBattleNpc>().FirstOrDefault(o => o.GameObjectId == bossId && !o.IsDead && o.IsTargetable);
        if (boss == null) return false;
        if (targets.Target?.GameObjectId != bossId) targets.Target = boss;
        return targets.Target?.GameObjectId == bossId;
    }
    public unsafe bool Sync(ushort fateId)
    {
        var manager = FateManager.Instance();
        if (manager == null) return false;
        if (manager->SyncedFateId == fateId) return true;
        return manager->GetCurrentFateId() == fateId && manager->LevelSync() != null;
    }
    public bool Enable() => EnableWithAI(false);
    private string? CheckTriggerReady()
    {
        if (CleanupPending) return "等待上次战斗控制还原。";
        if (triggerUseRotation && (!ReferenceEquals(triggerOwner, CurrentRotationOwner()) || RotationRequested() || RotationActive()))
            return "RSR 已更换或有其他循环正在运行。";
        if (triggerUseAi && (!ReferenceEquals(triggerBossOwner, CurrentBossOwner()) || AIActive() || !string.IsNullOrEmpty(ActivePreset())))
            return "BossMod Reborn 已更换或有其他 AI 正在运行。";
        return null;
    }
    public bool EnableTrigger(bool approach) => triggerUseAi ? EnableWithAI(approach, !approach) : EnableStationary();
    private bool EnableWithAI(bool triggerApproach, bool stationary = false)
    {
        try
        {
            Error = null;
            if (rotationOwned || aiOwned) return triggerSession ? StationaryHealthy : Healthy;
            var error = triggerSession ? CheckTriggerReady() : CheckReady();
            if (error != null) { Error = error; return false; }
            if (triggerApproach && !reborn) { Error = "普通自动触发的自动靠近需要BossMod Reborn。"; return false; }
            if (triggerApproach && !ReferenceEquals(triggerBossOwner, CurrentBossOwner()))
            { Error = "BossMod Reborn已更换，停止触发。"; return false; }
            bossOwner = CurrentBossOwner();
            Set("ForbidActions", "true"); Set("ForbidMovement", stationary ? "true" : "false");
            if (!reborn) Set("GoalZoneFallback", "true");
            if (reborn)
            {
                Set("ManualTarget", "true"); Set("FollowSlot", "0"); Set("FollowTarget", "true");
                if (triggerApproach)
                    foreach (var (field, value) in TriggerApproachSettings) Set(field, value);
                presetBefore = plugin.GetIpcSubscriber<string>("BossMod.AI.GetPreset").InvokeFunc();
                plugin.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset").InvokeAction("");
            }
            aiOwned = true;
            if (!commands.ProcessCommand(reborn ? "/bmrai on" : "/vbm ai on") || !AIActive()) throw new InvalidOperationException("BossMod AI启动失败。");
            if (reborn) aiBehaviour = RebornBehaviour();
            if (!reborn && !commands.ProcessCommand("/vbm ai follow slot1")) throw new InvalidOperationException("BossMod跟随自身失败。");
            if (!triggerSession || triggerUseRotation)
            {
                rotationOwned = true;
                rotationOwner = CurrentRotationOwner();
                if (triggerSession) ApplyTriggerMode(triggerAuto);
                else RotationMode("Manual");
                if (!RotationRequested() || !RotationActive()) throw new InvalidOperationException("自动循环启动失败。");
            }
            return true;
        }
        catch (Exception ex) { Error = "战斗控制启动失败：" + ex.GetBaseException().Message; Disable(); return false; }
    }
    public bool EnableStationary()
    {
        try
        {
            if (rotationOwned) return StationaryHealthy;
            var error = CheckTriggerReady();
            if (error != null) { Error = error; return false; }
            if (!triggerUseRotation) return true;
            rotationOwner = CurrentRotationOwner(); rotationOwned = true;
            ApplyTriggerMode(triggerAuto);
            if (!StationaryHealthy) throw new InvalidOperationException("自动循环未启用。");
            return true;
        }
        catch (Exception ex) { Error = ex.Message; Disable(); return false; }
    }
    public bool StationaryHealthy
    {
        get
        {
            try
            {
                return (!triggerUseRotation || rotationOwned && ReferenceEquals(rotationOwner, CurrentRotationOwner())
                    && TriggerSettingsHealthy() && RotationRequested() && ManualRequested() != triggerAuto && RotationActive())
                    && (!triggerUseAi || aiOwned && ReferenceEquals(bossOwner, CurrentBossOwner()) && AIActive()
                        && ReferenceEquals(aiBehaviour, RebornBehaviour())
                        && settings.All(p => Read(p.Key).Equals(p.Value.Written, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }
    }
    public bool Healthy
    {
        get { try { return rotationOwned && aiOwned && ReferenceEquals(rotationOwner, CurrentRotationOwner())
                    && ReferenceEquals(bossOwner, CurrentBossOwner()) && ManualRequested() && RotationActive() && AIActive()
                    && (!reborn || ReferenceEquals(aiBehaviour, RebornBehaviour())); } catch { return false; } }
    }
    public bool Disable()
    {
        // Unloading/replacing a provider ends our ownership; never switch off the replacement's session.
        if (rotationOwned && !ReferenceEquals(rotationOwner, CurrentRotationOwner())) rotationOwned = false;
        if (bossOwner != null && !ReferenceEquals(bossOwner, CurrentBossOwner()))
        { aiOwned = false; settings.Clear(); presetBefore = null; bossOwner = null; }
        try
        {
            if (rotationOwned && RotationRequested() && ManualRequested() == triggerAuto) rotationOwned = false;
            if (rotationOwned && triggerModeBefore != null && RotationRequested() && !TriggerSettingsHealthy())
                rotationOwned = false;
            if (aiOwned && reborn && aiBehaviour != null && RebornBehaviour() is { } current && !ReferenceEquals(current, aiBehaviour)) aiOwned = false;
        }
        catch { }
        if (rotationOwned)
        {
            try
            {
                if (RotationRequested())
                {
                    var ownsOverrides = triggerModeBefore != null && TriggerSettingsHealthy();
                    RotationMode("Off");
                    if (ownsOverrides) CaptureTriggerOverrides();
                }
                if (!RotationRequested()) rotationOwned = false;
            }
            catch { if (!commands.Commands.ContainsKey("/rotation")) rotationOwned = false; }
        }
        if (aiOwned)
        {
            try
            {
                if (!AIActive() || commands.ProcessCommand(reborn ? "/bmrai off" : "/vbm ai off") && !AIActive()) aiOwned = false;
            }
            catch { if (!commands.Commands.ContainsKey(reborn ? "/bmrai" : "/vbm")) aiOwned = false; }
        }
        if (aiOwned || rotationOwned) return false;
        foreach (var pair in settings.ToArray())
        {
            try
            {
                if (Read(pair.Key).Equals(pair.Value.Written, StringComparison.OrdinalIgnoreCase)
                    && Config("AIConfig", pair.Key, pair.Value.Before).Count != 0) continue;
                settings.Remove(pair.Key);
            }
            catch { }
        }
        if (presetBefore != null)
        {
            try
            {
                if (plugin.GetIpcSubscriber<string>("BossMod.AI.GetPreset").InvokeFunc() == "")
                    plugin.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset").InvokeAction(presetBefore);
                presetBefore = null;
            }
            catch { }
        }
        if (!OwnedCleanupPending)
        {
            bossOwner = null; rotationOwner = null; aiBehaviour = null;
            if (triggerEnding) RestoreTrigger();
        }
        return !OwnedCleanupPending;
    }

    public void SuppressEmergencyResume()
    {
        triggerRotationSuspended = triggerAiSuspended = false;
        triggerPresetBefore = null;
    }

    public string EmergencyStop()
    {
        var failures = new List<string>();
        try
        {
            if (commands.Commands.ContainsKey("/rotation") && RotationRequested()) RotationMode("Off");
        }
        catch { failures.Add("RSR"); }
        try
        {
            if (commands.Commands.ContainsKey("/bmrai") && RebornBehaviour() != null)
                commands.ProcessCommand("/bmrai off");
            else if (commands.Commands.ContainsKey("/vbm")
                && plugin.GetIpcSubscriber<bool>("BossMod.AI.IsEnabled").InvokeFunc())
                commands.ProcessCommand("/vbm ai off");
        }
        catch { failures.Add("BossMod AI"); }
        return failures.Count == 0 ? "已执行紧急停止。" : $"已停止本插件任务，未能确认关闭：{string.Join("、", failures)}。";
    }
}
