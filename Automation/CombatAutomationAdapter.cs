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
    private readonly Dictionary<string, (string Before, string Written)> settings = new();
    private string? presetBefore;
    private Assembly? rotationOwner;
    private object? bossOwner;
    private object? aiBehaviour;
    public string? Error { get; private set; }
    public bool CleanupPending => rotationOwned || aiOwned || settings.Count > 0 || presetBefore != null;
    public string DescribeState()
    {
        string rotation, ai;
        try { rotation = $"RSR 开关={RotationRequested()} 实际运行={RotationActive()} Manual={ManualRequested()}"; }
        catch (Exception ex) { rotation = "RSR 状态不可读：" + ex.GetBaseException().Message; }
        try
        {
            var active = commands.Commands.ContainsKey("/bmrai") ? RebornBehaviour() != null
                : plugin.GetIpcSubscriber<bool>("BossMod.AI.IsEnabled").InvokeFunc();
            ai = $"BossMod AI={active} 本插件循环所有权={rotationOwned} AI所有权={aiOwned}";
        }
        catch (Exception ex) { ai = "BossMod 状态不可读：" + ex.GetBaseException().Message; }
        return rotation + "\n" + ai;
    }
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
            if (!commands.Commands.ContainsKey("/rotation")) return "请安装并启用Rotation Solver Reborn。";
            reborn = commands.Commands.ContainsKey("/bmrai");
            if (!reborn && !commands.Commands.ContainsKey("/vbm")) return "请启用BossMod或BossMod Reborn。";
            if (reborn && commands.Commands.ContainsKey("/vbmai")) return "请仅启用一种BossMod，避免IPC冲突。";
            // This explicit start is authorized to close existing controls before taking a new session.
            var rotationOn = RotationRequested(); var aiOn = AIActive();
            _ = Read("ForbidActions"); _ = Read("ForbidMovement");
            if (rotationOn || RotationActive()) RotationMode("Off");
            if (aiOn && !commands.ProcessCommand(reborn ? "/bmrai off" : "/vbm ai off")) return "无法关闭已运行的BossMod AI。";
            plugin.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc();
            return CheckReady();
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
    public bool Enable()
    {
        try
        {
            Error = null;
            if (rotationOwned || aiOwned) return Healthy;
            var error = CheckReady();
            if (error != null) { Error = error; return false; }
            bossOwner = CurrentBossOwner();
            Set("ForbidActions", "true"); Set("ForbidMovement", "false");
            if (!reborn) Set("GoalZoneFallback", "true");
            if (reborn)
            {
                Set("ManualTarget", "true"); Set("FollowSlot", "0"); Set("FollowTarget", "true");
                presetBefore = plugin.GetIpcSubscriber<string>("BossMod.AI.GetPreset").InvokeFunc();
                plugin.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset").InvokeAction("");
            }
            aiOwned = true;
            if (!commands.ProcessCommand(reborn ? "/bmrai on" : "/vbm ai on") || !AIActive()) throw new InvalidOperationException("BossMod AI启动失败。");
            if (reborn) aiBehaviour = RebornBehaviour();
            if (!reborn && !commands.ProcessCommand("/vbm ai follow slot1")) throw new InvalidOperationException("BossMod跟随自身失败。");
            rotationOwned = true;
            rotationOwner = CurrentRotationOwner();
            RotationMode("Manual");
            if (!RotationRequested() || !RotationActive()) throw new InvalidOperationException("自动循环启动失败。");
            return true;
        }
        catch (Exception ex) { Error = "战斗控制启动失败：" + ex.GetBaseException().Message; Disable(); return false; }
    }
    public bool EnableStationary()
    {
        try
        {
            if (rotationOwned) return StationaryHealthy;
            var error = CheckReady();
            if (error != null) { Error = error; return false; }
            rotationOwner = CurrentRotationOwner(); rotationOwned = true;
            RotationMode("Manual");
            if (!StationaryHealthy) throw new InvalidOperationException("自动循环未启用。");
            return true;
        }
        catch (Exception ex) { Error = ex.Message; Disable(); return false; }
    }
    public bool StationaryHealthy
    {
        get { try { return rotationOwned && !aiOwned && ReferenceEquals(rotationOwner, CurrentRotationOwner())
            && ManualRequested() && RotationActive() && !AIActive(); } catch { return false; } }
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
            if (rotationOwned && RotationRequested() && !ManualRequested()) rotationOwned = false;
            if (aiOwned && reborn && aiBehaviour != null && RebornBehaviour() is { } current && !ReferenceEquals(current, aiBehaviour)) aiOwned = false;
        }
        catch { }
        if (rotationOwned)
        {
            try
            {
                if (RotationRequested()) RotationMode("Off");
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
        if (!CleanupPending) { bossOwner = null; rotationOwner = null; aiBehaviour = null; }
        return !CleanupPending;
    }
}
