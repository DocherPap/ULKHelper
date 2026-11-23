using System.Numerics;

namespace ULKHelper.Automation;

internal interface IIslandCrystalActions
{
    string? Error => null;
    Vector3? Locate(IslandCrystal source) => null;
    bool Open(IslandCrystal source);
    IReadOnlySet<uint>? Choices();
    bool Select(IslandCrystal target);
    void Cancel();
}

internal sealed record IslandCrystal(uint Territory, uint PlaceNameId, Vector3 Position, float? GroundHeight = null);
internal sealed record CrystalRoute(IslandCrystal Source, IslandCrystal Target, float Cost);
internal static class IslandCrystalRoutes
{
    public const float MinimumWalkingSavings = 30;
    public const float GroundRadius = 2;
    public const float ExpandedGroundRadius = GroundRadius * 1.5f;
    public static bool NearGround(Vector3 point, Vector3 anchor, float radius = GroundRadius) =>
        NavigationRules.Finite(point) && NavigationRules.Distance(point, anchor) <= radius && Math.Abs(point.Y - anchor.Y) <= 3;
    public static Vector3? GroundAnchor(IslandCrystal crystal, Vector3 player, INavigationMesh mesh, Vector3? live = null)
    {
        if (live is { } actual && NavigationRules.Finite(actual)) return actual;
        if (crystal.GroundHeight is { } height) return crystal.Position with { Y = height };
        foreach (var point in NavigationGround.Candidates(mesh, crystal.Position with { Y = player.Y }, false, GroundRadius, true)
            .Concat(NavigationGround.Candidates(mesh, crystal.Position, true, GroundRadius, true)))
            return crystal.Position with { Y = point.Y };
        return null;
    }
    public static IslandCrystal? Entrance(uint territory) => All.FirstOrDefault(p => p.Territory == territory);
    public static MapDestination? CampDestination(WorldContext world, Vector3 player) =>
        Entrance(world.Territory) is { } crystal
            ? new(world.Territory, world.Map, ArrivalRules.Position(player,
                crystal.Position with { Y = crystal.GroundHeight ?? crystal.Position.Y }, 7), "入口营地")
            : null;
    public static IslandCrystal? Nearest(uint territory, Vector3 player) => NavigationRules.Finite(player)
        ? All.Where(p => p.Territory == territory).OrderBy(p => NavigationRules.Distance(player, p.Position)).ThenBy(p => p.PlaceNameId).FirstOrDefault() : null;
    // Pyros/Hydatos X/Z reference positions: Lifestream CustomAethernet.cs, b36320170faafc8315f7585edb45cfbcd34e7046.
    // IDs are actual PlaceName rows, not Lifestream's synthetic Aetheryte IDs. Height is resolved through the mesh.
    public static readonly IReadOnlyList<IslandCrystal> All = [
        // Ground-height references: local continuous recording 2026-11-07 00:22:01 / 00:41:28 -07:00.
        // These are observed walkable floor heights near the crystals, not assumed object Y coordinates.
        // CN 2026-11-21 17:36:52: mesh floor <-138.900,-12.250,542.000>,
        // with the player on the connected camp ground at <-143.693,-13.239,521.666>.
        // Exact object XYZ: CN game files, 2026-11-23, z3fa/z3fb/level/planevent.lgb,
        // QST_TRANSPORT EObj 2009421/2009432/2009433 and 2009562..2009565.
        // Keep Anemos' observed floor references; Pagos uses the object's verified base height
        // as the floor reference (mesh grounding still required, live objects take priority).
        // Pagos' southern crystal is BELOW the overlapping -663m surface, at about -731m.
        new(732,2415,new(-138.9335f,-12.29663f,543.2245f),-12.25f),
        new(732,2429,new(-372.0779f,63.81725f,-458.646f),63.81869f),
        new(732,2436,new(435.5492f,34.75242f,-48.08812f),34.78712f),
        new(763,2463,new(-893.7092f,-668.5953f,159.0452f),-668.5953f),
        new(763,2474,new(91.1219f,-730.9799f,303.2021f),-730.9799f),
        new(763,2472,new(-707.0557f,-624.1889f,-318.9147f),-624.1889f),
        new(763,2473,new(345.9848f,-523.1805f,-289.6638f),-523.1805f),
        new(795,2531,new(-253.5f,0,146.8f)), new(795,2540,new(125.7f,0,795.3f)), new(795,2541,new(127.9f,0,-196.1f)), new(795,2542,new(-443.4f,0,-622.7f)),
        new(827,2876,new(-61.7f,0,-875.6f)), new(827,2891,new(-587.3f,0,-148.4f)), new(827,2892,new(781.1f,0,-417.5f)) ];
    // Compare only the distance travelled on foot when route measurements are unavailable.
    public static CrystalRoute? Choose(uint territory, Vector3 player, Vector3 goal, IslandCrystal? source = null,
        IReadOnlySet<uint>? available = null)
    {
        if (!NavigationRules.Finite(player) || !NavigationRules.Finite(goal)) return null;
        var direct = NavigationRules.Distance(player, goal);
        return (from a in All where a.Territory == territory && (source == null || a == source)
            from b in All where b.Territory == territory && a != b && (available == null || available.Contains(b.PlaceNameId))
            let cost = NavigationRules.Distance(player, a.Position) + NavigationRules.Distance(b.Position, goal)
            where direct - cost >= MinimumWalkingSavings
            orderby cost, a.PlaceNameId, b.PlaceNameId
            select new CrystalRoute(a, b, cost)).FirstOrDefault();
    }
}

