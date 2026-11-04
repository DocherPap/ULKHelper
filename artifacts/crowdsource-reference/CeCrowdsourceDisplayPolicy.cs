using BOCCHI.Data;
using System;

namespace BOCCHI.Modules.CeCrowdsource;

public static class CeCrowdsourceDisplayPolicy
{
    public static bool ShouldDisplayRecord(CeRecord record)
    {
        if (!ZoneData.IsOccultCrescentTerritory(record.TerritoryID))
        {
            return false;
        }

        if (string.Equals(record.EventType, "CE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The bridge stores all observations in the same generic event table.
        // Only the two North Horn magic-pot FATEs belong in this panel; do not
        // accidentally turn the regular FATE tracker into a second event feed.
        return string.Equals(record.EventType, "FATE", StringComparison.OrdinalIgnoreCase)
               && EventData.GetFate(record.EventID, record.TerritoryID).IsPot;
    }

    /// <summary>
    /// Resolves an observation to the state that should be shown in the panel.
    /// A server record which is no longer active is history, even when its last
    /// observation was Battle; otherwise dead bosses would remain red forever.
    /// </summary>
    public static string? ResolveState(string? observedState, string? localState, bool isActive)
    {
        if (string.Equals(localState, "Inactive", StringComparison.Ordinal))
        {
            return "Inactive";
        }

        if (!string.IsNullOrWhiteSpace(localState))
        {
            return localState;
        }

        return isActive ? observedState : "Inactive";
    }
}

