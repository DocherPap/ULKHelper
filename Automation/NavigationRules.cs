using System.Numerics;

namespace ULKHelper.Automation;

internal sealed record PlayerIdentity(string Name, uint HomeWorld);
internal sealed record PlayerEntry(PlayerIdentity Identity, string WorldName, Vector3 Position, float Distance, bool IsLoaded = true);
internal sealed record MapDestination(uint Territory, uint Map, Vector3 Position, string Label);
internal sealed record FateEntry(ushort Id, string Name, Vector3 Position, int StartedAt = 0);
internal readonly record struct WorldContext(uint Territory, uint Map, uint Instance, ulong PlayerId);

internal static class NavigationRules
{
    public static WorldContext TransferContext(WorldContext current, WorldContext previous, bool loggedIn, bool transferring, bool loading)
        => loggedIn && transferring && loading && (current.PlayerId == previous.PlayerId || current.PlayerId == 0)
            && (current.Territory == previous.Territory || current.Territory == 0) && (current.Map == previous.Map || current.Map == 0)
            && current.Instance == previous.Instance ? previous : current;

    // Local Chinese Fate sheet 2026.10.06: Eureka=1 / Rule=1 are the 67 NMs;
    // the five Happy Bunny events use Rule=4. Other Eureka values describe other content.
    public static bool IsEurekaNm(byte eureka, byte rule) => eureka == 1 && rule == 1;
    public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public static float Distance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    public static bool Overlaps(Vector3 player, Vector3 destination) => Distance(player, destination) <= 25;
    public static FateEntry? FindFate(Vector3 destination, IEnumerable<FateEntry> fates)
        => fates.Where(f => Finite(f.Position) && Distance(destination, f.Position) <= 50)
            .OrderBy(f => Distance(destination, f.Position)).ThenBy(f => f.Id).FirstOrDefault();

    // Stop on the near side of the centre; a coincident player never produces NaN.
    public static Vector3 StandOff(Vector3 player, Vector3 centre, float radius)
    {
        var delta = new Vector2(player.X - centre.X, player.Z - centre.Z);
        if (delta.LengthSquared() < .0001f) return player;
        var direction = Vector2.Normalize(delta);
        return new(centre.X + direction.X * radius, centre.Y, centre.Z + direction.Y * radius);
    }

    // vnavmesh consumes waypoints from the front. Only stop an unchanged suffix of our own route.
    public static bool OwnsRoute(IReadOnlyList<Vector3> current, IReadOnlyList<Vector3> issued)
    {
        if (current.Count == 0 || current.Count > issued.Count) return false;
        var offset = issued.Count - current.Count;
        for (var i = 0; i < current.Count; i++)
            if (Vector3.DistanceSquared(current[i], issued[offset + i]) > .0001f) return false;
        return true;
    }
}
