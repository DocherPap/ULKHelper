using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Fates;
using ULKHelper.Weather;
using ULKHelper.Automation;

namespace ULKHelper.Windows;

internal interface IMapTextures
{
    ImTextureID? Get(string path, out string? error);
}

internal sealed class FateMapPage(IFateMapProvider provider, IMapTextures textures, IReadOnlyList<WeatherZone> weather, IAutomationHelpers? navigation = null)
{
    private int islandIndex;
    public void Load(SettingsStore settings, string key)
    {
        islandIndex = Math.Clamp(settings.Get(key + ".island", 0), 0, Math.Max(0, provider.Islands.Count - 1));
        follow = settings.Get(key + ".follow", true); showBunnies = settings.Get(key + ".bunnies", true); showLabels = settings.Get(key + ".labels", true);
        zoom = Math.Clamp(settings.Get(key + ".zoom", 1f), 1, 4); centre = settings.Get(key + ".centre", new Vector2(.5f));
        search = settings.Get(key + ".search", "");
    }
    public void Save(SettingsStore settings, string key)
    {
        settings.Put(key + ".island", islandIndex); settings.Put(key + ".follow", follow);
        settings.Put(key + ".bunnies", showBunnies); settings.Put(key + ".labels", showLabels);
        settings.Put(key + ".zoom", zoom); settings.Put(key + ".centre", centre); settings.Put(key + ".search", search);
    }
    private bool follow = true, showBunnies = true, showLabels = true, dragged;
    private string search = "";
    private ushort selected;
    private float minutesAgo;
    private float zoom = 1;
    private Vector2 centre = new(.5f);
    private DateTimeOffset nextTiming;
    private readonly Dictionary<ushort, FateTimingResult> timing = new();
    private readonly Dictionary<ushort, FateTimingResult> markerTiming = new();
    private bool notificationEnabled, notificationInitialized;
    private int notificationChannel;
    private string notificationTemplate = FateNotificationSettings.DefaultTemplate;
    private readonly TriggerSelection triggerSelection = new();

    public void DrawTriggerPlanner(IReadOnlyList<TriggerKillCount>? kills = null)
    {
        var snapshot = provider.Snapshot;
        if (provider.Islands.Count == 0) { ImGui.TextWrapped("地图数据尚未就绪。"); return; }
        if (follow)
        {
            var index = provider.Islands.Select((value, i) => (value, i)).FirstOrDefault(p => p.value.Territory == snapshot.CurrentTerritory);
            if (index.value != null && islandIndex != index.i) { islandIndex = index.i; ResetView(); }
        }
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        if (ImGui.Combo("##TriggerIsland", ref islandIndex, provider.Islands.Select(i => i.Name).ToArray(), provider.Islands.Count))
        { follow = false; ResetView(); }
        ImGui.SameLine(); ImGui.Checkbox("跟随岛屿", ref follow);
        ImGui.SameLine(); ImGui.Checkbox("简称", ref showLabels);
        var island = provider.Islands[islandIndex];
        triggerSelection.SetIsland(island.Territory);
        var now = DateTimeOffset.UtcNow;
        UpdateTiming(island, snapshot, now);
        var points = island.Fates.Where(f => !f.Seed.Bunny).ToArray();
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        var reserved = Math.Min(availableHeight * .38f, ImGui.GetTextLineHeightWithSpacing() * (3 + triggerSelection.Ids.Count * 2)) + 12;
        var mapHeight = Math.Max(170, availableHeight - reserved);
        if (ImGui.BeginChild("##TriggerIslandMap", new Vector2(0, mapHeight)))
            DrawMap(island, points, snapshot, now, triggerSelection.Ids.ToHashSet(), true);
        ImGui.EndChild();
        var current = island.Fates.FirstOrDefault(f => f.Seed.Id == selected);
        ImGui.Separator();
        ImGui.TextUnformatted($"自动触发列表（{triggerSelection.Ids.Count}/3）");
        ImGui.SameLine(); ImGui.BeginDisabled(); ImGui.SmallButton("开始触发（未完成）"); ImGui.EndDisabled();
        if (current != null)
        {
            ImGui.TextUnformatted($"已选：{current.Seed.ShortName}");
            ImGui.SameLine();
            ImGui.BeginDisabled(triggerSelection.Ids.Count >= 3 || triggerSelection.Ids.Contains(selected));
            if (ImGui.SmallButton("加入列表")) triggerSelection.Add(current);
            ImGui.EndDisabled();
        }
        else ImGui.TextDisabled("点击地图上的NM，再加入列表（最多3个）。");
        if (ImGui.BeginChild("##TriggerPlanRows", Vector2.Zero))
        {
            foreach (var id in triggerSelection.Ids.ToArray())
            {
                var point = island.Fates.First(f => f.Seed.Id == id);
                ImGui.PushID(id);
                ImGui.TextUnformatted(point.Seed.ShortName);
                ImGui.SameLine();
                if (ImGui.SmallButton("移除")) triggerSelection.Remove(id);
                ImGui.TextWrapped("触发：" + point.Trigger);
                ImGui.TextDisabled(snapshot.CurrentTerritory == island.Territory
                    ? $"本地观测击杀：{kills?.FirstOrDefault(k => k.FateId == id)?.Count ?? 0}"
                    : "本地观测击杀：未获取");
                ImGui.Separator(); ImGui.PopID();
            }
            ImGui.TextDisabled("自动触发执行待实现。");
        }
        ImGui.EndChild();
    }

