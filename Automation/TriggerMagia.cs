using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;

namespace ULKHelper.Automation;

internal sealed class TriggerMagia(ISigScanner scanner, IDataManager data, ITargetManager targets)
{
    // CN 2026.10.06: ShellCommandModule dispatches a parsed CommandContext here.
    // The game looks up the registered handler and initializes it before calling ExecuteCommand.
    private const string DispatchSignature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B 59 08 49 8B F0 44 0F BF 0A 48 8B FA 48 8B CB 48 8B 43 08 80 78 19 00";
    private const ushort AttackId = 10; // TextCommand row, checked against the locally loaded sheet.
    private bool resolved;
    private nint dispatch;

    public unsafe string? TryAttack(IReadOnlyList<ulong> eligible)
    {
        if (targets.Target is not IBattleNpc { IsDead: false, IsTargetable: true } enemy || !eligible.Contains(enemy.GameObjectId))
            return "魔素板未调整：未选中可接战的敌人。";
        var director = EventFramework.Instance() == null ? null
            : (PublicContentEureka*)EventFramework.GetPublicContentDirectorByType(PublicContentDirectorType.Eureka);
        if (director == null) return "魔素板未调整：未读取到优雷卡副本。";
        var state = director->State;
        if (state.Fire < 5 && state.Ice < 5 && state.Wind < 5 && state.Earth < 5 && state.Lightning < 5 && state.Water < 5)
            return "魔素板未调整：没有集中 5 颗魔晶石的属性。";

        try
        {
            if (!resolved)
            {
                resolved = true;
                if (data.GetExcelSheet<Lumina.Excel.Sheets.TextCommand>() is { } sheet
                    && sheet.TryGetRow(AttackId, out var row) && row.Command.ExtractText() == "/magiaattack")
                    scanner.TryScanText(DispatchSignature, out dispatch);
            }
            var ui = UIModule.Instance();
            var shell = ui == null ? null : ui->GetRaptureShellModule();
            if (dispatch == 0 || shell == null || shell->IsTextCommandUnavailable)
                return "魔素板未调整：原生攻势接口当前不可用。";

            // No explicit arguments: the native action resolves the currently selected enemy.
            ShellCommandInterface.CommandContext context = new() { TextCommandId = AttackId };
            var invoke = (delegate* unmanaged<ShellCommands*, ShellCommandInterface.CommandContext*, UIModule*, int>)dispatch;
            var result = invoke(&shell->ShellCommands, &context, ui);
            return result == 0 ? null : $"魔素板攻势请求返回 {result}。";
        }
        catch (Exception ex)
        {
            return $"魔素板未调整：{ex.GetBaseException().Message}";
        }
    }
}
