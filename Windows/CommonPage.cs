using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Automation;
using ULKHelper.Fates;

namespace ULKHelper.Windows;

internal sealed class CommonPage(AutomationHelpers helpers, IFateMapProvider maps)
{
    private string discordOwner = "", discordToken = "";

    public void Draw(bool compact = false, Action? drawWindowSettings = null)
    {
        if (!compact) ImGui.TextUnformatted("常用功能");
        if (ImGui.Button("紧急停止", compact ? Vector2.Zero : new Vector2(-1, ImGui.GetFrameHeight() * 1.5f)))
            helpers.RunCommon(CommonAction.Emergency);
        if (compact) ImGui.SameLine();
        ImGui.TextWrapped(helpers.CommonStatus);
        drawWindowSettings?.Invoke();
        ImGui.Separator();

        if (!compact) ImGui.TextUnformatted("前往与进岛");
        var columns = compact ? 4 : 2;
        if (ImGui.BeginTable("##CommonTravel", columns, ImGuiTableFlags.SizingStretchSame))
        {
            Button(compact ? "前往入口" : "自动前往入口", CommonAction.Entrance);
            Button("返回营地", CommonAction.Camp);
            Button(compact ? "进风岛" : "进入常风之地", CommonAction.Anemos);
            Button(compact ? "进冰岛" : "进入恒冰之地", CommonAction.Pagos);
            Button(compact ? "进火岛" : "进入涌火之地", CommonAction.Pyros);
            Button(compact ? "进水岛" : "进入丰水之地", CommonAction.Hydatos);
            Button(compact ? "恢复药 ×99" : "购买恢复药 ×99（29700金币）", CommonAction.Potion);
            if (compact && ImGui.IsItemHovered()) ImGui.SetTooltip("购买 99 个恢复药（29700 金币）");
            Button(compact ? "水晶挂机" : "前往最近的水晶挂机", CommonAction.NearestCrystal);
            ImGui.EndTable();
        }
        var checkRing = helpers.CheckPagosRing;
        if (ImGui.Checkbox(compact ? "检查闪袭指环" : "进入恒冰之地前检查闪袭指环", ref checkRing)) helpers.SetCheckPagosRing(checkRing);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("未来 3 小时内有薄雾，携带或装备闪袭指环会取消自动参加。如果需要跳过检查请取消勾选。");
        if (compact) ImGui.SameLine();
        var walkNearDragon = helpers.WalkNearPagosDragon;
        if (ImGui.Checkbox(compact ? "冰龙旁走路" : "恒冰之地靠近冰雪龙时自动切换走路", ref walkNearDragon)) helpers.SetWalkNearPagosDragon(walkNearDragon);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("靠近虚无冰雪龙 15 米范围时进入走路状态");
        if (compact) ImGui.SameLine();
        var survey = helpers.QuickElementalSurvey;
        if (ImGui.Checkbox("快速检查元灵", ref survey)) helpers.SetQuickElementalSurvey(survey);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("进岛后巡查本岛可用水晶，记录各水晶周围 100 米内可见的元灵，检查完返回营地。");
        ImGui.Separator();

        DrawNavigationSettings(compact);
        if (!compact && ImGui.CollapsingHeader("Discord 远程进岛")) DrawDiscord();
        ImGui.Separator();
        if (!compact)
        {
            ImGui.TextUnformatted("地图与战斗");
            ImGui.TextWrapped("坐标导航使用当前岛地图上的旗标。");
        }
        if (ImGui.BeginTable("##CommonNavigation", columns, ImGuiTableFlags.SizingStretchSame))
        {
            Button(compact ? "寻路旗标" : "自动寻路坐标", CommonAction.Coordinate);
            Button(compact ? "寻路 FATE" : "自动寻路FATE", CommonAction.Fate);
            Button(compact ? "等待开怪" : "自动等待开怪", CommonAction.WaitForPull);
            Button("等级同步", CommonAction.Sync);
            Button(compact ? "寻找宝藏" : "自动寻找宝藏", CommonAction.BunnyTreasure);
            Button("吃 FATE", CommonAction.EatFate);
            if (compact) DrawShoutButtons();
            ImGui.EndTable();
        }
        ImGui.Separator();

        if (!compact) ImGui.TextUnformatted("组队与喊话");
        var invite = helpers.AutoInvite;
        var accept = helpers.AutoAccept;
        if (ImGui.Checkbox("自动组队", ref invite)) helpers.SetCommonParty(true, invite);
        ImGui.SameLine();
        if (ImGui.Checkbox(compact ? "自动接受邀请" : "自动接受组队邀请", ref accept)) helpers.SetCommonParty(false, accept);
        if (!compact && ImGui.BeginTable("##CommonShout", 2, ImGuiTableFlags.SizingStretchSame))
        {
            DrawShoutButtons();
            ImGui.EndTable();
        }
    }

    private void DrawShoutButtons()
    {
        var snapshot = maps.Snapshot;
        var island = maps.Islands.FirstOrDefault(i => i.Territory == snapshot.CurrentTerritory);
        var point = snapshot.Elementals?.Where(p => p.Territory == island?.Territory && p.MapId == island?.MapId).MaxBy(p => p.LastSeen);
        MapDestination? destination = point != null && island != null
            ? new(island.Territory, island.MapId, MapProjection.MapToWorld(point.Position, island), "最后已知元灵") : null;
        ImGui.TableNextColumn();
        ImGui.BeginDisabled(destination == null);
        if (ImGui.Button("发送元灵坐标", new Vector2(-1, 0))) helpers.RunCommon(CommonAction.Elemental, destination);
        ImGui.EndDisabled();
        Button("发送求救", CommonAction.Rescue);
    }

