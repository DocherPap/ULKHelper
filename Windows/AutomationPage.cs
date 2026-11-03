using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Automation;

namespace ULKHelper.Windows;

internal sealed class AutomationPage(IAutomationHelpers helpers)
{
    private string filter = "";
    private PlayerIdentity? selected;

    public void Draw()
    {
        const string title = "施工中";
        var available = ImGui.GetContentRegionAvail();
        var scale = Math.Clamp(available.X / (ImGui.CalcTextSize(title).X * 1.5f), 1f, 5f);
        var size = ImGui.CalcTextSize(title) * scale;
        var origin = ImGui.GetCursorScreenPos();
        var bannerHeight = size.Y + ImGui.GetStyle().ItemSpacing.Y * 4;
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
            origin + new Vector2(Math.Max(0, (available.X - size.X) / 2), (bannerHeight - size.Y) / 2),
            ImGui.GetColorU32(new Vector4(1f, .72f, .25f, 1f)), title);
        ImGui.Dummy(new Vector2(0, bannerHeight));
        var state = helpers.Snapshot;
        ImGui.TextWrapped("独立 helper 调试：车头消息只标记地图。导航需要单独点击开始，并安装、启用 vnavmesh。");
        ImGui.Separator();
        if (!ImGui.BeginTable("##AutomationHelpers", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            ImGui.TableSetupColumn("玩家列表", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("车头监听与导航", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.TextUnformatted($"当前已加载玩家（{state.Players.Count}）");
            ImGui.TextDisabled("每秒刷新；仅包含客户端已加载对象。");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##PlayerSearch", "搜索玩家 / 服务器", ref filter, 100);
            var height = Math.Max(120, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 2);
            if (ImGui.BeginChild("##PlayerList", new Vector2(0, height)))
            {
                foreach (var player in state.Players.Where(p => string.IsNullOrWhiteSpace(filter)
                    || p.Identity.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || p.WorldName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    var label = $"{player.Identity.Name} @ {player.WorldName}    {player.Distance:0}m##{player.Identity.HomeWorld}";
                    if (ImGui.Selectable(label, selected == player.Identity)) selected = player.Identity;
                }
            }
            ImGui.EndChild();
            ImGui.BeginDisabled(selected == null || !state.Players.Any(p => p.Identity == selected));
            if (ImGui.Button("将选中玩家设为车头", new Vector2(-1, 0))) helpers.SelectLeader(selected!);
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted("车头监听");
            var leader = state.Leader;
            var worldName = state.Players.FirstOrDefault(p => p.Identity == leader)?.WorldName;
            ImGui.TextWrapped(leader == null ? "尚未选择车头" : $"车头：{leader.Name} @ {worldName ?? leader.HomeWorld.ToString()}");
            ImGui.BeginDisabled(leader == null);
            if (ImGui.Button(state.Listening ? "停止监听" : "开始监听")) helpers.Listen(!state.Listening);
            ImGui.EndDisabled();
            ImGui.TextWrapped(state.ListenerStatus);
            ImGui.TextDisabled("说话 / 呼喊 / 喊话；25 米内坐标跳过。跨地图坐标跳过。");
            ImGui.Separator();
            ImGui.TextUnformatted("目的地导航");
            ImGui.TextWrapped(state.Destination?.Label ?? "尚无车头坐标。");
            ImGui.BeginDisabled(state.Destination == null || state.Navigating);
            if (ImGui.Button("开始导航到目的地")) helpers.Navigate();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!state.Navigating);
            if (ImGui.Button("停止导航")) helpers.Stop();
            ImGui.EndDisabled();
            ImGui.TextWrapped(state.NavigationStatus);
            ImGui.TextDisabled("附近 50 米匹配进行中的 NM；无 NM 等待 2 秒。");
            ImGui.TextDisabled("初始距中心 25 米；接近目的地后改为距中心 30 米。");
            ImGui.Separator();
            ImGui.TextUnformatted("车头消息（最近 50 条）");
            if (ImGui.BeginChild("##LeaderMessages", new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y))))
            {
                foreach (var message in state.Messages)
                {
                    ImGui.TextWrapped($"{message.Time.ToLocalTime():HH:mm:ss} [{message.Channel}] {message.Text}");
                    ImGui.TextDisabled(message.Result);
                    ImGui.Separator();
                }
            }
            ImGui.EndChild();
        }
        finally { ImGui.EndTable(); }
    }
}
