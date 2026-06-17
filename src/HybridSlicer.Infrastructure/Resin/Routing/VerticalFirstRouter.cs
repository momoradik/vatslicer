using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Drop-in replacement for PillarRouter.Route that uses OccupancyBitstack
/// for O(1) vertical clearance checks.
///
/// Strategy (ChiTuBox/Lychee fast path):
/// (a) FAST PATH: if ColumnClearToPlate → straight vertical pillar (85% of supports)
/// (b) SUPPORT-ON-SUPPORT: land on nearby existing pillar body (TODO: Task 4)
/// (c) FALLBACK: existing PillarRouter.Route (3D motion planner for hard minority)
///
/// Returns PillarRoute (same type) so it's a drop-in replacement.
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
        // (a) FAST PATH: bitstack column clearance check
        if (bitstack.ColumnClearToPlate(junctionPoint, junctionRadius))
        {
            return PillarRouter.FastVerticalRoute(junctionPoint, junctionRadius, config);
        }

        // (c) FALLBACK: full 3D motion planner for the hard minority
        return PillarRouter.Route(junctionPoint, junctionRadius, bvh, config);
    }
}
