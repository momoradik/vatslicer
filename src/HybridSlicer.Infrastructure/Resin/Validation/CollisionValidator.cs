using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Validation;

/// <summary>
/// Validates support geometry against the model mesh for collisions.
///
/// Uses BVH beam-cast (8-16 rays per check) to test the full volume of
/// each support element — not just the centerline. Checks:
/// - Pinhead path (pin sphere → back sphere → junction)
/// - Pillar path (junction → base, including widening)
/// - Bridge paths (any bridge-and-descend segments)
/// - Pedestal (base cone)
/// - Cross-connections between pillars
///
/// Reports collision points with penetration depth for diagnostics.
/// </summary>
public static class CollisionValidator
{
    public sealed class CollisionIssue
    {
        public required string SupportId { get; init; }
        public required string Element { get; init; } // "pinhead", "pillar", "bridge", "pedestal", "interconnect"
        public required Vector3 CollisionPoint { get; init; }
        public required float PenetrationDepth { get; init; }
        public required string Description { get; init; }
    }

    public sealed class CollisionResult
    {
        public required int TotalSupportsChecked { get; init; }
        public required int CollisionFreeSupports { get; init; }
        public required int CollidingSupports { get; init; }
        public required int TotalCollisionPoints { get; init; }
        public required List<CollisionIssue> Issues { get; init; }
        public required long ElapsedMs { get; init; }
    }

