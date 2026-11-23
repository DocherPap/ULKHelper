using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace ULKHelper.Automation;

internal static class CliffCollisionAdapter
{
    // Called only on the framework thread, immediately before leaving the ledge.
    // Missing/streaming collision is unknown, never permission to drop.
    public static unsafe bool? Clear(CliffDrop drop)
    {
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null) return null;
        var module = framework->BGCollisionModule;
        if (module->ShuttingDown || module->SceneManager == null || module->LoadInProgressCounter != 0) return null;
        if (!IslandCliffRoutes.Valid(drop) || !Floor(drop.Approach) || !Floor(drop.Landing)) return false;
        var direction = drop.Landing - drop.Approach; direction.Y = 0;
        direction = Vector3.Normalize(direction);
        var lateral = new Vector3(-direction.Z, 0, direction.X) * .35f;
        foreach (var height in new[] { 1f, 1.8f })
        foreach (var offset in new[] { Vector3.Zero, lateral, -lateral })
        {
            var start = drop.Approach + Vector3.UnitY * height + offset;
            var over = new Vector3(drop.Landing.X, drop.Approach.Y + height, drop.Landing.Z) + offset;
            var bottom = drop.Landing + Vector3.UnitY * height + offset;
            if (!ClearSegment(start, over) || !ClearSegment(over, bottom) || !ClearSegment(start, bottom)) return false;
        }
        return true;
    }
    private static bool Floor(Vector3 point) => BGCollisionModule.RaycastMaterialFilter(point + Vector3.UnitY * 3,
        -Vector3.UnitY, out var hit, 6) && Vector3.Distance(hit.Point, point) <= 1.25f && hit.Normal.Y >= .6f;
    private static bool ClearSegment(Vector3 start, Vector3 end)
    {
        var delta = end - start;
        var length = delta.Length();
        return length < .05f || !BGCollisionModule.RaycastMaterialFilter(start, delta / length, out _, length - .05f);
    }
}
