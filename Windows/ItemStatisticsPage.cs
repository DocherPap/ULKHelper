using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Data;
using ULKHelper.Inventory;

namespace ULKHelper.Windows;

internal sealed class ItemStatisticsPage
{
    private static readonly Vector4 BrightGreen = new(.30f, .91f, .48f, 1);
    private static readonly Vector4 PaleGreen = new(.58f, .76f, .63f, 1);
    private static readonly Vector4 Muted = new(.53f, .57f, .60f, 1);
    private static readonly Vector4 PreviewColor = new(.89f, .74f, .42f, 1);
    private static readonly string[] Sources = ["背包", "兵装库", "陆行鸟背包", "投影台", "收藏柜"];
    private readonly bool[] selectedJobs = Enumerable.Repeat(true, StatisticsCatalog.Jobs.Length).ToArray();
    private readonly bool[] selectedCategories = Enumerable.Repeat(true, StatisticsCatalog.CategoryNames.Length).ToArray();
    private bool byJob;
    private bool overall;
    private bool showExamples = true;
    private EquipmentSection[] sections = [];
    private MaterialPreview[] materials = [];
    private readonly IStatisticsProvider? statistics;
    private long inventoryRevision = -1;
    private bool HasOwnership => showExamples || statistics?.HasData == true;

    public ItemStatisticsPage(IStatisticsProvider? statistics = null)
    {
        this.statistics = statistics;
        showExamples = statistics == null;
        RebuildPreview();
    }
    public void Load(SettingsStore settings)
    {
        byJob = settings.Get("statistics.byJob", false); overall = settings.Get("statistics.overall", false);
        var jobs = settings.Get("statistics.jobs", selectedJobs);
        var categories = settings.Get("statistics.categories", selectedCategories);
        Array.Copy(jobs, selectedJobs, Math.Min(jobs.Length, selectedJobs.Length));
        Array.Copy(categories, selectedCategories, Math.Min(categories.Length, selectedCategories.Length));
        RebuildPreview();
    }
    public void Save(SettingsStore settings)
    {
        settings.Put("statistics.byJob", byJob); settings.Put("statistics.overall", overall);
        settings.Put("statistics.jobs", selectedJobs); settings.Put("statistics.categories", selectedCategories);
    }

