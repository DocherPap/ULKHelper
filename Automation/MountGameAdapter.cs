using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace ULKHelper.Automation;

internal sealed class MountGameAdapter(IObjectTable objects, IDataManager data) : IArrivalActions
{
    public IReadOnlyList<OwnedMount> Owned { get; private set; } = [];
    public uint Selected { get; private set; }
    public unsafe void Refresh()
    {
        var state = PlayerState.Instance();
        Owned = state == null ? [] : data.GetExcelSheet<Lumina.Excel.Sheets.Mount>()!
            .Where(m => m.ModelChara.RowId != 0 && state->IsMountUnlocked(m.RowId))
            .Select(m => new OwnedMount(m.RowId, m.Singular.ExtractText(), m.IsImmobile)).OrderBy(m => m.Name).ToArray();
        if (!Owned.Any(m => m.Id == Selected && !m.Immobile)) Selected = Owned.FirstOrDefault(m => !m.Immobile)?.Id ?? 0;
    }
    public bool EnsureTravelMount(uint preferred = 0, bool refresh = false)
    {
        if (refresh || Selected == 0)
        {
            var restorePreference = Selected == 0;
            Refresh();
            if (restorePreference) Select(preferred);
        }
        return Selected != 0;
    }

    public void Select(uint id) { if (Owned.Any(m => m.Id == id && !m.Immobile)) Selected = id; }
    public void Reset() { Owned = []; Selected = 0; }
    public unsafe bool Mount()
    {
        if (objects.LocalPlayer is not { IsDead: false } player || player.IsCasting || !EnsureTravelMount()) return false;
        var character = (Character*)player.Address;
        var state = PlayerState.Instance();
        if (character->Mount.MountId != 0) return true;
        var action = ActionManager.Instance();
        return !character->InCombat && state != null && state->IsMountUnlocked(Selected) && action != null
            && action->GetActionStatus(ActionType.Mount, Selected) == 0 && action->UseAction(ActionType.Mount, Selected);
    }
    public unsafe bool Dismount()
    {
        if (objects.LocalPlayer is not { } player) return false;
        if (((Character*)player.Address)->Mount.MountId == 0) return true;
        var action = ActionManager.Instance();
        return action != null && action->GetActionStatus(ActionType.GeneralAction, 23) == 0 && action->UseAction(ActionType.GeneralAction, 23);
    }
}
