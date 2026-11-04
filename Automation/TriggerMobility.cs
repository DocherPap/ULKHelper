using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameAction = Lumina.Excel.Sheets.Action;

namespace ULKHelper.Automation;

internal sealed class TriggerMobility(IObjectTable objects, IDataManager data, INavigationMesh mesh)
{
    // PvE rows verified in the CN Action, GeneralAction and Status sheets.
    private const uint Sprint = 4, Peloton = 7557, Expedient = 25868, Shukuchi = 2262;
    private static readonly uint[] EnemyDashes = [16461, 25762, 7386, 36951, 36926, 7492, 7506, 36934, 24295, 34646];
    private static readonly (uint Action, float Distance)[] ForwardDashes =
        [(16010, 10), (24401, 15), (37008, 15), (34684, 15)];

    // True means a displacement was accepted. Speed buffs leave the current route alone.
    public unsafe bool TryUse(TriggerFrame frame, Vector3 waypoint, TriggerMob? enemy)
    {
        var player = objects.LocalPlayer;
        var manager = ActionManager.Instance();
        if (player == null || manager == null) return false;
        var job = player.ClassJob.RowId;
        var actions = data.GetExcelSheet<GameAction>();
        var displacement = waypoint - frame.Position;
        displacement.Y = 0;
        var distance = displacement.Length();
        if (distance < 3) return false;
        var direction = displacement / distance;
        var hasSpeed = player.StatusList.Any(s => s.StatusId is 50 or 1199 or 2717);

        bool Learned(uint id) => actions.TryGetRow(id, out var row) && row.ClassJob.RowId == job
            && row.ClassJobLevel <= player.Level;
        bool Ready(uint id, ulong target = 0xE0000000) => manager->GetActionStatus(ActionType.Action, id, target) == 0;

        if (!hasSpeed)
        {
            if (frame.InCombat && Learned(Expedient) && Ready(Expedient)
                && manager->UseAction(ActionType.Action, Expedient)) return false;
            if (manager->GetActionStatus(ActionType.GeneralAction, Sprint) == 0
                && manager->UseAction(ActionType.GeneralAction, Sprint)) return false;
            if (!frame.InCombat && job is 23 or 31 or 38 && player.Level >= 20 && Ready(Peloton)
                && manager->UseAction(ActionType.Action, Peloton)) return false;
        }

        if (enemy != null && distance >= 6 && Math.Abs(enemy.Position.Y - frame.Position.Y) <= 2)
        {
            foreach (var id in EnemyDashes)
                if (Learned(id) && distance <= actions.GetRow(id).Range + enemy.HitboxRadius && Ready(id, enemy.Id)
                    && Grounded(frame.Position, direction, Math.Max(0, distance - enemy.HitboxRadius)) != null
                    && manager->UseAction(ActionType.Action, id, enemy.Id)) return true;
        }

        if (Learned(Shukuchi) && Ready(Shukuchi) && distance >= 8
            && Grounded(frame.Position, direction, Math.Min(19, distance - 1)) is { } landing
            && manager->UseActionLocation(ActionType.Action, Shukuchi, location: &landing)) return true;

        var facing = new Vector3(MathF.Sin(player.Rotation), 0, MathF.Cos(player.Rotation));
        if (Vector3.Dot(facing, direction) < .995f) return false;
        foreach (var (id, length) in ForwardDashes)
            if (distance >= length + 1 && Learned(id) && Ready(id)
                && Grounded(frame.Position, facing, length) != null
                && manager->UseAction(ActionType.Action, id)) return true;
        return false;
    }

    private Vector3? Grounded(Vector3 start, Vector3 direction, float distance)
    {
        var segments = Math.Max(1, (int)MathF.Ceiling(distance / 2));
        var previous = start;
        for (var i = 1; i <= segments; i++)
        {
            var sample = start + direction * (distance * i / segments);
            sample.Y = previous.Y;
            if (mesh.Ground(sample, false) is not { } point || !NavigationRules.Finite(point)
                || NavigationRules.Distance(point, sample) > .5f || Math.Abs(point.Y - previous.Y) > .75f)
                return null;
            previous = point;
        }
        return previous;
    }
}
