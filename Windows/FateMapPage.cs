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

internal sealed class FateMapPage(IFateMapProvider provider, IMapTextures textures, IReadOnlyList<WeatherZone> weather, IAutomationHelpers? navigation = null, BunnyPage? bunnies = null)
{
    private int islandIndex;
    private int pullBatch = 1, abandonStep = 2;
    private bool showMobs = true;
    private bool useTriggerRotation = true, useTriggerBmr = true;
    private (ushort Fate, int Route) nameDraftKey;
    private string nameDraft = "";
    private readonly Dictionary<ushort, TriggerMode> triggerModes = new();
    private TriggerMode Mode(ushort id) => triggerModes.GetValueOrDefault(id, AutoTriggerRules.DefaultMode(id));
    private bool CanAddTrigger(FateDefinition candidate, IslandMap island)
    {
        var level = navigation?.TriggerSnapshot.Level;
        return level != null && candidate.Trigger.Contains("击杀 ") && !triggerSelection.Ids.Contains(candidate.Seed.Id)
            && triggerSelection.Ids.Count + 1 <= AutoTriggerRules.Capacity(level.Value,
                island.Fates.Where(f => triggerSelection.Ids.Contains(f.Seed.Id)).Select(f => (int)f.Level).Append(candidate.Level));
    }
    public void Load(SettingsStore settings, string key)
    {
        islandIndex = Math.Clamp(settings.Get(key + ".island", 0), 0, Math.Max(0, provider.Islands.Count - 1));
        showBunnies = settings.Get(key + ".bunnies", true); showLabels = settings.Get(key + ".labels", true);
        showElementals = settings.Get(key + ".elementals", true);
        zoom = Math.Clamp(settings.Get(key + ".zoom", 1f), 1, 4); centre = settings.Get(key + ".centre", new Vector2(.5f));
        search = settings.Get(key + ".search", "");
        pullBatch = Math.Clamp(settings.Get("trigger.batch", 1), 1, 21); abandonStep = Math.Clamp(settings.Get("trigger.abandon", 2), 1, 7);
        showMobs = settings.Get("trigger.showMobs", true);
        useTriggerRotation = settings.Get("trigger.useRotation", true);
        useTriggerBmr = settings.Get("trigger.useBmr", true);
        foreach (var p in settings.Get("trigger.modes", new Dictionary<ushort, TriggerMode>()))
            if (Enum.IsDefined(p.Value)) triggerModes[p.Key] = p.Value;
    }
    public void Save(SettingsStore settings, string key)
    {
        settings.Put(key + ".island", islandIndex);
        settings.Put(key + ".bunnies", showBunnies); settings.Put(key + ".labels", showLabels);
        settings.Put(key + ".elementals", showElementals);
        settings.Put("trigger.batch", pullBatch); settings.Put("trigger.abandon", abandonStep);
        settings.Put("trigger.showMobs", showMobs); settings.Put("trigger.modes", triggerModes);
        settings.Put("trigger.useRotation", useTriggerRotation); settings.Put("trigger.useBmr", useTriggerBmr);
        settings.Put(key + ".zoom", zoom); settings.Put(key + ".centre", centre); settings.Put(key + ".search", search);
    }
    private bool showBunnies = true, showLabels = true, showElementals = true, dragged;
    private (uint Territory, DateTimeOffset? StartedAt) lastEntry;
    private string search = "";
    private ushort selected;
    private ushort nearbyRoutePreview;
    private MobArea? selectedArea;
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

