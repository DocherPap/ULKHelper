using System.Numerics;

namespace ULKHelper.Automation;

internal interface IIslandCrystalActions
{
    string? Error => null;
    bool Open(IslandCrystal source);
    IReadOnlySet<uint>? Choices();
    bool Select(IslandCrystal target);
    void Cancel();
}

internal sealed record IslandCrystal(uint Territory, uint PlaceNameId, Vector3 Position);
internal sealed record CrystalRoute(IslandCrystal Source, IslandCrystal Target, float Cost);
internal static class IslandCrystalRoutes
{
    public static IslandCrystal? Entrance(uint territory) => All.FirstOrDefault(p => p.Territory == territory);
    public static IslandCrystal? Nearest(uint territory, Vector3 player) => NavigationRules.Finite(player)
        ? All.Where(p => p.Territory == territory).OrderBy(p => NavigationRules.Distance(player, p.Position)).ThenBy(p => p.PlaceNameId).FirstOrDefault() : null;
    // X/Z reference positions: Lifestream CustomAethernet.cs, b36320170faafc8315f7585edb45cfbcd34e7046.
    // IDs are actual PlaceName rows, not Lifestream's synthetic Aetheryte IDs. Height is resolved through the mesh.
    public static readonly IReadOnlyList<IslandCrystal> All = [
        new(732,2415,new(-138.9f,0,543.2f)), new(732,2429,new(-372.1f,0,-458.7f)), new(732,2436,new(435.5f,0,-48.1f)),
        new(763,2463,new(-893.7f,0,159)), new(763,2474,new(91.1f,0,303.2f)), new(763,2472,new(-707.1f,0,-318.9f)), new(763,2473,new(346,0,-289.7f)),
        new(795,2531,new(-253.5f,0,146.8f)), new(795,2540,new(125.7f,0,795.3f)), new(795,2541,new(127.9f,0,-196.1f)), new(795,2542,new(-443.4f,0,-622.7f)),
        new(827,2876,new(-61.7f,0,-875.6f)), new(827,2891,new(-587.3f,0,-148.4f)), new(827,2892,new(781.1f,0,-417.5f)) ];
    // Conservative heuristic in equivalent walking metres, not an exact travel-time claim.
    public static CrystalRoute? Choose(uint territory, Vector3 player, Vector3 goal, IslandCrystal? source = null,
        IReadOnlySet<uint>? available = null)
    {
        if (!NavigationRules.Finite(player) || !NavigationRules.Finite(goal)) return null;
        var direct = NavigationRules.Distance(player, goal);
        return (from a in All where a.Territory == territory && (source == null || a == source)
            from b in All where b.Territory == territory && a != b && (available == null || available.Contains(b.PlaceNameId))
            let cost = NavigationRules.Distance(player, a.Position) + 120 + NavigationRules.Distance(b.Position, goal)
            where cost + 150 < direct
            orderby cost, a.PlaceNameId, b.PlaceNameId
            select new CrystalRoute(a, b, cost)).FirstOrDefault();
    }
}
