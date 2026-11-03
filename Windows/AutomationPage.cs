using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Automation;

namespace ULKHelper.Windows;

internal sealed class AutomationPage(IAutomationHelpers helpers, FateMapPage? map = null)
{
    private string filter = "";
    private PlayerIdentity? selected;

    public void Draw()
    {
        var state = helpers.Snapshot;
        if (!ImGui.BeginTable("##AutomationMapLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            ImGui.TableSetupColumn("地图与自动触发", ImGuiTableColumnFlags.WidthStretch, 1.35f);
            ImGui.TableSetupColumn("跟随与导航", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("##AutomationMap", Vector2.Zero))
            {
                if (map == null) ImGui.TextDisabled("地图数据尚未就绪。");
                else map.DrawTriggerPlanner(state.KillCounts);
            }
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##AutomationControls", Vector2.Zero)) DrawControls(state);
            ImGui.EndChild();
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawControls(AutomationSnapshot state)
    {
        ImGui.BeginDisabled(!state.Following && (state.Leader == null || state.SelectedMount == 0 || state.Navigating));
        if (ImGui.Button(state.Following ? "停止跟随伐木" : "开始跟随伐木", new Vector2(-1, 0))) helpers.Follow(!state.Following);
        ImGui.EndDisabled();
        ImGui.TextWrapped(state.FollowStatus);
        ImGui.Separator();
        if (ImGui.BeginTable("##LeaderAndResidents", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("车头", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("岛民", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.TextUnformatted("车头");
            var leader = state.Leader;
            var worldName = state.Players.FirstOrDefault(p => p.Identity == leader)?.WorldName;
            ImGui.TextWrapped(leader == null ? "尚未选择车头" : $"{leader.Name}\n@ {worldName ?? leader.HomeWorld.ToString()}");
            ImGui.BeginDisabled(leader == null);
            if (ImGui.Button(state.Listening ? "停止监听" : "开始监听")) helpers.Listen(!state.Listening);
            ImGui.EndDisabled();
            ImGui.TextWrapped(state.ListenerStatus);
            if (ImGui.Button("当前目标设为车头", new Vector2(-1, 0))) helpers.SelectCurrentTargetAsLeader();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("将游戏中当前选中的玩家设为车头。");
            ImGui.TableNextColumn();
            var inlineButtons = ImGui.GetContentRegionAvail().X >= ImGui.CalcTextSize($"岛民（{state.Players.Count}）刷新设为车头").X
                + ImGui.GetStyle().ItemSpacing.X * 3 + ImGui.GetStyle().FramePadding.X * 4;
            ImGui.TextUnformatted($"岛民（{state.Players.Count}）");
            if (inlineButtons) ImGui.SameLine();
            if (ImGui.SmallButton("刷新")) helpers.RefreshPlayers();
            ImGui.SameLine();
            ImGui.BeginDisabled(selected == null || !state.Players.Any(p => p.Identity == selected));
            if (ImGui.SmallButton("设为车头")) helpers.SelectLeader(selected!);
            ImGui.EndDisabled();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##PlayerSearch", "搜索岛民", ref filter, 100);
            if (ImGui.BeginChild("##PlayerList", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 4.5f), true))
            {
                foreach (var player in state.Players.Where(p => string.IsNullOrWhiteSpace(filter)
                    || p.Identity.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || p.WorldName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    if (ImGui.Selectable($"{player.Identity.Name}##{player.Identity.HomeWorld}", selected == player.Identity)) selected = player.Identity;
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip(); ImGui.TextUnformatted($"{player.Identity.Name} @ {player.WorldName}");
                        ImGui.TextUnformatted(player.IsLoaded ? $"距离 {player.Distance:0}m" : "位置未加载"); ImGui.EndTooltip();
                    }
                }
            }
            ImGui.EndChild(); ImGui.EndTable();
        }
        ImGui.Separator();
        ImGui.TextUnformatted("导航");
        var owned = state.Mounts ?? [];
        ImGui.BeginDisabled(state.Navigating);
        ImGui.TextUnformatted("坐骑"); ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##FollowMount", owned.FirstOrDefault(m => m.Id == state.SelectedMount)?.Name ?? "选择已拥有的坐骑"))
        {
            foreach (var mount in owned)
            {
                ImGui.BeginDisabled(mount.Immobile);
                if (ImGui.Selectable(mount.Name + (mount.Immobile ? "（不可移动）" : "") + $"##Mount{mount.Id}", mount.Id == state.SelectedMount)) helpers.SelectMount(mount.Id);
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(state.Destination?.Label ?? "尚无车头坐标。");
        ImGui.BeginDisabled(state.Destination == null || state.Navigating);
        if (ImGui.Button("开始导航到目的地")) helpers.Navigate();
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.BeginDisabled(!state.Navigating);
        if (ImGui.Button("停止")) helpers.Stop();
        ImGui.EndDisabled();
        ImGui.TextWrapped(state.NavigationStatus);
        ImGui.Separator();
        ImGui.TextUnformatted("未触发NM小怪击杀（本地观测）");
        var counts = state.KillCounts ?? [];
        if (counts.Count == 0) ImGui.TextDisabled("尚未观测到击杀。");
        else
        {
            if (ImGui.BeginChild("##TriggerKillCounts", new Vector2(0, Math.Min(4, counts.Count) * ImGui.GetTextLineHeightWithSpacing())))
                foreach (var count in counts) ImGui.TextWrapped($"{count.FateName} · {count.MobName}：{count.Count}");
            ImGui.EndChild();
        }
        if (ImGui.CollapsingHeader("历史记录（车头 / 小怪击杀，最近50条）"))
        {
            if (ImGui.BeginChild("##LeaderMessages", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 7)))
            {
                foreach (var message in state.Messages)
                {
                    ImGui.TextWrapped($"{message.Time.ToLocalTime():HH:mm:ss} [{message.Channel}] {message.Text}");
                    ImGui.TextDisabled(message.Result); ImGui.Separator();
                }
            }
            ImGui.EndChild();
        }
    }
}