    private void UpdateTiming(IslandMap island, FateMapSnapshot snapshot, DateTimeOffset now)
    {
        if (now < nextTiming) return;
        timing.Clear(); markerTiming.Clear();
        foreach (var point in island.Fates)
        {
            snapshot.Records.TryGetValue(point.Seed.Id, out var record);
            timing[point.Seed.Id] = FateTiming.Describe(point, record, weather.FirstOrDefault(w => w.TerritoryId == island.Territory), now);
            markerTiming[point.Seed.Id] = FateTiming.ForMarker(point, record, timing[point.Seed.Id], now);
        }
        nextTiming = now.AddSeconds(1);
    }

    public void Draw()
    {
        var snapshot = provider.Snapshot;
        if (provider.Islands.Count == 0) { ImGui.TextWrapped(snapshot.Status); return; }
        if (follow)
        {
            var current = provider.Islands.Select((value, index) => (value, index)).FirstOrDefault(p => p.value.Territory == snapshot.CurrentTerritory);
            if (current.value != null && islandIndex != current.index) { islandIndex = current.index; ResetView(); }
        }
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("常风之地").X + ImGui.GetFrameHeight() * 2);
        if (ImGui.Combo("岛屿", ref islandIndex, provider.Islands.Select(i => i.Name).ToArray(), provider.Islands.Count))
        { follow = false; ResetView(); }
        ImGui.SameLine(); ImGui.Checkbox("跟随当前岛屿", ref follow);
        ImGui.SameLine(); ImGui.Checkbox("含幸福兔", ref showBunnies);
        ImGui.SameLine(); ImGui.Checkbox("显示简称", ref showLabels);
        if (!notificationInitialized)
        {
            var settings = snapshot.Notifications ?? FateNotificationSettings.Default;
            notificationEnabled = settings.Enabled; notificationChannel = (int)settings.Channel; notificationTemplate = settings.Template;
            notificationInitialized = true;
        }
        var notificationChanged = ImGui.Checkbox("发送通知频道", ref notificationEnabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("跨服通信贝 8").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Combo("##FateNotificationChannel", ref notificationChannel, FateNotificationText.ChannelNames, FateNotificationText.ChannelNames.Length))
        { notificationEnabled = false; notificationChanged = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        notificationChanged |= ImGui.InputTextWithHint("##FateNotificationTemplate", "通知内容", ref notificationTemplate, 512);
        if (notificationChanged) provider.ConfigureNotifications(new(notificationEnabled, (FateNotificationChannel)notificationChannel, notificationTemplate));
        ImGui.TextDisabled("<位置> 代表位置，<名字> 代表 FATE 缩写。开启后仅通知新出现的 FATE。");
        if (FateNotificationText.IsLocal((FateNotificationChannel)notificationChannel)) ImGui.TextDisabled("当前频道仅在本机显示。");
        if (notificationEnabled && snapshot.NotificationStatus != null) ImGui.TextWrapped(snapshot.NotificationStatus);
        if (navigation != null)
        {
            if (ImGui.Button("自动前往入口营地")) navigation.GoToEntrance();
            ImGui.SameLine(); ImGui.BeginDisabled(!navigation.Snapshot.Navigating);
            if (ImGui.Button("停止移动")) navigation.Stop();
            ImGui.EndDisabled();
            ImGui.TextWrapped(navigation.Snapshot.NavigationStatus);
        }
        if (snapshot.CrowdsourceStatus != null)
            ImGui.TextWrapped($"{(snapshot.RoomId != 0 ? $"房间 {snapshot.RoomId} · " : "")}{snapshot.CrowdsourceStatus}");
        var island = provider.Islands[islandIndex];
        var now = DateTimeOffset.UtcNow;
        if (now >= nextTiming)
        {
            timing.Clear();
            markerTiming.Clear();
            foreach (var point in island.Fates)
            {
                snapshot.Records.TryGetValue(point.Seed.Id, out var record);
                timing[point.Seed.Id] = FateTiming.Describe(point, record, weather.FirstOrDefault(w => w.TerritoryId == island.Territory), now);
                markerTiming[point.Seed.Id] = FateTiming.ForMarker(point, record, timing[point.Seed.Id], now);
            }
            nextTiming = now.AddSeconds(1);
        }
        var points = island.Fates.Where(f => (showBunnies || !f.Seed.Bunny)
            && (search.Length == 0 || f.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || f.Seed.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        LayoutSettings.RememberTable("##FateMapLayout");
        if (!ImGui.BeginTable("##FateMapLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            ImGui.TableSetupColumn("地图", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("点位与计时", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("##IslandMap", Vector2.Zero)) DrawMap(island, points, snapshot, now);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##FateDetails", Vector2.Zero)) DrawList(island, points, snapshot, now);
            ImGui.EndChild();
        }
        finally { ImGui.EndTable(); }
    }

    private void ResetView() { centre = new(.5f); zoom = 1; selected = 0; timing.Clear(); markerTiming.Clear(); nextTiming = default; }

    private void DrawMap(IslandMap island, IReadOnlyList<FateDefinition> points, FateMapSnapshot snapshot, DateTimeOffset now, IReadOnlySet<ushort>? planned = null, bool compactLabels = false)
    {
        if (ImGui.SmallButton("还原视图")) { centre = new(.5f); zoom = 1; }
        ImGui.SameLine(); ImGui.TextDisabled("滚轮缩放 · 拖动平移 · 点击点位选择");
        var space = ImGui.GetContentRegionAvail();
        var side = Math.Max(100, Math.Min(space.X, space.Y - ImGui.GetTextLineHeightWithSpacing()));
        var pos = ImGui.GetCursorScreenPos() + new Vector2(Math.Max(0, (space.X - side) / 2), 0);
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##MapCanvas", new(side), ImGuiButtonFlags.MouseButtonLeft);
        var hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        if (hovered && io.MouseWheel != 0)
        {
            var focus = (io.MousePos - pos) / side;
            var previous = 1 / zoom;
            zoom = Math.Clamp(zoom * MathF.Pow(1.25f, io.MouseWheel), 1, 4);
            centre += (focus - new Vector2(.5f)) * (previous - 1 / zoom);
        }
        if (ImGui.IsItemActivated()) dragged = false;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        { centre -= io.MouseDelta / side / zoom; dragged = true; }
        var half = .5f / zoom;
        centre = Vector2.Clamp(centre, new(half), new(1 - half));
        var uvMin = centre - new Vector2(half);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(pos, pos + new Vector2(side), true);
        draw.AddRectFilled(pos, pos + new Vector2(side), 0xFF252C2D);
        var texture = textures.Get(island.TexturePath, out var error);
        if (texture is { } handle) draw.AddImage(handle, pos, pos + new Vector2(side), uvMin, centre + new Vector2(half));
        else draw.AddText(pos + new Vector2(8), 0xFFFFFFFF, error ?? "地图载入中…");
        ushort? hoveredPoint = null;
        var nearest = float.MaxValue;
        var labels = new List<(Vector2 Min, Vector2 Max)>();
        foreach (var fate in points)
        {
            var uv = MapProjection.ToUv(new(fate.Seed.X, fate.Seed.Y), island.SizeFactor);
            var marker = pos + (uv - uvMin) * zoom * side;
            var status = timing.GetValueOrDefault(fate.Seed.Id) ?? new FateTimingResult("未知", null, FateTiming.UnknownColor);
            var radius = Math.Max(5, ImGui.GetFontSize() * .38f);
            draw.AddCircleFilled(marker, radius, status.Color);
            draw.AddCircle(marker, radius + (selected == fate.Seed.Id ? 3 : 1), selected == fate.Seed.Id ? 0xFFFFFFFF : 0xFF202020, 16, 2);
            if (planned?.Contains(fate.Seed.Id) == true) draw.AddCircle(marker, radius + 5, 0xFFFFCA65, 16, 2);
            var markerState = markerTiming.GetValueOrDefault(fate.Seed.Id) ?? status;
            var countdown = markerState.Until is { } until ? $"{markerState.Text} {FateTiming.Countdown(until, now)}" : markerState.Text;
            if (countdown == "未知 · 未记录") countdown = "未记录";
            if (countdown == "条件可用 · 待触发") countdown = "待触发";
            var label = compactLabels ? showLabels || selected == fate.Seed.Id ? fate.Seed.ShortName : ""
                : showLabels || selected == fate.Seed.Id ? $"{fate.Seed.ShortName}\n{countdown}" : countdown;
            if (label.Length > 0 && marker.X >= pos.X && marker.Y >= pos.Y && marker.X <= pos.X + side && marker.Y <= pos.Y + side)
            {
                var fontSize = Math.Max(12, ImGui.GetFontSize() * .8f);
                var size = ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize()) + new Vector2(6, 4);
                var labelPos = marker + new Vector2(10, -size.Y / 2);
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var sideOffset = attempt % 2 == 0 ? 10 : -size.X - 10;
                    var vertical = (attempt / 2) switch { 0 => -size.Y / 2, 1 => -size.Y - 10, 2 => 10, 3 => -size.Y * 2 - 10, 4 => size.Y + 10, _ => -size.Y / 2 };
                    var candidate = Vector2.Clamp(marker + new Vector2(sideOffset, vertical), pos, Vector2.Max(pos, pos + new Vector2(side) - size));
                    labelPos = candidate;
                    if (!labels.Any(r => candidate.X < r.Max.X && candidate.X + size.X > r.Min.X && candidate.Y < r.Max.Y && candidate.Y + size.Y > r.Min.Y)) break;
                }
                labels.Add((labelPos, labelPos + size));
                draw.AddLine(marker, Vector2.Clamp(marker, labelPos, labelPos + size), status.Color);
                draw.AddRectFilled(labelPos, labelPos + size, 0xCE181818, 3);
                draw.AddText(ImGui.GetFont(), fontSize, labelPos + new Vector2(3, 2), markerState.Color, label);
            }
            var distance = Vector2.DistanceSquared(marker, io.MousePos);
            if (hovered && distance < Math.Max(12, radius * 2) * Math.Max(12, radius * 2) && distance < nearest)
            { nearest = distance; hoveredPoint = fate.Seed.Id; }
        }
        if (snapshot.CurrentTerritory == island.Territory && snapshot.PlayerMapPosition is { } player)
        {
            var p = pos + (MapProjection.ToUv(player, island.SizeFactor) - uvMin) * side * zoom;
            draw.AddCircleFilled(p, 5, 0xFFFFD070); draw.AddCircle(p, 7, 0xFFFFFFFF, 16, 2);
        }
        draw.PopClipRect();
        if (hoveredPoint is { } id)
        {
            var fate = points.First(p => p.Seed.Id == id);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Lv.{fate.Level} {fate.Name}");
            ImGui.TextUnformatted($"({fate.Seed.X:0.0}, {fate.Seed.Y:0.0}) · {TimingText(id, now)}");
            ImGui.TextUnformatted(fate.Trigger);
            ImGui.EndTooltip();
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !dragged) selected = id;
        }
        ImGui.TextDisabled($"缩放 {zoom:0.0}×　点位 {points.Count}　蓝色圆点：自己");
    }

