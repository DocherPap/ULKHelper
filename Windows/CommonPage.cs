using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Automation;
using ULKHelper.Fates;

namespace ULKHelper.Windows;

internal sealed class CommonPage(AutomationHelpers helpers, IFateMapProvider maps)
{
    public void Draw()
    {
        ImGui.TextUnformatted("常用功能");
        if (ImGui.Button("紧急停止", new Vector2(-1, ImGui.GetFrameHeight() * 1.5f)))
            helpers.RunCommon(CommonAction.Emergency);
        ImGui.TextWrapped(helpers.CommonStatus);
        ImGui.Separator();

        ImGui.TextUnformatted("前往与进岛");
        if (ImGui.BeginTable("##CommonTravel", 2, ImGuiTableFlags.SizingStretchSame))
        {
            Button("自动前往入口", CommonAction.Entrance);
            Button("返回营地", CommonAction.Camp);
            Button("进入常风之地", CommonAction.Anemos);
            Button("进入恒冰之地", CommonAction.Pagos);
            Button("进入涌火之地", CommonAction.Pyros);
            Button("进入丰水之地", CommonAction.Hydatos);
            Button("购买恢复药 ×99（29700金币）", CommonAction.Potion);
            Button("前往最近的水晶挂机", CommonAction.NearestCrystal);
            ImGui.EndTable();
        }
        ImGui.Separator();

        ImGui.TextUnformatted("地图与战斗");
        ImGui.TextWrapped("坐标导航使用当前岛地图上的旗标。");
        if (ImGui.BeginTable("##CommonNavigation", 2, ImGuiTableFlags.SizingStretchSame))
        {
            Button("自动寻路坐标", CommonAction.Coordinate);
            Button("自动寻路FATE", CommonAction.Fate);
            Button("自动等待开怪", CommonAction.WaitForPull);
            Button("等级同步", CommonAction.Sync);
            ImGui.EndTable();
        }
        ImGui.Separator();

        ImGui.TextUnformatted("组队与喊话");
        var invite = helpers.AutoInvite;
        var accept = helpers.AutoAccept;
        if (ImGui.Checkbox("自动组队", ref invite)) helpers.SetCommonParty(true, invite);
        ImGui.SameLine();
        if (ImGui.Checkbox("自动接受组队邀请", ref accept)) helpers.SetCommonParty(false, accept);
        var snapshot = maps.Snapshot;
        var island = maps.Islands.FirstOrDefault(i => i.Territory == snapshot.CurrentTerritory);
        var point = snapshot.Elementals?.Where(p => p.Territory == island?.Territory && p.MapId == island?.MapId).MaxBy(p => p.LastSeen);
        MapDestination? destination = point != null && island != null
            ? new(island.Territory, island.MapId, MapProjection.MapToWorld(point.Position, island), "最后已知元灵") : null;
        if (ImGui.BeginTable("##CommonShout", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(destination == null);
            if (ImGui.Button("发送元灵坐标", new Vector2(-1, 0))) helpers.RunCommon(CommonAction.Elemental, destination);
            ImGui.EndDisabled();
            Button("发送求救", CommonAction.Rescue);
            ImGui.EndTable();
        }
    }

    private void Button(string label, CommonAction action)
    {
        ImGui.TableNextColumn();
        if (ImGui.Button(label, new Vector2(-1, 0))) helpers.RunCommon(action);
    }
}
