using System.Numerics;

namespace ULKHelper.Automation;

internal sealed record ArrivalParticipant(ulong Id, Vector3 Position, bool Alive, bool Party, bool Attacking);
internal sealed record ArrivalFate(ushort Id, int StartedAt, Vector3 Centre, float Radius, bool? NeedsSync, float? HpRatio,
    IReadOnlyList<ArrivalParticipant> Participants, int? IslandPlayerCount = null, bool RequireIslandRoster = false,
    ulong BossId = 0, bool BossAlive = true, bool BossTargetable = false, bool Synced = false, Vector3? BossPosition = null);
internal sealed record ArrivalFrame(WorldContext World, Vector3 Player, bool Safe, bool Mounted, bool Casting, bool InCombat, ArrivalFate? Fate);
internal sealed record PullDecision(bool Started, int Players, int Attackers, int PartyAttackers);

internal static class ArrivalRules
{
    public static int? BossIndex(IReadOnlyList<(bool Alive, bool Targetable)> candidates)
    {
        var selectable = Enumerable.Range(0, candidates.Count).Where(i => candidates[i] is (true, true)).ToArray();
        if (selectable.Length == 1) return selectable[0];
        if (selectable.Length > 1) return null;
        var alive = Enumerable.Range(0, candidates.Count).Where(i => candidates[i].Alive).ToArray();
        return alive.Length == 1 ? alive[0] : candidates.Count == 1 ? 0 : null;
    }
    public static bool? NeedsSync(int? elementalLevel, int maxLevel, bool alreadySynced)
        => alreadySynced ? true : elementalLevel is >= 1 and <= 60 && maxLevel is >= 1 and <= 60
            ? elementalLevel > maxLevel : null;

    public static PullDecision IsPulled(ArrivalFate fate)
    {
        // Use the loaded, living participants near this FATE, not the whole map or party.
        var people = fate.Participants.Where(p => p.Alive && NavigationRules.Finite(p.Position)
            && NavigationRules.Distance(p.Position, fate.Centre) <= Math.Max(50, fate.Radius))
            .DistinctBy(p => p.Id).ToArray();
        var attackers = people.Count(p => p.Attacking);
        var party = people.Count(p => p.Attacking && p.Party);
        var denominator = people.Length;
        return new(denominator > 0 && attackers * 3 >= denominator * 2 || fate.HpRatio is >= 0 and < .8f || party >= 2,
            denominator, attackers, party);
    }

    public static Vector3 Position(Vector3 player, Vector3 centre, float distance)
    {
        // At the exact centre there is no near-side direction; choose a finite escape point.
        if (NavigationRules.Distance(player, centre) < .01f) return centre + new Vector3(distance, 0, 0);
        return NavigationRules.StandOff(player, centre, distance);
    }
}

internal interface IArrivalActions
{
    bool Mount();
    bool Dismount();
}

internal enum TravelMountResult { Waiting, Mounted, InCombat, Failed }

// Framework-thread polling shared by travel and remote waiting. Each caller owns one attempt state.
internal sealed class TravelMountHelper(IArrivalActions actions)
{
    private int attempts;
    private DateTimeOffset next;
    public void Reset() { attempts = 0; next = default; }

    public TravelMountResult Tick(bool mounted, bool casting, bool inCombat, DateTimeOffset now)
    {
        if (inCombat) return TravelMountResult.InCombat;
        if (casting) return TravelMountResult.Waiting;
        if (mounted) return TravelMountResult.Mounted;
        if (now < next) return TravelMountResult.Waiting;
        if (attempts >= 3) return TravelMountResult.Failed;
        attempts++;
        next = now.AddSeconds(3);
        actions.Mount();
        return TravelMountResult.Waiting;
    }
}