    public void Draw()
    {
        if (statistics != null && inventoryRevision != statistics.Revision)
        {
            inventoryRevision = statistics.Revision;
            RebuildPreview();
        }
        // Initial weights; ImGui retains widths adjusted by dragging the column borders.
        LayoutSettings.RememberTable("##StatisticsLayoutWideMaterials");
        if (!ImGui.BeginTable("##StatisticsLayoutWideMaterials", 3,
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            ImGui.TableSetupColumn("选项", ImGuiTableColumnFlags.WidthStretch, 18);
            ImGui.TableSetupColumn("拥有情况", ImGuiTableColumnFlags.WidthStretch, 54);
            ImGui.TableSetupColumn("材料统计", ImGuiTableColumnFlags.WidthStretch, 28);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawPane("##Options", DrawOptions);
            ImGui.TableSetColumnIndex(1);
            DrawPane("##Equipment", DrawEquipment);
            ImGui.TableSetColumnIndex(2);
            DrawPane("##Materials", DrawMaterials);
        }
        finally { ImGui.EndTable(); }
    }

    private static void DrawPane(string id, Action content)
    {
        var visible = ImGui.BeginChild(id, Vector2.Zero);
        try { if (visible) content(); }
        finally { ImGui.EndChild(); }
    }

    private void DrawOptions()
    {
        SectionHeader("选项");
        var changed = false;
        if (ImGui.RadioButton("按物品分类", !byJob)) { byJob = false; changed = true; }
        if (ImGui.RadioButton("按职业分类", byJob)) { byJob = true; changed = true; }
        ImGui.Spacing();
        SectionHeader(byJob ? "物品分类" : "职业筛选");
        if (byJob)
        {
            for (var i = 0; i < selectedCategories.Length; i++)
                changed |= ImGui.Checkbox(StatisticsCatalog.CategoryNames[i], ref selectedCategories[i]);
        }
        else
        {
            var columns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 13 ? 2 : 1;
            if (ImGui.BeginTable("##JobFilters", columns, ImGuiTableFlags.SizingStretchSame))
            {
                try
                {
                    for (var i = 0; i < selectedJobs.Length; i++)
                    {
                        ImGui.TableNextColumn();
                        changed |= ImGui.Checkbox(StatisticsCatalog.Jobs[i].Name, ref selectedJobs[i]);
                    }
                }
                finally { ImGui.EndTable(); }
            }
        }

        ImGui.Spacing();
        SectionHeader("拥有物品更新状态");
        if (statistics != null)
        {
            foreach (var source in MergeRetainerStatuses(statistics.Sources))
            {
                ImGui.TextWrapped(source.Label);
                var state = source.ReadThisSession ? "已更新" : source.UpdatedAt != null ? "已缓存" : "未获取";
                if (ImGui.GetContentRegionAvail().X >= ImGui.CalcTextSize(state).X + ImGui.GetStyle().ItemSpacing.X)
                    ImGui.SameLine();
                ImGui.TextColored(source.ReadThisSession ? BrightGreen : Muted, state);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(source.UpdatedAt is { } time
                        ? $"最后获取：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n保存于当前角色的本地缓存。"
                        : "请打开此来源获取库存；未获取不代表没有物品。");
            }
            ImGui.Spacing();
            ImGui.TextWrapped("库存自动更新；缓存来源保留上次获取的结果。");
        }
        else
        {
            for (var i = 0; i < Sources.Length; i++)
            {
                ImGui.TextUnformatted(Sources[i]);
                ImGui.SameLine();
                var updated = showExamples && i < 2;
                ImGui.TextColored(updated ? BrightGreen : Muted, updated ? "已更新" : "未获取");
            }
            ImGui.Spacing();
            ImGui.Separator();
            changed |= ImGui.Checkbox("示例进度", ref showExamples);
            ImGui.TextColored(PreviewColor, "仅 UI 预览");
            ImGui.TextWrapped("未读取角色物品；示例进度仅用于预览。");
        }
        if (changed) RebuildPreview();
    }

    private static IEnumerable<SourceSnapshot> MergeRetainerStatuses(IReadOnlyList<SourceSnapshot> sources)
    {
        foreach (var group in sources.GroupBy(source =>
        {
            var separator = source.Key.LastIndexOf(':');
            return source.Key.StartsWith("retainer:", StringComparison.Ordinal) && separator > 8
                ? source.Key[..separator] : source.Key;
        }))
        {
            var first = group.First();
            if (!first.Key.StartsWith("retainer:", StringComparison.Ordinal))
            {
                yield return first;
                continue;
            }
            var suffix = first.Label.LastIndexOf('（');
            yield return new SourceSnapshot
            {
                Key = group.Key,
                Label = suffix >= 0 ? first.Label[..suffix] : first.Label,
                ReadThisSession = group.Any(s => s.ReadThisSession),
                UpdatedAt = group.Max(s => s.UpdatedAt),
            };
        }
    }

    private void DrawEquipment()
    {
        SectionHeader("拥有情况");
        ImGui.TextColored(PreviewColor, statistics?.StatusText ?? (showExamples ? "示例预览 · 非角色实际数据" : "拥有情况尚未获取"));
        DrawLegend();
        DrawPane("##EquipmentScroll", () =>
        {
            if (sections.Length == 0)
            {
                ImGui.TextDisabled("没有符合筛选条件的装备。");
                return;
            }
            foreach (var section in sections)
            {
                SectionHeader(section.Title);
                ImGui.PushID(section.Title);
                try
                {
                    if (byJob) DrawJobEquipment(section.Cards);
                    else DrawEquipmentGrid(section.Cards);
                }
                finally { ImGui.PopID(); }
                ImGui.Spacing();
            }
        });
    }

    private static void DrawLegend()
    {
        ImGui.TextWrapped("亮色：实际拥有　浅色：前置阶段　空心：未拥有");
        foreach (var entry in new[] { ("常风", "常风水晶"), ("恒冰", "恒冰水晶"), ("涌火", "涌火水晶"), ("丰水", "丰水水晶"), ("改", "优雷卡的断片") })
        {
            if (entry.Item1 != "常风") ImGui.SameLine();
            ImGui.TextColored(StatisticsPalette.Material(entry.Item2).Bright, entry.Item1);
        }
    }

    private void DrawJobEquipment(EquipmentCard[] cards)
    {
        EquipmentCard[][] rows =
        [
            cards.Where(c => c.Category == EquipmentCategory.Weapon || c.SpecialArmor)
                .OrderBy(c => c.SpecialArmor).ToArray(),
            cards.Where(c => c.Category == EquipmentCategory.Artifact).ToArray(),
            cards.Where(c => c.Category == EquipmentCategory.Elemental && !c.SpecialArmor).ToArray(),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i].Length == 0) continue;
            ImGui.PushID(i);
            try { DrawEquipmentGrid(rows[i]); }
            finally { ImGui.PopID(); }
            ImGui.Spacing();
        }
    }

    private void DrawEquipmentGrid(EquipmentCard[] cards)
    {
        // Cap the card size and center the five-column grid instead of stretching it.
        var startX = ImGui.GetCursorPosX();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var targetSide = 110 * ImGui.GetFontSize() / 17;
        var gridWidth = Math.Max(1, Math.Min(availableWidth, 5 * (targetSide + ImGui.GetStyle().CellPadding.X * 2)));
        ImGui.SetCursorPosX(startX + Math.Max(0, (availableWidth - gridWidth) / 2));
        if (!ImGui.BeginTable("##EquipmentGrid", 5, ImGuiTableFlags.SizingStretchSame, new Vector2(gridWidth, 0)))
        {
            ImGui.SetCursorPosX(startX);
            return;
        }
        try
        {
            foreach (var card in cards)
            {
                ImGui.TableNextColumn();
                var side = Math.Max(1, ImGui.GetContentRegionAvail().X);
                var height = Math.Max(ImGui.GetFontSize() * 4.8f, side * .74f);
                var size = new Vector2(side, height);
                var origin = ImGui.GetCursorScreenPos();
                var draw = ImGui.GetWindowDrawList();
                ImGui.InvisibleButton(card.Key, size);
                var hovered = ImGui.IsItemHovered();
                DrawCardBackground(origin, size, hovered);
                var caption = card.Category is EquipmentCategory.Elemental or EquipmentCategory.Attire
                    ? StatisticsCatalog.Jobs.First(j => card.Jobs.Contains(j.Key)).Role
                    : StatisticsCatalog.Jobs.First(j => j.Key == card.Jobs[0]).Name;
                draw.PushClipRect(origin, origin + size, true);
                try
                {
                    DrawCenteredText(caption, origin + new Vector2(6, 3),
                        new Vector2(side - 12, ImGui.GetFontSize()), Muted);
                    DrawCenteredText(card.Name, origin + new Vector2(6, ImGui.GetFontSize() + 5),
                        new Vector2(side - 12, height - ImGui.GetFontSize() * 1.9f - 5), ImGui.GetStyle().Colors[(int)ImGuiCol.Text], 1.15f);
                    DrawStageDots(card, origin, size);
                }
                finally { draw.PopClipRect(); }
                if (hovered) DrawEquipmentTooltip(card);
            }
        }
        finally
        {
            ImGui.EndTable();
            ImGui.SetCursorPosX(startX);
        }
    }

    private void DrawStageDots(EquipmentCard card, Vector2 origin, Vector2 size)
    {
        var side = size.X;
        var draw = ImGui.GetWindowDrawList();
        var fontScale = ImGui.GetFontSize() / 17;
        var radii = card.Stages.Select(s => (s.Major ? 3.8f : 1.9f) * fontScale).ToArray();
        var gap = 2 * fontScale;
        var fullWidth = radii.Sum(r => r * 2) + gap * (radii.Length - 1);
        var fit = Math.Clamp((side - 12) / fullWidth, 0, 1);
        var x = origin.X + (side - fullWidth * fit) / 2;
        var owned = GetOwnedStages(card);
        var latest = GetCompletedStage(card, owned);
        for (var i = 0; i < radii.Length; i++)
        {
            var radius = radii[i] * fit;
            var center = new Vector2(x + radius, origin.Y + size.Y - 8 * fontScale);
            var palette = StatisticsPalette.Stage(card, i);
            var color = owned.Contains(i) ? palette.Bright : i <= latest ? palette.Pale : Muted;
            if (i <= latest) draw.AddCircleFilled(center, radius, Color(color));
            else draw.AddCircle(center, radius, Color(color));
            x += radius * 2 + gap * fit;
        }
    }

    private void DrawEquipmentTooltip(EquipmentCard card)
    {
        ImGui.BeginTooltip();
        try
        {
            ImGui.TextUnformatted(card.Name);
            if (card.Note.Length > 0) ImGui.TextUnformatted(card.Note);
            ImGui.Separator();
            var owned = GetOwnedStages(card);
            var latest = GetCompletedStage(card, owned);
            for (var i = 0; i < card.Stages.Length; i++)
            {
                var stage = card.Stages[i];
                var palette = StatisticsPalette.Stage(card, i);
                var color = owned.Contains(i) ? palette.Bright : i <= latest ? palette.Pale : Muted;
                var state = !HasOwnership ? "未获取" : owned.Contains(i) ? "拥有" : i <= latest ? "已完成" : "未在已获取库存中找到";
                ImGui.TextColored(color, $"{(stage.Major ? "●" : "·")} {stage.Name} · {state}");
            }
            ImGui.Separator();
            ImGui.TextDisabled("大圆点：主要阶段；小圆点：中间阶段");
        }
        finally { ImGui.EndTooltip(); }
    }

    private void DrawMaterials()
    {
        SectionHeader("材料统计");
        ImGui.RadioButton("按目前进度", ref overall, false);
        ImGui.RadioButton("按总体进度", ref overall, true);
        ImGui.TextWrapped(overall ? "持有 + 已投入 / 全部所需" : "目前持有 / 后续所需（未扣持有）");
        ImGui.Separator();
        DrawPane("##MaterialScroll", () =>
        {
            if (materials.Length == 0)
            {
                ImGui.TextDisabled("没有需要统计的材料。");
                return;
            }
            foreach (var group in StatisticsCatalog.MaterialRows)
            {
                var row = group.Materials.Select(name => materials.FirstOrDefault(m => m.Material.Name == name))
                    .OfType<MaterialPreview>().ToArray();
                if (row.Length == 0) continue;
                SectionHeader(group.Title);
                DrawMaterialRow(group.Title, row);
                ImGui.Spacing();
            }
            ImGui.Spacing();
            ImGui.TextDisabled("可兑换材料：悬停查看换算");
            ImGui.TextWrapped("爆炎乱属性水晶：每次调属性消耗 2 个，按需使用，不计入固定总量。");
        });
    }

    private void DrawMaterialRow(string id, MaterialPreview[] row)
    {
        var startX = ImGui.GetCursorPosX();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var targetSide = 90 * ImGui.GetFontSize() / 17;
        var gridWidth = Math.Max(1, Math.Min(availableWidth, row.Length * (targetSide + ImGui.GetStyle().CellPadding.X * 2)));
        ImGui.SetCursorPosX(startX + Math.Max(0, (availableWidth - gridWidth) / 2));
        if (!ImGui.BeginTable($"##Materials-{id}", row.Length, ImGuiTableFlags.SizingStretchSame, new Vector2(gridWidth, 0)))
        {
            ImGui.SetCursorPosX(startX);
            return;
        }
        try
        {
            foreach (var material in row)
            {
                ImGui.TableNextColumn();
                DrawMaterialCard(material);
            }
        }
        finally
        {
            ImGui.EndTable();
            ImGui.SetCursorPosX(startX);
        }
    }

    private void DrawMaterialCard(MaterialPreview value)
    {
        var side = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var height = Math.Max(ImGui.GetFontSize() * 4.35f, side * .8f);
        var size = new Vector2(side, height);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(value.Material.Name, size);
        var hovered = ImGui.IsItemHovered();
        DrawCardBackground(origin, size, hovered);
        var palette = StatisticsPalette.Material(value.Material.Name);
        var numerator = overall ? value.Owned + value.Spent : value.Owned;
        var denominator = overall ? value.Total : value.Remaining;
        var fraction = !HasOwnership ? 0 : denominator == 0 ? 1 : Math.Clamp((float)numerator / denominator, 0, 1);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        try
        {
            var cardScale = Math.Min(1, side / (110 * ImGui.GetFontSize() / 17));
            var font = ImGui.GetFontSize() * cardScale;
            DrawCenteredText(value.Material.Name, origin + new Vector2(6, 3),
                new Vector2(side - 12, height * .47f - 3), ImGui.GetStyle().Colors[(int)ImGuiCol.Text], 1.15f * cardScale);
            var ownedText = HasOwnership ? $"{numerator:N0}" : "—";
            var numbers = $"{ownedText} / {denominator:N0}";
            if (ImGui.CalcTextSize(numbers).X * cardScale > side - 10)
                numbers = $"{ownedText} /\n{denominator:N0}";
            DrawCenteredText(numbers, origin + new Vector2(5, height * .47f + 2),
                new Vector2(side - 10, height * .53f - font * .65f - 2), palette.Pale, cardScale);
            var barStart = origin + new Vector2(7, height - font * .45f);
            var barEnd = origin + new Vector2(side - 7, height - font * .20f);
            draw.AddRectFilled(barStart, barEnd, Color(palette.Pale with { W = .30f }), 3);
            if (fraction > 0)
                draw.AddRectFilled(barStart, new Vector2(barStart.X + (barEnd.X - barStart.X) * fraction, barEnd.Y), Color(palette.Bright), 3);
        }
        finally { draw.PopClipRect(); }
        if (hovered) DrawMaterialTooltip(value);
    }

    private void DrawMaterialTooltip(MaterialPreview value)
    {
        ImGui.BeginTooltip();
        try
        {
            ImGui.TextUnformatted(value.Material.Name);
            ImGui.TextUnformatted($"全部所需：{value.Total:N0}");
            if (!HasOwnership)
            {
                ImGui.TextDisabled("库存尚未获取，请打开对应的物品来源。");
                return;
            }
            ImGui.TextUnformatted($"目前拥有：{value.Owned:N0}　推算已投入：{value.Spent:N0}");
            ImGui.TextUnformatted($"后续所需：{value.Remaining:N0}　缺口：{value.Deficit:N0}");
            if (value.Material.ExchangeMaterial is not { } exchange) return;
            ImGui.Separator();
            var required = (int)Math.Ceiling(value.Deficit * value.Material.ExchangeCost);
            ImGui.TextColored(StatisticsPalette.Material(value.Material.Name).Pale, $"兑换缺口需{(value.Material.Approximate ? "约 " : " ")}{required:N0} 个{exchange}");
            ImGui.TextDisabled(value.Material.Approximate
                ? "按 1 常风水晶 ≈ 3.5 乱属性水晶估算，向上取整。"
                : $"每个{value.Material.Name}需 {value.Material.ExchangeCost:0} 个{exchange}。");
            ImGui.TextDisabled("独立估算，未扣兑换材料库存及其他用途。");
        }
        finally { ImGui.EndTooltip(); }
    }

    private void RebuildPreview()
    {
        EquipmentCard[] visible;
        if (byJob)
        {
            visible = StatisticsCatalog.Equipment.Where(c => selectedCategories[(int)c.Category]).ToArray();
            sections = StatisticsCatalog.Jobs.Select(job =>
                new EquipmentSection(job.Name, visible.Where(c => c.Jobs.Contains(job.Key)).ToArray()))
                .Where(s => s.Cards.Length > 0).ToArray();
        }
        else
        {
            var jobs = StatisticsCatalog.Jobs.Where((_, i) => selectedJobs[i]).Select(j => j.Key).ToHashSet();
            visible = StatisticsCatalog.Equipment.Where(c => c.Jobs.Any(jobs.Contains)).ToArray();
            sections = Enum.GetValues<EquipmentCategory>().Select(category =>
                new EquipmentSection(StatisticsCatalog.CategoryNames[(int)category], visible.Where(c => c.Category == category)
                    .OrderBy(c => c.SpecialArmor && c.Name.StartsWith("朱红", StringComparison.Ordinal)).ToArray()))
                .Where(s => s.Cards.Length > 0).ToArray();
        }
        materials = statistics?.Summarize(visible) ?? StatisticsPreview.Summarize(visible, showExamples);
    }

    private int[] GetOwnedStages(EquipmentCard card) => statistics?.OwnedStages(card) ?? StatisticsPreview.GetOwnedStages(card, showExamples);
    private int GetCompletedStage(EquipmentCard card, int[] owned) => statistics?.CompletedStage(card) ?? owned.DefaultIfEmpty(-1).Max();

    private static void DrawCardBackground(Vector2 origin, Vector2 size, bool hovered)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, Color(new Vector4(.13f, .16f, .17f, 1)), 5);
        draw.AddRect(origin, origin + size, Color(hovered ? PaleGreen : new Vector4(.27f, .32f, .32f, 1)), 5);
    }

    // Measure and draw at the same font scale so larger names still wrap and center correctly.
    private static void DrawCenteredText(string text, Vector2 origin, Vector2 size, Vector4 color, float fontScale = 1)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var character in text)
        {
            if (character == '\n' || line.Length > 0 && ImGui.CalcTextSize(line + character).X * fontScale > size.X)
            {
                lines.Add(line);
                line = "";
            }
            if (character != '\n') line += character;
        }
        if (line.Length > 0) lines.Add(line);
        var height = ImGui.GetTextLineHeight() * fontScale * .95f;
        var y = origin.Y + Math.Max(0, (size.Y - lines.Count * height) / 2);
        var draw = ImGui.GetWindowDrawList();
        foreach (var part in lines)
        {
            var x = origin.X + Math.Max(0, (size.X - ImGui.CalcTextSize(part).X * fontScale) / 2);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * fontScale, new Vector2(x, y), Color(color), part);
            y += height;
        }
    }

    private static uint Color(Vector4 value) => ImGui.ColorConvertFloat4ToU32(value);

    private static void SectionHeader(string title)
    {
        ImGui.TextUnformatted(title);
        ImGui.Separator();
    }

    private sealed record EquipmentSection(string Title, EquipmentCard[] Cards);
}