    private void DrawTriggerPlanner(IslandMap island, FateMapSnapshot snapshot)
    {
        var points = island.Fates.Where(f => !f.Seed.Bunny).ToArray();
        var current = island.Fates.FirstOrDefault(f => f.Seed.Id == selected);
        var auto = navigation?.TriggerSnapshot ?? AutoTriggerSnapshot.Empty;
        var cap = auto.Level is { } level ? AutoTriggerRules.Capacity(level, island.Fates.Where(f => triggerSelection.Ids.Contains(f.Seed.Id)).Select(f => (int)f.Level)) : 0;
        ImGui.TextUnformatted($"自动触发列表（{triggerSelection.Ids.Count}/{cap}） 元素等级：{auto.Level?.ToString() ?? "未获取"}");
        ImGui.SetNextItemWidth((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2);
        ImGui.SliderInt("##PullBatch", ref pullBatch, 1, 21, pullBatch == 21 ? "每批：无限" : "每批：%d 只");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderInt("##Abandon", ref abandonStep, 1, 7, abandonStep == 7 ? "放弃阈值：无限" : $"放弃阈值：{abandonStep * 50} 只");
        FateDefinition? nearby = null;
        if (triggerSelection.Ids.Count == 0 && (current == null || !CanAddTrigger(current, island))
            && snapshot.CurrentTerritory == island.Territory && snapshot.PlayerMapPosition is { } player)
        {
            var position = MapProjection.MapToWorld(player, island);
            nearby = points.Where(f => CanAddTrigger(f, island)
                    && snapshot.Records.GetValueOrDefault(f.Seed.Id)?.Active != true)
                .Select(f =>
                {
                    var distance = NavigationRules.Distance(position, MapProjection.MapToWorld(new(f.Seed.X, f.Seed.Y), island));
                    var special = Mode(f.Seed.Id) == TriggerMode.Recorded;
                    var route = auto.SelectedRoute(f.Seed.Id);
                    if (special && route.Length > 0)
                        distance = Math.Min(distance, NavigationRules.Distance(position, route[0]));
                    return (Fate: f, Distance: distance, Special: special);
                })
                .Where(entry => entry.Distance <= 100)
                .OrderByDescending(entry => entry.Special).ThenBy(entry => entry.Distance)
                .Select(entry => entry.Fate)
                .FirstOrDefault();
        }
        var routeTarget = nearby ?? current;
        nearbyRoutePreview = nearby?.Seed.Id ?? 0;
        var canStart = triggerSelection.Ids.Count > 0 ? triggerSelection.Ids.Count <= cap
            : routeTarget != null && CanAddTrigger(routeTarget, island)
                && (Mode(routeTarget.Seed.Id) != TriggerMode.Recorded || auto.SelectedRoute(routeTarget.Seed.Id).Length >= 2);
        ImGui.BeginDisabled(!auto.Active && (navigation == null || navigation.Snapshot.Navigating || !canStart || snapshot.CurrentTerritory != island.Territory));
        var buttonLabel = auto.Active ? "停止自动触发" : nearby != null ? $"自动触发 · {nearby.Seed.ShortName}" : "自动触发";
        if (ImGui.Button(buttonLabel + "###AutoTriggerStartStop", new Vector2(-1, ImGui.GetFrameHeight() * 1.6f)))
        {
            if (auto.Active) navigation?.Stop();
            else
            {
                if (nearby == null && triggerSelection.Ids.Count == 0 && current != null) triggerSelection.Add(current);
                var ids = nearby != null ? new[] { nearby.Seed.Id } : triggerSelection.Ids.ToArray();
                navigation?.StartTrigger(ids.Select(id =>
                {
                    var fate = island.Fates.First(f => f.Seed.Id == id);
                    var centre = MapProjection.MapToWorld(new(fate.Seed.X, fate.Seed.Y), island);
                    return new TriggerPlan(id, fate.Level, new(island.Territory, island.MapId, centre, fate.Seed.ShortName), Mode(id), auto.SelectedRoute(id));
                }).ToArray(), pullBatch, abandonStep, useTriggerRotation, useTriggerBmr);
            }
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(auto.Active);
        if (routeTarget != null && CanAddTrigger(routeTarget, island))
            DrawTriggerMode(routeTarget.Seed.Id, "触发模式");
        if (routeTarget != null && Mode(routeTarget.Seed.Id) == TriggerMode.Recorded)
            DrawTriggerRoutes(routeTarget.Seed.Id, auto, island, snapshot.CurrentTerritory == island.Territory);
        ImGui.Checkbox("自动循环", ref useTriggerRotation);
        ImGui.SameLine();
        ImGui.Checkbox("BMR AI", ref useTriggerBmr);
        if (current != null)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(!CanAddTrigger(current, island));
            if (ImGui.SmallButton($"加入列表 · {current.Seed.ShortName}###AddTrigger")) triggerSelection.Add(current);
            ImGui.EndDisabled();
        }
        ImGui.TextUnformatted(auto.Status);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(auto.Status);
        ImGui.EndDisabled();
    }

    private void DrawTriggerQueue(IslandMap island, FateMapSnapshot snapshot, IReadOnlyList<TriggerKillCount>? kills)
    {
        if (triggerSelection.Ids.Count == 0 || !ImGui.CollapsingHeader($"触发队列（{triggerSelection.Ids.Count}）###TriggerQueue")) return;
        ImGui.BeginDisabled(navigation?.TriggerSnapshot.Active == true);
        {
            foreach (var id in triggerSelection.Ids.ToArray())
            {
                var point = island.Fates.First(f => f.Seed.Id == id);
                ImGui.PushID(id);
                ImGui.TextUnformatted(point.Seed.ShortName);
                ImGui.SameLine();
                if (ImGui.SmallButton("移除")) triggerSelection.Remove(id);
                DrawTriggerMode(id, "##TriggerMode");
                if (Mode(id) == TriggerMode.Recorded && selected != id && ImGui.SmallButton("选择路线")) selected = id;
                ImGui.TextWrapped("触发：" + point.Trigger);
                ImGui.TextDisabled(snapshot.CurrentTerritory == island.Territory
                    ? $"本地观测击杀：{kills?.FirstOrDefault(k => k.FateId == id)?.Count ?? 0}"
                    : "本地观测击杀：未获取");
                ImGui.Separator(); ImGui.PopID();
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawTriggerMode(ushort id, string label)
    {
        var mode = (int)Mode(id);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo(label, ref mode, new[] { "普通：攻击拉怪", "特殊：骑乘记录路线" }, 2))
        {
            triggerModes[id] = (TriggerMode)mode;
            selected = id;
        }
    }

    private void DrawTriggerRoutes(ushort id, AutoTriggerSnapshot auto, IslandMap island, bool canRecord)
    {
        var set = auto.Routes.GetValueOrDefault(id);
        var count = set?.Paths.Length ?? 0;
        ImGui.PushID($"routes-{id}");
        if (set != null)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##Route", $"{set.NameAt(set.Selected)}（{set.Current.Length} 点）"))
            {
                for (var i = 0; i < count; i++)
                {
                    var chosen = set.Selected == i;
                    if (ImGui.Selectable($"{set.NameAt(i)}（{set.Paths[i].Length} 点）###Route{i}", chosen))
                    {
                        selected = id;
                        navigation?.SelectTriggerRoute(id, i);
                        FrameTriggerRoute(set.Paths[i], island);
                    }
                    if (chosen) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            var draftKey = (id, set.Selected);
            if (nameDraftKey != draftKey)
            {
                nameDraftKey = draftKey;
                nameDraft = set.Names?.ElementAtOrDefault(set.Selected) ?? "";
            }
            var saveWidth = ImGui.CalcTextSize("保存名称").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(Math.Max(60, ImGui.GetContentRegionAvail().X - saveWidth - ImGui.GetStyle().ItemSpacing.X));
            ImGui.InputTextWithHint("##RouteName", $"路线{set.Selected + 1}", ref nameDraft, 64);
            ImGui.SameLine();
            if (ImGui.Button("保存名称")) navigation?.RenameTriggerRoute(id, set.Selected, nameDraft);
        }
        ImGui.BeginDisabled(!canRecord || count >= TriggerRouteSet.MaxRoutes);
        if (ImGui.SmallButton("新建路线 / 记录起点"))
        {
            selected = id;
            navigation?.RecordTriggerPoint(id, true);
        }
        ImGui.EndDisabled();
        if (set != null)
        {
            ImGui.BeginDisabled(!canRecord);
            ImGui.SameLine();
            if (ImGui.SmallButton("追加途径点 / 终点"))
            {
                selected = id;
                navigation?.RecordTriggerPoint(id);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("删除当前路线"))
            {
                nameDraftKey = default;
                navigation?.ClearTriggerRoute(id);
            }
            ImGui.EndDisabled();
        }
        ImGui.PopID();
    }

    private void FrameTriggerRoute(Vector3[] route, IslandMap island)
    {
        if (route.Length == 0) return;
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        foreach (var point in route)
        {
            var uv = MapProjection.ToUv(MapProjection.WorldToMap(point, island), island.SizeFactor);
            min = Vector2.Min(min, uv);
            max = Vector2.Max(max, uv);
        }
        var paddedHalf = new Vector2(.475f / zoom);
        var viewMin = centre - paddedHalf;
        var viewMax = centre + paddedHalf;
        if (min.X >= viewMin.X && min.Y >= viewMin.Y && max.X <= viewMax.X && max.Y <= viewMax.Y) return;
        var extent = max - min;
        zoom = Math.Clamp(Math.Min(zoom, .9f / Math.Max(.01f, Math.Max(extent.X, extent.Y))), 1f, 4f);
        centre = (min + max) * .5f;
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

    public void Draw() => Draw(null);

    public void Draw(AutomationPage? automation)
    {
        var snapshot = provider.Snapshot;
        if (provider.Islands.Count == 0) { ImGui.TextWrapped(snapshot.Status); return; }
        if (bunnies?.ConsumeReset() == true) ResetView();
        var entry = (snapshot.CurrentTerritory, snapshot.EntryStartedAt);
        if (lastEntry != entry)
        {
            lastEntry = entry;
            var current = provider.Islands.Select((value, index) => (value, index))
                .FirstOrDefault(p => p.value.Territory == snapshot.CurrentTerritory);
            if (current.value != null && islandIndex != current.index) { islandIndex = current.index; ResetView(); }
        }
        LayoutSettings.RememberTable("##FateMapLayout");
        if (!ImGui.BeginTable("##FateMapLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            ImGui.TableSetupColumn("地图", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("详情与自动化", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var now = DateTimeOffset.UtcNow;
            var showMap = ImGui.BeginChild("##IslandMap", Vector2.Zero);
            if (showMap)
            {
                ImGui.SetNextItemWidth(ImGui.CalcTextSize("常风之地").X + ImGui.GetFrameHeight() * 2);
                if (ImGui.Combo("岛屿", ref islandIndex, provider.Islands.Select(i => i.Name).ToArray(), provider.Islands.Count))
                    ResetView();
                ImGui.SameLine(); ImGui.Checkbox("幸福兔", ref showBunnies);
                ImGui.SameLine(); ImGui.Checkbox("简称", ref showLabels);
                if (bunnies != null) { ImGui.SameLine(); bunnies.DrawVisibilityToggle(); }
                ImGui.SameLine(); ImGui.Checkbox("元灵", ref showElementals);
                ImGui.SameLine(); ImGui.Checkbox("小怪区域", ref showMobs);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("显示已观测小怪区域");
                if (ImGui.CollapsingHeader("FATE 通知设置")) DrawNotifications(snapshot);
            }
            var island = provider.Islands[islandIndex];
            triggerSelection.SetIsland(island.Territory);
            UpdateTiming(island, snapshot, now);
            var points = island.Fates.Where(f => showBunnies || !f.Seed.Bunny).ToArray();
            if (showMap) DrawMap(island, points, snapshot, now, triggerSelection.Ids.ToHashSet());
            ImGui.EndChild();

            ImGui.TableNextColumn();
            const ImGuiWindowFlags pinned = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (ImGui.BeginChild("##FateDetails", Vector2.Zero, false, pinned))
            {
                var itemSpacing = ImGui.GetStyle().ItemSpacing;
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(itemSpacing.X, Math.Min(itemSpacing.Y, 2)));
                var textLine = ImGui.GetTextLineHeightWithSpacing();
                var bunnyHeight = bunnies == null ? 0 : ImGui.GetFrameHeightWithSpacing() * 3 + textLine * 3 + 8;
                var reservedLines = 5.7f + (navigation != null ? 10.7f : 0)
                    + (automation != null && navigation != null ? 4.8f : 0);
                var line = Math.Min(textLine, Math.Max(1, (ImGui.GetContentRegionAvail().Y - bunnyHeight - textLine * 3 - 18) / reservedLines));
                if (ImGui.BeginChild("##SelectedFateDetails", new Vector2(0, line * 5.7f), false, pinned))
                    DrawSelectedFate(island, snapshot);
                ImGui.EndChild();
                ImGui.Separator();
                if (navigation != null)
                {
                    if (ImGui.BeginChild("##PinnedTrigger", new Vector2(0, line * 10.7f), false, pinned))
                        DrawTriggerPlanner(island, snapshot);
                    ImGui.EndChild();
                }
                if (automation != null && navigation != null)
                {
                    ImGui.Separator();
                    if (ImGui.BeginChild("##PinnedFollow", new Vector2(0, line * 4.8f), false, pinned))
                        automation.DrawFollow(navigation.Snapshot);
                    ImGui.EndChild();
                }
                if (bunnies != null)
                {
                    ImGui.Separator();
                    if (ImGui.BeginChild("##PinnedBunny", new Vector2(0, bunnyHeight), false, pinned))
                        bunnies.DrawSummary(island, navigation);
                    ImGui.EndChild();
                }
                ImGui.Separator();
                ImGui.PopStyleVar();
                if (ImGui.BeginChild("##ScrollableMapLists", Vector2.Zero))
                {
                    if (ImGui.CollapsingHeader("FATE 列表")) DrawList(points, now);
                    DrawTriggerQueue(island, snapshot, navigation?.Snapshot.KillCounts);
                    if (automation != null && navigation != null)
                    {
                        if (ImGui.CollapsingHeader($"岛民（{navigation.Snapshot.Players.Count}）###Residents"))
                            automation.DrawResidents(navigation.Snapshot);
                        automation.DrawHistory(navigation.Snapshot);
                    }
                }
                ImGui.EndChild();
            }
            ImGui.EndChild();
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawNotifications(FateMapSnapshot snapshot)
    {
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
        ImGui.TextWrapped("<位置> 代表位置，<名字> 代表 FATE 缩写。开启后仅通知新出现的 FATE。");
        if (notificationEnabled && snapshot.NotificationStatus is {} notificationStatus && notificationStatus!="等待进岛建立活动基线。")
            ImGui.TextWrapped(notificationStatus);
        if (snapshot.CrowdsourceStatus != null)
            ImGui.TextWrapped($"{(snapshot.RoomId != 0 ? $"房间 {snapshot.RoomId} · " : "")}{snapshot.CrowdsourceStatus}");
    }

    private void ResetView() { centre = new(.5f); zoom = 1; selected = 0; selectedArea = null; timing.Clear(); markerTiming.Clear(); nextTiming = default; }

    private void DrawMap(IslandMap island, IReadOnlyList<FateDefinition> points, FateMapSnapshot snapshot, DateTimeOffset now, IReadOnlySet<ushort>? planned = null, bool compactLabels = false)
    {
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
        MobArea? hoveredArea = null;
        var nearest = float.MaxValue;
        var labels = new List<(Vector2 Min, Vector2 Max)>();
        if (showMobs && navigation != null && snapshot.CurrentTerritory == island.Territory)
        {
            foreach (var area in navigation.TriggerSnapshot.Areas)
            {
                var uv = MapProjection.ToUv(MapProjection.WorldToMap(area.Position, island), island.SizeFactor);
                var marker = pos + (uv - uvMin) * zoom * side;
                var extent = Math.Max(4, 10 * island.SizeFactor / 100f / 2048 * zoom * side);
                var color = ImGui.ColorConvertFloat4ToU32(new Vector4(.3f + area.FateId % 3 * .3f, .3f + area.FateId % 5 * .15f, .3f + area.FateId % 7 * .1f, .4f));
                draw.AddRectFilled(marker - new Vector2(extent), marker + new Vector2(extent), color);
                if (hovered && Math.Abs(io.MousePos.X - marker.X) <= extent && Math.Abs(io.MousePos.Y - marker.Y) <= extent)
                { hoveredPoint = area.FateId; hoveredArea = area; }
            }
        }
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
            { nearest = distance; hoveredPoint = fate.Seed.Id; hoveredArea = null; }
        }
        bunnies?.DrawOverlay(island,pos,side,zoom,uvMin,hovered&&hoveredPoint==null,dragged);
        ElementalMapPoint? hoveredElemental = null;
        if (showElementals && snapshot.Elementals is { } elementals)
        {
            foreach (var elemental in elementals)
            {
                if (elemental.Territory != island.Territory || elemental.MapId != island.MapId) continue;
                var marker = pos + (MapProjection.ToUv(elemental.Position, island.SizeFactor) - uvMin) * side * zoom;
                var radius = elemental.Visible ? 6 : 4;
                var top = marker - new Vector2(0, radius);
                var right = marker + new Vector2(radius, 0);
                var bottom = marker + new Vector2(0, radius);
                var left = marker - new Vector2(radius, 0);
                if (elemental.Visible) draw.AddQuadFilled(top, right, bottom, left, 0xFFEFE259);
                else draw.AddQuad(top, right, bottom, left, 0xCCEFE259, 1.5f);
                if (hovered && hoveredPoint == null && Vector2.DistanceSquared(marker, io.MousePos) <= 64)
                    hoveredElemental = elemental;
            }
        }
        if (snapshot.CurrentTerritory == island.Territory && snapshot.PlayerMapPosition is { } player)
        {
            var p = pos + (MapProjection.ToUv(player, island.SizeFactor) - uvMin) * side * zoom;
            draw.AddCircleFilled(p, 5, 0xFFFFD070); draw.AddCircle(p, 7, 0xFFFFFFFF, 16, 2);
        }
        var routePreview = nearbyRoutePreview != 0 ? nearbyRoutePreview : selected;
        if (planned != null && navigation != null && island.Fates.Any(f => f.Seed.Id == routePreview))
        {
            var route = navigation.TriggerSnapshot.SelectedRoute(routePreview);
            var markers = route.Select(point => pos + (MapProjection.ToUv(MapProjection.WorldToMap(point, island),
                island.SizeFactor) - uvMin) * zoom * side).ToArray();
            for (var i = 1; i < markers.Length; i++)
            {
                draw.AddLine(markers[i - 1], markers[i], 0xEE151515, 6);
                draw.AddLine(markers[i - 1], markers[i], 0xFFFFD34D, 3);
            }
            for (var i = 0; i < markers.Length; i++)
            {
                draw.AddCircleFilled(markers[i], 6, 0xEE151515);
                draw.AddCircleFilled(markers[i], 4, i == 0 ? 0xFF72E68A : i == markers.Length - 1 ? 0xFF6262FF : 0xFFFFD34D);
                var label = i == 0 ? "起点" : i == markers.Length - 1 ? "终点" : (i + 1).ToString();
                var text = markers[i] + new Vector2(7, -ImGui.GetTextLineHeight() / 2);
                draw.AddRectFilled(text - Vector2.One, text + ImGui.CalcTextSize(label) + Vector2.One, 0xCC151515, 2);
                draw.AddText(text, 0xFFFFFFFF, label);
            }
        }
        draw.PopClipRect();
        if (hoveredElemental is { } elementalPoint && !dragged)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(elementalPoint.Visible ? "元灵 · 当前可见" : "元灵 · 本实例已发现");
            ImGui.TextUnformatted($"({elementalPoint.Position.X:0.0}, {elementalPoint.Position.Y:0.0})");
            ImGui.TextUnformatted($"最后观察 {elementalPoint.LastSeen.ToLocalTime():MM-dd HH:mm}");
            ImGui.EndTooltip();
        }
        if (hoveredPoint is { } id)
        {
            var fate = island.Fates.First(p => p.Seed.Id == id);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Lv.{fate.Level} {fate.Name}");
            ImGui.TextUnformatted($"({fate.Seed.X:0.0}, {fate.Seed.Y:0.0}) · {TimingText(id, now)}");
            ImGui.TextUnformatted(fate.Trigger);
            ImGui.EndTooltip();
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !dragged) { selected = id; selectedArea = hoveredArea; }
        }
        if (ImGui.SmallButton("还原视图")) { centre = new(.5f); zoom = 1; }
        ImGui.SameLine();ImGui.TextDisabled($"缩放 {zoom:0.0}×　点位 {points.Count}　蓝色圆点：自己");
    }

    private string TimingText(ushort id, DateTimeOffset now)
    {
        if (!timing.TryGetValue(id, out var value)) return "未知 · 未记录";
        return value.Until is { } end ? $"{value.Text} {FateTiming.Countdown(end, now)}" : value.Text;
    }

    private void DrawSelectedFate(IslandMap island, FateMapSnapshot snapshot)
    {
        ImGui.TextUnformatted("当前选中 FATE");
        var definition = island.Fates.FirstOrDefault(f => f.Seed.Id == selected);
        if (definition != null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(definition.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(definition.Name);
            if (ImGui.Button("在游戏地图标记")) provider.Mark(definition);
            if (navigation != null)
            {
                ImGui.SameLine(); ImGui.BeginDisabled(snapshot.CurrentTerritory != island.Territory || navigation.Snapshot.Navigating);
                if (ImGui.Button("自动前往")) navigation.NavigateTo(new(island.Territory, island.MapId,
                    MapProjection.MapToWorld(new(definition.Seed.X, definition.Seed.Y), island), definition.Seed.ShortName));
                ImGui.EndDisabled();
            }
            var condition = definition.Seed.WeatherId == 0 ? "不限天气" : weather.FirstOrDefault(w => w.TerritoryId == island.Territory)?.Chances
                .FirstOrDefault(c => c.WeatherId == definition.Seed.WeatherId)?.Name ?? "特定天气";
            ImGui.TextWrapped($"触发：{definition.Trigger}　｜　条件：{condition}{(definition.Seed.Night ? "，ET 19:00–06:00" : "")}{(definition.Seed.Bunny ? "，受寻宝状态影响" : "")}");
            snapshot.Records.TryGetValue(selected, out var record);
            ImGui.TextUnformatted(record != null ? $"出现 {record.Appeared.ToLocalTime():HH:mm:ss}" : "出现 —");
            if (record != null && ImGui.IsItemHovered()) ImGui.SetTooltip(record.Appeared.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            ImGui.SameLine();
            ImGui.BeginDisabled(snapshot.CurrentTerritory != island.Territory || record is { Active: true });
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 4.5f);
            ImGui.InputFloat("##MinutesAgo", ref minutesAgo, 0, 0, "%.1f 分");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("出现于多少分钟前");
            minutesAgo = Math.Clamp(minutesAgo, 0, 240);
            ImGui.SameLine();
            if (ImGui.SmallButton("补记")) { provider.RecordAppearance(selected, minutesAgo); nextTiming = default; }
            ImGui.SameLine();
            if (ImGui.SmallButton("清除")) { provider.ClearRecord(selected); nextTiming = default; }
            ImGui.EndDisabled();
        }
        else ImGui.TextDisabled("点地图上的 FATE 查看详情。");
    }

    private void DrawList(IReadOnlyList<FateDefinition> points, DateTimeOffset now)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##FateSearch", "搜索 FATE / 简称", ref search, 100);
        if (ImGui.BeginTable("##CompactFateList", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                foreach (var fate in points.Where(f => f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || f.Seed.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    var seed = fate.Seed;
                    ImGui.PushID(seed.Id);
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"Lv.{fate.Level} {seed.ShortName}", selected == seed.Id)) selected = seed.Id;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{fate.Name} ({seed.X:0.0}, {seed.Y:0.0})\n{fate.Trigger}");
                    ImGui.TableNextColumn();
                    var status = timing.GetValueOrDefault(seed.Id);
                    ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(status?.Color ?? FateTiming.UnknownColor), TimingText(seed.Id, now));
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
    }
}
