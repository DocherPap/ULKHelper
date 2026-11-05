using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace ULKHelper.Automation;

// Owns only the SelectString menu opened against the chosen crystal. No Lifestream dependency.
internal sealed unsafe class IslandCrystalGameAdapter(IObjectTable objects, IGameGui gui, IDataManager data) : IIslandCrystalActions
{
    private ulong actor;
    private ushort addonId;
    private bool opened;
    public string? Error { get; private set; }
    private bool Fail(string reason) { Error = reason; return false; }
    private HashSet<uint>? shardIds;
    private bool IsCrystal(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        // Lifestream Utils.IsAetheryte also accepts EObj shards, not just ObjectKind.Aetheryte.
        var rows = data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>()!;
        if (shardIds == null)
        {
            var names = new HashSet<string>();
            // 2009421: live CN Anemos report, 2026-11-03, EventObj "岛内以太之光", 1.6m, targetable.
            foreach (var id in new uint[] { 2000151, 2014665, 2014664, 2003395, 2011160, 2009421 })
                if (rows.TryGetRow(id, out var row) && row.Singular.ExtractText() is { Length: > 0 } name) names.Add(name);
            shardIds = rows.Where(r => names.Contains(r.Singular.ExtractText())).Select(r => r.RowId).ToHashSet();
        }
        return obj.ObjectKind == ObjectKind.Aetheryte || obj.ObjectKind == ObjectKind.EventObj && shardIds.Contains(obj.BaseId);
    }
    private string Name(IslandCrystal point) => data.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>()!.GetRow(point.PlaceNameId).Name.ExtractText();
    private AddonSelectString* Menu()
    {
        var unit = gui.GetAddonByName("SelectString");
        if (unit.IsNull || !unit.IsVisible || !unit.IsReady) return null;
        return (AddonSelectString*)unit.Address;
    }
    public Vector3? Locate(IslandCrystal source)
    {
        var match = objects.Where(o => o.IsTargetable && IsCrystal(o)
            && NavigationRules.Distance(o.Position, source.Position) < 10).Take(2).ToArray();
        return match.Length == 1 ? match[0].Position : null;
    }

    public bool Open(IslandCrystal source)
    {
        Error = null;
        if (Menu() != null || objects.LocalPlayer is not { } player) return Fail("已有菜单或玩家尚未加载。");
        if (new[] { "SelectYesno", "Talk", "SelectIconString", "TelepotTown" }.Any(n =>
            { var a = gui.GetAddonByName(n); return !a.IsNull && a.IsVisible; })) return Fail("其他交互界面占用中。");
        var recognized = objects.Where(IsCrystal).ToArray();
        var nearSource = recognized.Where(o => NavigationRules.Distance(o.Position, source.Position) < 10).ToArray();
        var selectable = nearSource.Where(o => o.IsTargetable).ToArray();
        var crystal = selectable.Where(o => Vector3.Distance(o.Position, player.Position) <= 5)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position)).FirstOrDefault();
        var targets = TargetSystem.Instance();
        if (recognized.Length == 0) return Fail("没有识别到已加载的水晶对象；请复制包含现场状态的报告。");
        if (nearSource.Length == 0) return Fail($"已识别{recognized.Length}个水晶，但没有位于源水晶{source.PlaceNameId}参考点10米内的对象。");
        if (selectable.Length == 0) return Fail("源水晶对象已加载，但当前不可选中。");
        if (crystal == null) return Fail($"源水晶已识别，实际距离{selectable.Min(o => Vector3.Distance(o.Position, player.Position)):0.0}米，超过5米交互范围。");
        if (targets == null) return Fail("游戏目标系统尚未就绪。");
        targets->SetHardTarget((GameObject*)crystal.Address);
        if ((ulong)targets->GetTargetObjectId() != crystal.GameObjectId) return Fail("无法选中源水晶。");
        actor = crystal.GameObjectId; addonId = 0; opened = true;
        targets->InteractWithObject((GameObject*)crystal.Address);
        return true;
    }
    private AddonSelectString* OwnedMenu()
    {
        if (!opened || TargetSystem.Instance() == null || (ulong)TargetSystem.Instance()->GetTargetObjectId() != actor) return null;
        var menu = Menu();
        if (menu == null || addonId != 0 && menu->Id != addonId) return null;
        if (addonId == 0) addonId = menu->Id;
        return menu;
    }
    public IReadOnlySet<uint>? Choices()
    {
        var menu = OwnedMenu();
        if (menu == null) return null;
        var popup = &menu->PopupMenu.PopupMenu;
        if (popup->EntryNames == null || popup->List == null || popup->EntryCount is < 1 or > 32
            || popup->List->ListLength < popup->EntryCount) return null;
        var available = new HashSet<uint>();
        foreach (var point in IslandCrystalRoutes.All)
            for (var i = 0; i < popup->EntryCount; i++)
                if (!popup->List->GetItemDisabledState(i) && popup->EntryNames[i].ToString() == Name(point)) available.Add(point.PlaceNameId);
        return available;
    }
    public bool Select(IslandCrystal target)
    {
        var menu = OwnedMenu();
        if (menu == null) return false;
        var popup = &menu->PopupMenu.PopupMenu;
        if (popup->EntryNames == null || popup->List == null || popup->EntryCount is < 1 or > 32
            || popup->List->ListLength < popup->EntryCount) return false;
        var matches = new List<int>();
        for (var i = 0; i < popup->EntryCount; i++)
            if (!popup->List->GetItemDisabledState(i) && popup->EntryNames[i].ToString() == Name(target)) matches.Add(i);
        if (matches.Count != 1) return false;
        AtkValue value = default; value.SetInt(matches[0]);
        var accepted = menu->FireCallback(1, &value, true);
        if (accepted) { opened = false; actor = 0; addonId = 0; }
        return accepted;
    }
    public void Cancel()
    {
        var menu = OwnedMenu();
        if (menu != null) menu->Close(true);
        actor = 0; addonId = 0; opened = false;
    }
}