    private string TimingText(ushort id, DateTimeOffset now)
    {
        if (!timing.TryGetValue(id, out var value)) return "未知 · 未记录";
        return value.Until is { } end ? $"{value.Text} {FateTiming.Countdown(end, now)}" : value.Text;
    }

    private void DrawList(IslandMap island, IReadOnlyList<FateDefinition> points, FateMapSnapshot snapshot, DateTimeOffset now)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##FateSearch", "搜索 FATE / 简称", ref search, 100);
        var definition = island.Fates.FirstOrDefault(f => f.Seed.Id == selected);
        if (definition != null)
        {
            ImGui.TextWrapped(definition.Name);
            ImGui.TextWrapped($"触发：{definition.Trigger}");
            if (ImGui.Button("在游戏地图标记")) provider.Mark(definition);
            if (navigation != null)
            {
                ImGui.SameLine(); ImGui.BeginDisabled(snapshot.CurrentTerritory != island.Territory || navigation.Snapshot.Navigating);
                if (ImGui.Button("自动前往")) navigation.NavigateTo(new(island.Territory, island.MapId,
                    MapProjection.MapToWorld(new(definition.Seed.X, definition.Seed.Y), island), definition.Seed.ShortName));
                ImGui.EndDisabled();
            }
            ImGui.TextWrapped($"参考冷却：出现后 {definition.Seed.CooldownMinutes:0.#} 分钟。仅估计；需要刷怪 / 天气等条件。");
            var condition = definition.Seed.WeatherId == 0 ? "不限天气" : weather.FirstOrDefault(w => w.TerritoryId == island.Territory)?.Chances
                .FirstOrDefault(c => c.WeatherId == definition.Seed.WeatherId)?.Name ?? "特定天气";
            ImGui.TextWrapped($"条件：{condition}{(definition.Seed.Night ? "，ET 19:00–06:00" : "")}{(definition.Seed.Bunny ? "；还受寻宝持有状态影响" : "")}。");
            snapshot.Records.TryGetValue(selected, out var record);
            if (record != null) ImGui.TextWrapped($"{(record.Crowdsourced ? "进岛众包快照" : record.Manual ? "手动补记" : "本次观察")}：{record.Appeared.ToLocalTime():MM-dd HH:mm:ss}");
            ImGui.BeginDisabled(snapshot.CurrentTerritory != island.Territory || record is { Active: true });
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
            ImGui.InputFloat("分钟前出现", ref minutesAgo, 1, 5, "%.1f");
            minutesAgo = Math.Clamp(minutesAgo, 0, 240);
            if (ImGui.Button("补记出现时间")) { provider.RecordAppearance(selected, minutesAgo); nextTiming = default; }
            ImGui.SameLine();
            if (ImGui.Button("清除此记录")) { provider.ClearRecord(selected); nextTiming = default; }
            ImGui.EndDisabled();
            ImGui.Separator();
        }
        if (ImGui.BeginChild("##FateTimers", Vector2.Zero))
        {
            foreach (var fate in points)
            {
                var seed = fate.Seed;
                ImGui.PushID(seed.Id);
                if (ImGui.Selectable($"Lv.{fate.Level} {seed.ShortName}  ({seed.X:0.0}, {seed.Y:0.0})", selected == seed.Id)) selected = seed.Id;
                if (ImGui.IsItemHovered()) { ImGui.BeginTooltip(); ImGui.TextUnformatted(fate.Name); ImGui.TextUnformatted(fate.Trigger); ImGui.EndTooltip(); }
                ImGui.TextWrapped(fate.Trigger);
                var status = timing.GetValueOrDefault(seed.Id);
                ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(status?.Color ?? FateTiming.UnknownColor), TimingText(seed.Id, now));
                ImGui.Separator(); ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }
}