    private void DrawNavigationSettings(bool compact)
    {
        var state = helpers.Snapshot;
        if (!compact) ImGui.TextUnformatted("导航");
        var owned = state.Mounts ?? [];
        var mountName = owned.FirstOrDefault(m => m.Id == state.SelectedMount)?.Name ?? (compact ? "选择坐骑" : "选择已拥有的坐骑");
        ImGui.BeginDisabled(state.Navigating);
        if (!compact) { ImGui.TextUnformatted("坐骑"); ImGui.SameLine(); }
        var comboWidth = ImGui.CalcTextSize("选择坐骑").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(compact ? Math.Min(comboWidth, ImGui.GetContentRegionAvail().X * .28f) : -1);
        if (ImGui.BeginCombo("##FollowMount", mountName))
        {
            foreach (var mount in owned)
            {
                ImGui.BeginDisabled(mount.Immobile);
                if (ImGui.Selectable(mount.Name + (mount.Immobile ? "（不可移动）" : "") + $"##Mount{mount.Id}", mount.Id == state.SelectedMount)) helpers.SelectMount(mount.Id);
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
        else if (compact && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(mountName);
        ImGui.EndDisabled();
        if (compact) ImGui.SameLine();
        else ImGui.TextWrapped(state.Destination?.Label ?? "尚无车头坐标。");
        ImGui.BeginDisabled(state.Destination == null || state.Navigating);
        if (ImGui.Button(compact ? "导航车头" : "开始导航到目的地")) helpers.Navigate();
        if (compact && state.Destination is { } goal && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(goal.Label);
        ImGui.EndDisabled();
        ImGui.SameLine();
        var status = compact ? state.NavigationStatus.ReplaceLineEndings(" ") : state.NavigationStatus;
        var statusWidth = ImGui.CalcTextSize(status).X;
        var stopWidth = ImGui.CalcTextSize("停止").X + ImGui.GetStyle().FramePadding.X * 2;
        if (!compact || state.Navigating && ImGui.GetContentRegionAvail().X >= stopWidth + ImGui.GetStyle().ItemSpacing.X + statusWidth)
        {
            ImGui.BeginDisabled(!state.Navigating);
            if (ImGui.Button("停止")) helpers.Stop();
            ImGui.EndDisabled();
            if (compact) ImGui.SameLine();
        }
        if (!compact)
        {
            ImGui.TextWrapped(status);
            return;
        }
        var available = new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(pos, pos + available, true);
        draw.AddText(pos + new Vector2(0, ImGui.GetStyle().FramePadding.Y), ImGui.GetColorU32(ImGuiCol.Text), status);
        draw.PopClipRect();
        ImGui.Dummy(available);
        if (statusWidth > available.X && ImGui.IsItemHovered()) ImGui.SetTooltip(status);
    }

    private void DrawDiscord()
    {
        var discord = helpers.DiscordSnapshot;
        ImGui.TextUnformatted("Discord 远程进岛");
        if (ImGui.Button(discord.Enabled ? "关闭 Discord 接入" : "开启 Discord 接入"))
            helpers.EnableDiscord(!discord.Enabled);
        ImGui.SameLine();
        ImGui.BeginDisabled(!discord.Active);
        if (ImGui.Button("停止 Discord 任务")) helpers.StopDiscord();
        ImGui.EndDisabled();

        var bridgeStatus = !discord.Enabled ? "接入已关闭"
            : discord.BotId == 0 ? "等待机器人桥接上报"
            : DateTimeOffset.UtcNow - discord.LastSeen <= TimeSpan.FromSeconds(45)
                ? $"已收到桥接心跳，Bot ID {discord.BotId}"
                : $"桥接心跳超时，最近 Bot ID {discord.BotId}";
        ImGui.TextWrapped(bridgeStatus);
        ImGui.TextWrapped(discord.Status);
        var partyRequest = discord.PartyRequest;
        var autoParty = discord.AutoParty;
        if (ImGui.Checkbox("锁定 FATE 后喊话组队", ref partyRequest))
            helpers.SetDiscordParty(partyRequest, autoParty);
        ImGui.SameLine();
        if (ImGui.Checkbox("任务期间自动组队", ref autoParty))
            helpers.SetDiscordParty(partyRequest, autoParty);
        ImGui.SetNextItemWidth(Math.Min(360, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("Discord 用户 ID", ref discordOwner, 24, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SetNextItemWidth(Math.Min(360, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("Bot Token", ref discordToken, 256, ImGuiInputTextFlags.Password);
        var useSaved = discord.CredentialsSaved && discordOwner.Length == 0 && discordToken.Length == 0;
        ImGui.BeginDisabled(!useSaved && (string.IsNullOrWhiteSpace(discordOwner) || string.IsNullOrWhiteSpace(discordToken)));
        if (ImGui.Button("连接 Discord Bot"))
        {
            helpers.StartDiscordBridge(discordOwner, discordToken);
            discordOwner = discordToken = "";
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!discord.CredentialsSaved);
        if (ImGui.Button("清除已保存凭据"))
        {
            helpers.ClearDiscordCredentials();
            discordOwner = discordToken = "";
        }
        ImGui.EndDisabled();
        if (discord.CredentialsSaved) ImGui.TextWrapped("已加密保存用户 ID 和 Token，两框留空即可连接。");
        if (!string.IsNullOrEmpty(discord.BridgeStatus)) ImGui.TextWrapped(discord.BridgeStatus);
        ImGui.TextWrapped("填你自己的 Discord 用户 ID 和开发者后台 Bot 页面的 Token。看到 Bot ID 后，私聊机器人发送「状态」。");
    }

    private void Button(string label, CommonAction action)
    {
        ImGui.TableNextColumn();
        if (ImGui.Button(label, new Vector2(-1, 0))) helpers.RunCommon(action);
    }
}
