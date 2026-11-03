using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace ULKHelper.Automation;

internal static class PlayerListHelper
{
    // Only called on the framework thread; never return live game objects to the UI.
    public static IReadOnlyList<PlayerEntry> GetPlayers(IObjectTable objects)
    {
        var local = objects.LocalPlayer;
        if (local == null) return [];
        var origin = local.Position;
        return objects.OfType<IPlayerCharacter>()
            .Where(p => p.HomeWorld.RowId != 0 && !string.IsNullOrWhiteSpace(p.Name.TextValue))
            .Select(p => new PlayerEntry(new(p.Name.TextValue, p.HomeWorld.RowId),
                p.HomeWorld.Value.Name.ExtractText(), p.Position, NavigationRules.Distance(origin, p.Position)))
            .DistinctBy(p => p.Identity).OrderBy(p => p.Distance).ThenBy(p => p.Identity.Name).ToArray();
    }
}