    /// <summary>
    /// Validate pinhead placements against the mesh.
    /// </summary>
    public static List<CollisionIssue> ValidatePinheads(
        List<(string id, PinheadOptimizer.Pinhead pinhead)> pinheads, AabbBvh bvh, int beamRays = 16)
    {
        var issues = new List<CollisionIssue>();

        foreach (var (id, ph) in pinheads)
        {
            if (!ph.IsValid) continue;

            // Check back sphere region — must not be inside mesh
            if (bvh.IsInside(ph.BackCenter))
            {
                issues.Add(new CollisionIssue
                {
                    SupportId = id, Element = "pinhead",
                    CollisionPoint = ph.BackCenter, PenetrationDepth = 0,
                    Description = "Back sphere center is inside mesh",
                });
                continue;
            }

            // Check junction point — must not be inside mesh
            if (bvh.IsInside(ph.JunctionPoint))
            {
                issues.Add(new CollisionIssue
                {
                    SupportId = id, Element = "pinhead",
                    CollisionPoint = ph.JunctionPoint, PenetrationDepth = 0,
                    Description = "Junction point is inside mesh",
                });
                continue;
            }

            // Beam-cast along the pinhead direction, starting from the BACK sphere
            // (skip the pin sphere and connecting cone — those are intentionally near the model surface)
            var dir = Vector3.Normalize(ph.JunctionPoint - ph.ContactPoint);
            float checkLen = Vector3.Distance(ph.BackCenter, ph.JunctionPoint);
            if (checkLen < 0.1f) continue;

            // Only check the back sphere → junction segment (away from model surface)
            float clearance = bvh.BeamCast(ph.BackCenter, dir, ph.BackRadius * 0.5f, beamRays / 2, checkLen);

            if (clearance < checkLen * 0.8f)
            {
                var collisionPt = ph.BackCenter + dir * clearance;
                issues.Add(new CollisionIssue
                {
                    SupportId = id, Element = "pinhead",
                    CollisionPoint = collisionPt,
                    PenetrationDepth = checkLen - clearance,
                    Description = $"Pinhead back-to-junction collides at {clearance:F1}mm of {checkLen:F1}mm",
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Validate pillar routes against the mesh.
    /// </summary>
    public static List<CollisionIssue> ValidatePillarRoutes(
        List<(string id, PillarRouter.PillarRoute route)> routes, AabbBvh bvh, int beamRays = 8)
    {
        var issues = new List<CollisionIssue>();

        foreach (var (id, route) in routes)
        {
            for (int i = 0; i < route.Path.Count - 1; i++)
            {
                var wp1 = route.Path[i];
                var wp2 = route.Path[i + 1];
                float segLen = Vector3.Distance(wp1.Position, wp2.Position);
                if (segLen < 0.05f) continue;

                var dir = Vector3.Normalize(wp2.Position - wp1.Position);
                float radius = Math.Max(wp1.Radius, wp2.Radius);
                string element = wp1.Type == "bridge" || wp2.Type == "bridge" ? "bridge" :
                                 wp2.Type == "base" ? "pedestal" : "pillar";

                // Check if endpoint is inside the mesh (skip base waypoints — they sit on the build plate,
                // which can be at Z≈0 right at the model's bottom surface after centering)
                if (wp2.Type != "base" && bvh.IsInside(wp2.Position))
                {
                    issues.Add(new CollisionIssue
                    {
                        SupportId = id, Element = element,
                        CollisionPoint = wp2.Position, PenetrationDepth = 0,
                        Description = $"Waypoint ({wp2.Type}) is inside mesh",
                    });
                    continue;
                }

                // Beam-cast along the segment
                float clearance = bvh.BeamCast(wp1.Position, dir, radius, beamRays, segLen);
                if (clearance < segLen * 0.95f)
                {
                    var collisionPt = wp1.Position + dir * clearance;
                    issues.Add(new CollisionIssue
                    {
                        SupportId = id, Element = element,
                        CollisionPoint = collisionPt,
                        PenetrationDepth = segLen - clearance,
                        Description = $"{element} segment collides at {clearance:F1}mm of {segLen:F1}mm",
                    });
                }
            }

            // Check for inside-mesh at several sample points along the pillar
            var pillarWaypoints = route.Path.Where(w => w.Type == "pillar").ToList();
            foreach (var wp in pillarWaypoints)
            {
                if (bvh.IsInside(wp.Position))
                {
                    issues.Add(new CollisionIssue
                    {
                        SupportId = id, Element = "pillar",
                        CollisionPoint = wp.Position, PenetrationDepth = 0,
                        Description = "Pillar waypoint is inside mesh interior",
                    });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Validate interconnections (cross-braces) against the mesh.
    /// </summary>
    public static List<CollisionIssue> ValidateInterconnections(
        List<InterconnectBuilder.Interconnection> interconnections, AabbBvh bvh, int beamRays = 8)
    {
        var issues = new List<CollisionIssue>();

        foreach (var conn in interconnections)
        {
            float segLen = Vector3.Distance(conn.PointA, conn.PointB);
            if (segLen < 0.05f) continue;

            var dir = Vector3.Normalize(conn.PointB - conn.PointA);
            float clearance = bvh.BeamCast(conn.PointA, dir, conn.Radius, beamRays, segLen);

            if (clearance < segLen * 0.9f)
            {
                var collisionPt = conn.PointA + dir * clearance;
                issues.Add(new CollisionIssue
                {
                    SupportId = $"pair-{conn.PillarA}-{conn.PillarB}",
                    Element = "interconnect",
                    CollisionPoint = collisionPt,
                    PenetrationDepth = segLen - clearance,
                    Description = $"Cross-connection ({conn.Type}) collides with mesh",
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Run full collision validation across all support elements.
    /// </summary>
    public static CollisionResult ValidateAll(
        List<(string id, PinheadOptimizer.Pinhead pinhead)> pinheads,
        List<(string id, PillarRouter.PillarRoute route)> routes,
        List<InterconnectBuilder.Interconnection> interconnections,
        AabbBvh bvh)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allIssues = new List<CollisionIssue>();
        allIssues.AddRange(ValidatePinheads(pinheads, bvh));
        allIssues.AddRange(ValidatePillarRoutes(routes, bvh));
        allIssues.AddRange(ValidateInterconnections(interconnections, bvh));

        var collidingIds = new HashSet<string>(allIssues.Select(i => i.SupportId));
        int totalSupports = pinheads.Count;

        sw.Stop();
        return new CollisionResult
        {
            TotalSupportsChecked = totalSupports,
            CollisionFreeSupports = totalSupports - collidingIds.Count,
            CollidingSupports = collidingIds.Count,
            TotalCollisionPoints = allIssues.Count,
            Issues = allIssues,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
