using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Drop-in replacement for PillarRouter.Route using OccupancyBitstack.
///
/// Strategy:
/// (a) FAST: ColumnClearToPlate → straight vertical pillar (~85% case)
/// (b) SHIFT+DROP: try shifting XY within maxShift to find a clear column,
///     then bridge horizontally + drop vertically (avoids BVH bridge search)
/// (c) FALLBACK: PillarRouter.Route (3D planner, BVH-based, for hard minority)
/// </summary>
public static class VerticalFirstRouter
{
    public static PillarRouter.PillarRoute RouteVerticalFirst(
        Vector3 junctionPoint,
        float junctionRadius,
        OccupancyBitstack bitstack,
        AabbBvh bvh,
        PillarRouter.RoutingConfig config)
    {
        // (a) FAST PATH: column clear → straight down
        if (bitstack.ColumnClearToPlate(junctionPoint, junctionRadius))
            return PillarRouter.FastVerticalRoute(junctionPoint, junctionRadius, config);

        // (b) SHIFT+DROP: try 8 directions to find a clear column nearby
        float maxShift = config.MaxBridgeLengthMm * 0.5f;
        int radCells = Math.Max(0, (int)MathF.Ceiling(junctionRadius / bitstack.CellSize));

        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = attempt * MathF.PI * 0.25f;
            float shiftX = MathF.Cos(angle) * maxShift;
            float shiftY = MathF.Sin(angle) * maxShift;
            var shifted = new Vector3(junctionPoint.X + shiftX, junctionPoint.Y + shiftY, junctionPoint.Z);

            if (bitstack.ColumnClearToPlate(shifted, junctionRadius))
            {
                // Build: junction → bridge → shifted position → vertical drop
                var path = new System.Collections.Generic.List<PillarRouter.Waypoint>();
                path.Add(new PillarRouter.Waypoint { Position = junctionPoint, Radius = junctionRadius, Type = "junction" });
                path.Add(new PillarRouter.Waypoint { Position = shifted, Radius = junctionRadius, Type = "bridge" });

                // Vertical descent from shifted position
                var verticalPath = PillarRouter.FastVerticalRoute(shifted, junctionRadius, config);
                path.AddRange(verticalPath.Path);

                return new PillarRouter.PillarRoute
                {
                    Path = path,
                    ReachesGround = true,
                    TotalLength = Vector3.Distance(junctionPoint, shifted) + verticalPath.TotalLength,
                };
            }
        }

        // (c) FALLBACK: full 3D motion planner
        return PillarRouter.Route(junctionPoint, junctionRadius, bvh, config);
    }
}
