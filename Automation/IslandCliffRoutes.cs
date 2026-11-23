using System.Numerics;
using System.Text.Json;

namespace ULKHelper.Automation;

// Candidates derived from the local 732/763 navmesh, not verified jump shortcuts.
// Both mesh legs and live game collision must pass before any downward movement.
internal sealed record CliffDrop(string Id, Vector3 Approach, Vector3 Edge, Vector3 Landing);
internal sealed record CliffRegion(uint Territory, string Id, string Name, Vector3 Minimum, Vector3 Maximum, CliffDrop[] Drops);
internal static class IslandCliffRoutes
{
    public static readonly IReadOnlyList<CliffRegion> All = Load();
    private static CliffRegion[] Load()
    {
        try
        {
            using var stream = typeof(IslandCliffRoutes).Assembly.GetManifestResourceStream("ULKHelper.Automation.cliff-routes.json");
            var regions = stream == null ? [] : JsonSerializer.Deserialize<CliffRegion[]>(stream, new JsonSerializerOptions { IncludeFields = true }) ?? [];
            return regions.Where(r => r.Territory is 732 or 763 && NavigationRules.Finite(r.Minimum) && NavigationRules.Finite(r.Maximum))
                .Select(r => r with { Drops = r.Drops.Where(Valid).Take(128).ToArray() }).ToArray();
        }
        catch { return []; }
    }
    public static bool Valid(CliffDrop d) => NavigationRules.Finite(d.Approach) && NavigationRules.Finite(d.Edge)
        && NavigationRules.Finite(d.Landing) && d.Approach.Y - d.Landing.Y is >= 6 and <= 150
        && NavigationRules.Distance(d.Approach, d.Landing) is >= 2 and <= 12
        && NavigationRules.Distance(d.Approach, d.Edge) >= 1;
    public static bool Contains(CliffRegion r, Vector3 p) => p.X >= r.Minimum.X - 1 && p.X <= r.Maximum.X + 1
        && p.Z >= r.Minimum.Z - 1 && p.Z <= r.Maximum.Z + 1 && p.Y >= r.Minimum.Y - 1 && p.Y <= r.Maximum.Y + 3;
}