internal sealed class CrystalPathPlanner(INavigationMesh mesh, uint territory, Vector3 player, Vector3 goal,
    DateTimeOffset started, float nearbyGroundRadius, bool knownHeight = false, Func<Vector3, bool>? destinationFilter = null,
    float? endpointTolerance = null, Func<IslandCrystal, Vector3?>? locateCrystal = null) : IDisposable
{
    private readonly record struct Leg(Vector3 From, Vector3 To);
    private readonly Dictionary<Leg, float> lengths = new();
    private readonly Queue<Leg> pending = new();
    private readonly CancellationTokenSource cancel = new();
    private (IslandCrystal Crystal, Vector3 Ground)[] points = [];
    private IslandCrystal[] sources = [];
    private Vector3 destination;
    private Leg current;
    private Task<List<Vector3>>? path;
    private readonly NavigationEndpoint endpoint = new(mesh);
    private bool initialized;
    public IReadOnlyList<CrystalRoute>? Routes { get; private set; }
    public bool DestinationHasNoGround { get; private set; }
    public string? Decision { get; private set; }

    // Null means measurement was unavailable; empty means no complete route met the savings threshold.
    public bool Tick(DateTimeOffset now)
    {
        if (now - started >= TimeSpan.FromSeconds(10) || !mesh.Ready || mesh.Route.Count > 0) return true;
        if (!initialized)
        {
            if (mesh.Busy) return false;
            initialized = true;
            var samples = NavigationGround.Candidates(mesh, goal, !knownHeight,
                nearbyGroundRadius > 0 ? nearbyGroundRadius : NavigationGround.SearchRadius, true);
            Vector3? ground = samples.Where(p => destinationFilter?.Invoke(p) != false).Select(p => (Vector3?)p).FirstOrDefault();
            if (ground is not { } floor) { DestinationHasNoGround = true; return true; }
            destination = floor;
            points = IslandCrystalRoutes.All.Where(c => c.Territory == territory)
                .Select(c => (Crystal: c, Ground: CrystalGround(c)))
                .Where(c => c.Ground is { } p && NavigationRules.Finite(p))
                .Select(c => (c.Crystal, c.Ground!.Value)).ToArray();
            if (points.Length < 2)
            { Decision = $"仅找到{points.Length}处水晶附近可用地面（先2米，失败后3米），无法完整比较"; return true; }
            sources = points.OrderBy(c => NavigationRules.Distance(player, c.Ground)).Take(2).Select(c => c.Crystal).ToArray();
            QueueLeg(new(player, destination));
            foreach (var point in points)
            {
                if (sources.Contains(point.Crystal)) QueueLeg(new(player, point.Ground));
                QueueLeg(new(point.Ground, destination));
            }
        }

        if (path != null)
        {
            if (!path.IsCompleted) return false;
            var isDestination = current.To == destination;
            endpoint.TakeDestination(out var result, isDestination ? endpointTolerance ?? 3 : .5f);
            if (isDestination && result.Count > 0 && destinationFilter?.Invoke(result[^1]) == false)
                result = [];
            if (result.Count == 0)
            {
                path = endpoint.Next(current.From, cancel.Token);
                if (path != null) return false;
            }
            lengths[current] = result.Count == 0 ? float.PositiveInfinity
                : PathLength(new(current.From, endpoint.Point), result);
            path = null;
        }
        if (mesh.Busy) return false;
        if (pending.TryDequeue(out current))
        {
            // No path query is needed between the same verified floor point and origin.
            // Providers may return no waypoints for this leg; that is not an unreachable source.
            if (Vector3.DistanceSquared(current.From, current.To) < .0001f)
            { lengths[current] = 0; return false; }
            var crystal = points.FirstOrDefault(p => p.Ground == current.To);
            if (crystal.Crystal is { } reference)
            {
                var anchor = locateCrystal?.Invoke(reference) ?? reference.Position with { Y = crystal.Ground.Y };
                endpoint.Start(anchor, false, IslandCrystalRoutes.GroundRadius,
                    p => IslandCrystalRoutes.NearGround(p, anchor, IslandCrystalRoutes.ExpandedGroundRadius), expandOnce: true);
            }
            else endpoint.Start(goal, unknownHeight: !knownHeight,
                nearbyGroundRadius > 0 ? nearbyGroundRadius : NavigationGround.SearchRadius, destinationFilter, expandOnce: true);
            path = endpoint.Next(current.From, cancel.Token);
            if (path == null) lengths[current] = float.PositiveInfinity;
            return false;
        }

        var direct = lengths[new(player, destination)];
        var candidates = (from a in points where sources.Contains(a.Crystal)
            from b in points where a.Crystal != b.Crystal
            let cost = lengths[new(player, a.Ground)] + lengths[new(b.Ground, destination)]
            where float.IsFinite(cost)
            orderby cost, a.Crystal.PlaceNameId, b.Crystal.PlaceNameId
            select new CrystalRoute(a.Crystal, b.Crystal, cost)).ToArray();
        Routes = candidates.Where(r => direct - r.Cost >= IslandCrystalRoutes.MinimumWalkingSavings).ToArray();
        Decision = candidates.Length == 0
            ? $"{points.Length}处水晶地面有效，但未取得完整水晶步行路线，直达={direct:0.0}米"
            : $"直达={direct:0.0}米，最短水晶步行={candidates[0].Cost:0.0}米，需节省{IslandCrystalRoutes.MinimumWalkingSavings:0}米";
        return true;
    }

    private void QueueLeg(Leg leg)
    {
        if (!pending.Contains(leg)) pending.Enqueue(leg);
    }

    private Vector3? CrystalGround(IslandCrystal crystal)
    {
        if (IslandCrystalRoutes.GroundAnchor(crystal, player, mesh, locateCrystal?.Invoke(crystal)) is not { } anchor) return null;
        foreach (var candidate in NavigationGround.Candidates(mesh, anchor, false, IslandCrystalRoutes.GroundRadius, true))
            if (IslandCrystalRoutes.NearGround(candidate, anchor, IslandCrystalRoutes.ExpandedGroundRadius)) return candidate;
        return null;
    }

    private static float PathLength(Leg leg, IReadOnlyList<Vector3> path)
    {
        if (path.Count == 0) return Vector3.Distance(leg.From, leg.To) <= 3 ? Vector3.Distance(leg.From, leg.To) : float.PositiveInfinity;
        if (path.Any(p => !NavigationRules.Finite(p)) || Vector3.Distance(path[^1], leg.To) > 5) return float.PositiveInfinity;
        var previous = leg.From;
        var length = 0f;
        foreach (var point in path)
        {
            length += Vector3.Distance(previous, point);
            previous = point;
        }
        return length;
    }

    public void Dispose()
    {
        cancel.Cancel();
        endpoint.Reset();
        cancel.Dispose();
        if (path != null)
            _ = path.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
