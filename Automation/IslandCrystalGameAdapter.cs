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
    private string Name(IslandCrystal point) => data.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>()!.GetRow(point.PlaceNameId).Name.ExtractText();
    private AddonSelectString* Menu()
    {
        var unit = gui.GetAddonByName("SelectString");
        if (unit.IsNull || !unit.IsVisible || !unit.IsReady) return null;
        return (AddonSelectString*)unit.Address;
    }
    public bool Open(IslandCrystal source)
    {
        if (Menu() != null || objects.LocalPlayer is not { } player) return false;
        if (new[] { "SelectYesno", "Talk", "SelectIconString", "TelepotTown" }.Any(n =>
            { var a = gui.GetAddonByName(n); return !a.IsNull && a.IsVisible; })) return false;
        var crystal = objects.FirstOrDefault(o => o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable
            && NavigationRules.Distance(o.Position, source.Position) < 10 && Vector3.Distance(o.Position, player.Position) <= 5);
        var targets = TargetSystem.Instance();
        if (crystal == null || targets == null) return false;
        targets->SetHardTarget((GameObject*)crystal.Address);
        if ((ulong)targets->GetTargetObjectId() != crystal.GameObjectId) return false;
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
