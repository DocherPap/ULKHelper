using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using ULKHelper.Automation;

namespace ULKHelper.Windows;

internal sealed class DebugTestsPage(IAutomationHelpers helpers, SettingsStore? settings = null)
{
    private uint crystal;
    private string note = settings?.Get("debug.note", "我的单人测试") ?? "我的单人测试";
    private string settingsResult = "修改文字并保存，重载插件后检查是否保留。";
    public void Draw()
    {
        var test = helpers.DebugSnapshot; var state = helpers.Snapshot;
        ImGui.TextUnformatted("单人调试");
        ImGui.TextWrapped("先在安全营地测试。水晶测试只传送一次，不需要车头、队友或vnavmesh。页面不会自动开启战斗循环，不向其他玩家发送测试消息。");
        if (ImGui.Button("读取现场状态（只读）")) helpers.RunDebug(SoloDebugAction.Inspect);
        ImGui.SameLine();
        if (ImGui.Button("复制测试报告")) ImGui.SetClipboardText(Report(test, state));
        ImGui.TextDisabled("测试会自动采集现场，也可手动读取；报告只复制到剪贴板，不自动发送。");
        var recording = helpers.Recording;
        ImGui.BeginDisabled(recording.Saving);
        if (ImGui.Button(recording.Active ? "结束并保存记录" : "开始记录")) helpers.RecordDebug(!recording.Active);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(recording.Text == null);
        if (ImGui.Button("复制最近记录")) ImGui.SetClipboardText(recording.Text!);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(recording.Path == null);
        if (ImGui.Button("复制记录文件路径")) ImGui.SetClipboardText(recording.Path!);
        ImGui.EndDisabled();
        ImGui.TextWrapped(recording.Status);
        if (ImGui.Button("停止测试及本插件移动")) { helpers.StopDebug(); helpers.Stop(); }
        ImGui.Separator();
        ImGui.TextWrapped("测试结果：" + test.Status);
        ImGui.TextWrapped("移动状态：" + state.NavigationStatus);
        ImGui.BeginDisabled(test.Active || state.Navigating);
        ImGui.TextUnformatted("1. 坐骑：在营地脱战，选择坐骑，依次上/下坐骑。");
        var owned = state.Mounts ?? [];
        ImGui.SetNextItemWidth(Math.Min(380, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##DebugMount", owned.FirstOrDefault(m => m.Id == state.SelectedMount)?.Name ?? "等待角色坐骑列表"))
        {
            foreach (var mount in owned)
            {
                ImGui.BeginDisabled(mount.Immobile);
                if (ImGui.Selectable(mount.Name + "##DebugMount" + mount.Id, mount.Id == state.SelectedMount)) helpers.SelectMount(mount.Id);
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(state.SelectedMount == 0);
        if (ImGui.Button("单次上坐骑")) helpers.RunDebug(SoloDebugAction.Mount);
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("单次下坐骑")) helpers.RunDebug(SoloDebugAction.Dismount);
        ImGui.Separator();
        ImGui.TextWrapped("2. 水晶：手动走到本岛一座水晶5米内，关闭菜单，选择另一座已解锁水晶后点击。可骑乘开始，会先下坐骑；传送落地即结束，不自动回程。");
        if (!test.Crystals.Any(c => c.PlaceNameId == crystal)) crystal = test.Crystals.FirstOrDefault()?.PlaceNameId ?? 0;
        ImGui.SetNextItemWidth(Math.Min(380, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##DebugCrystal", test.Crystals.FirstOrDefault(c => c.PlaceNameId == crystal)?.Name ?? "请先进入优雷卡"))
        {
            foreach (var destination in test.Crystals)
                if (ImGui.Selectable(destination.Name + "##Crystal" + destination.PlaceNameId, crystal == destination.PlaceNameId)) crystal = destination.PlaceNameId;
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(crystal == 0);
        if (ImGui.Button("单次水晶传送")) helpers.RunDebug(SoloDebugAction.CrystalTeleport, crystal);
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextWrapped("3. 导航：在安全位置点击返回入口。检查能否上坐骑、寻路，以及到达后是否停止。途中可点击顶部停止按钮；最长5分钟。");
        if (ImGui.Button("测试前往入口营地")) helpers.GoToEntrance();
        if (ImGui.Button("手动刷新岛民名单")) helpers.RefreshPlayers();
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextWrapped("4. 设置：修改下方文字保存，然后重载插件，或关闭再打开窗口并从JSON重读。物品筛选、通知模板和窗口栏宽也可同时检查。");
        ImGui.SetNextItemWidth(Math.Min(500, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("测试文字", ref note, 200);
        ImGui.BeginDisabled(settings == null);
        if (ImGui.Button("保存测试文字")) { settings!.Put("debug.note", note); settings.Flush(true); settingsResult = "已提交保存，请从JSON重读或重载插件核对。"; }
        ImGui.SameLine();
        if (ImGui.Button("从JSON重读"))
        {
            var stored = settings!.ReadFromDisk("debug.note", "（没有保存的测试文字）");
            settingsResult = stored == note ? "通过：磁盘JSON与测试文字一致。" : "磁盘读取：" + stored;
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(settingsResult);
        ImGui.TextWrapped("真实NM开怪、同步及战后上坐骑仍需遇到NM；不要求组队或指定车头。请在自动化页继续调试，不用模拟怪物替代实际战斗。");
        if (ImGui.CollapsingHeader("现场状态与测试记录", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped(test.Diagnostics);
            foreach (var line in test.History) ImGui.TextWrapped($"{line.Time.ToLocalTime():HH:mm:ss} {line.Message}");
        }
    }
    private string Report(SoloDebugSnapshot test, AutomationSnapshot state)
    {
        var result = new StringBuilder("ULKHelper 单人调试报告\n");
        result.AppendLine(test.Diagnostics).AppendLine("测试：" + test.Status).AppendLine("移动：" + state.NavigationStatus)
            .AppendLine("跟随：" + state.FollowStatus)
            .AppendLine("设置测试：" + settingsResult);
        foreach (var line in test.History) result.AppendLine($"{line.Time.ToLocalTime():HH:mm:ss} {line.Message}");
        return result.ToString();
    }
}
