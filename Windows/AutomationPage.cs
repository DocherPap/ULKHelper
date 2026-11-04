using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Automation;

namespace ULKHelper.Windows;

internal sealed class AutomationPage(IAutomationHelpers helpers, FateMapPage? map = null)
{
    private string filter = "";
    private PlayerIdentity? selected;
    public void Load(SettingsStore settings) => filter = settings.Get("automation.playerFilter", "");
    public void Save(SettingsStore settings) => settings.Put("automation.playerFilter", filter);

    public void Draw()
    {
        var build = typeof(AutomationPage).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        ImGui.TextDisabled("构建：" + build);
        if (map == null) ImGui.TextDisabled("地图数据尚未就绪。");
        else map.Draw(this);
    }

    public void DrawFollow(AutomationSnapshot state)
    {
        var residentsLabel = $"岛民（{state.Players.Count}）";
        var residentsWidth = ImGui.CalcTextSize(residentsLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.BeginDisabled(!state.Following && (state.Leader == null || state.SelectedMount == 0 || state.Navigating));
        if (ImGui.Button(state.Following ? "停止跟随伐木" : "开始跟随伐木",
            new Vector2(Math.Max(80, ImGui.GetContentRegionAvail().X - residentsWidth - ImGui.GetStyle().ItemSpacing.X), 0)))
            helpers.Follow(!state.Following);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(residentsLabel + "###FollowResidents")) ImGui.OpenPopup("##FollowResidentsPopup");
        ImGui.SetNextWindowSize(new Vector2(280, 290), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("##FollowResidentsPopup"))
        {
            DrawResidents(state);
            ImGui.EndPopup();
        }
        ImGui.TextUnformatted(state.FollowStatus);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(state.FollowStatus);
        var leader = state.Leader;
        ImGui.TextUnformatted(leader == null ? "车头：尚未选择" : $"车头：{leader.Name}");
        if (leader != null && ImGui.IsItemHovered())
        {
            var world = state.Players.FirstOrDefault(p => p.Identity == leader)?.WorldName ?? leader.HomeWorld.ToString();
            ImGui.SetTooltip($"{leader.Name} @ {world}");
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(leader == null);
        if (ImGui.SmallButton(state.Listening ? "停止监听" : "开始监听")) helpers.Listen(!state.Listening);
        ImGui.SameLine();
        if (ImGui.SmallButton("清除车头")) helpers.ClearLeader();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("当前目标设为车头")) helpers.SelectCurrentTargetAsLeader();
        ImGui.TextUnformatted(state.ListenerStatus);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(state.ListenerStatus);
    }

    public void DrawResidents(AutomationSnapshot state)
    {
        if (ImGui.SmallButton("刷新")) helpers.RefreshPlayers();
        ImGui.SameLine();
        ImGui.BeginDisabled(selected == null || !state.Players.Any(p => p.Identity == selected));
        if (ImGui.SmallButton("设为车头")) helpers.SelectLeader(selected!);
        ImGui.EndDisabled();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##PlayerSearch", "搜索岛民", ref filter, 100);
        foreach (var player in state.Players.Where(p => string.IsNullOrWhiteSpace(filter)
            || p.Identity.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || p.WorldName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            if (ImGui.Selectable($"{player.Identity.Name}##{player.Identity.HomeWorld}", selected == player.Identity)) selected = player.Identity;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{player.Identity.Name} @ {player.WorldName}\n{(player.IsLoaded ? $"距离 {player.Distance:0}m" : "位置未加载")}");
        }
    }

    public void DrawHistory(AutomationSnapshot state)
    {
        if (ImGui.CollapsingHeader("历史记录（车头 / 小怪击杀，最近50条）"))
        {
            ImGui.TextUnformatted("未触发NM小怪击杀（本地观测）");
            var counts = state.KillCounts ?? [];
            if (counts.Count == 0) ImGui.TextDisabled("尚未观测到击杀。");
            else
                foreach (var count in counts) ImGui.TextWrapped($"{count.FateName} · {count.MobName}：{count.Count}");
            foreach (var message in state.Messages)
            {
                ImGui.TextWrapped($"{message.Time.ToLocalTime():HH:mm:ss} [{message.Channel}] {message.Text}");
                ImGui.TextDisabled(message.Result);
                ImGui.Separator();
            }
        }
    }
}
