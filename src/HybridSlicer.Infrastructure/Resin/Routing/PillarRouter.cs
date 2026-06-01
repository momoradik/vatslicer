using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Routes support pillars from pinhead junction points down to the build plate,
/// navigating around model geometry.
///
/// Strategy (like PrusaSlicer):
/// 1. Direct descent: beam-cast straight down. If clear → vertical pillar.
/// 2. Bridge-and-descend: search for a bridge direction that avoids obstacles,
///    then descend vertically from the bridge endpoint.
/// 3. Multi-junction: chain up to 3 bridge segments for complex geometry.
/// 4. Anchor: if ground unreachable, anchor on upward-facing model surface.
///
/// Features:
/// - Pillar widening: radius increases toward base (2% per mm, configurable)
/// - BVH beam-cast collision for full pillar volume
/// - Recorded path as sequence of (point, radius) for mesh generation
/// </summary>
public static class PillarRouter
{
    /// <summary>
    /// A routed pillar path from junction to base.
    /// </summary>
    public sealed class PillarRoute
    {
        /// <summary>Sequence of waypoints from top to bottom.</summary>
        public required List<Waypoint> Path { get; init; }
        public required bool ReachesGround { get; init; }
        /// <summary>If not reaching ground, this is the anchor point on the model surface.</summary>
        public Vector3? AnchorPoint { get; init; }
        public Vector3? AnchorNormal { get; init; }
        public required float TotalLength { get; init; }
    }

    public sealed class Waypoint
    {
        public required Vector3 Position { get; init; }
        public required float Radius { get; init; }
        /// <summary>Type: "junction", "bridge", "pillar", "base", "anchor"</summary>
        public required string Type { get; init; }
    }

    public sealed record RoutingConfig
    {
        public float BaseZ { get; init; } = 0;
        public float PillarRadiusMm { get; init; } = 0.5f;
        public float BaseRadiusMm { get; init; } = 2.0f;
        public float BaseHeightMm { get; init; } = 0.5f;
        /// <summary>Radius increase per mm of descent. 0 = constant radius.</summary>
        public float WideningFactor { get; init; } = 0.01f;
        /// <summary>Max bridge length (mm).</summary>
        public float MaxBridgeLengthMm { get; init; } = 15f;
        /// <summary>Max angle from vertical for bridges (radians). Default 45 deg.</summary>
        public float MaxBridgeSlope { get; init; } = MathF.PI / 4f;
        /// <summary>Number of rays for beam collision check.</summary>
        public int CollisionRays { get; init; } = 8;
    }

    /// <summary>
    /// Route a pillar from the junction point down to the build plate.
    /// </summary>
    public static PillarRoute Route(Vector3 junctionPoint, float junctionRadius, AabbBvh bvh, RoutingConfig config)
    {
        var path = new List<Waypoint>();
        path.Add(new Waypoint { Position = junctionPoint, Radius = junctionRadius, Type = "junction" });

        // Strategy 1: Try direct descent (fast — single beam-cast)
        var directResult = TryDirectDescent(junctionPoint, junctionRadius, bvh, config);
        if (directResult != null)
        {
            path.AddRange(directResult);
            return new PillarRoute { Path = path, ReachesGround = true, TotalLength = ComputePathLength(path) };
        }

        // Strategy 2: Try anchor on model surface below (single ray-cast)
        var anchorResult = TryAnchor(junctionPoint, junctionRadius, bvh, config);
        if (anchorResult.HasValue)
        {
            var ar = anchorResult.Value;
            path.AddRange(ar.waypoints);
            return new PillarRoute
            {
                Path = path, ReachesGround = false,
                AnchorPoint = ar.anchorPoint, AnchorNormal = ar.anchorNormal,
                TotalLength = ComputePathLength(path),
            };
        }

        // Strategy 3: Try bridge-and-descend (multi-junction for complex geometry)
        var bridgeResult = TryBridgeAndDescend(junctionPoint, junctionRadius, bvh, config, maxJunctions: 2);
        if (bridgeResult != null)
        {
            path.AddRange(bridgeResult);
            return new PillarRoute { Path = path, ReachesGround = true, TotalLength = ComputePathLength(path) };
        }

        // Fallback: direct descent (may collide but still usable)
        path.AddRange(BuildVerticalPillar(junctionPoint, junctionRadius, config));
        return new PillarRoute { Path = path, ReachesGround = true, TotalLength = ComputePathLength(path) };
    }

    // ── Strategy 1: Direct descent ───────────────────────────────────────

    private static List<Waypoint>? TryDirectDescent(Vector3 start, float radius, AabbBvh bvh, RoutingConfig config)
    {
        float heightToBase = start.Z - config.BaseZ - config.BaseHeightMm;
        if (heightToBase < 0.5f) return BuildVerticalPillar(start, radius, config);

        // Beam-cast straight down from junction to base
        float clearance = bvh.BeamCast(start, -Vector3.UnitZ, radius, config.CollisionRays, heightToBase);
        if (clearance >= heightToBase - 0.1f)
        {
            // Clear path — build vertical pillar with widening
            return BuildVerticalPillar(start, radius, config);
        }

        return null; // blocked
    }

    // ── Strategy 2: Bridge-and-descend ───────────────────────────────────

    private static List<Waypoint>? TryBridgeAndDescend(Vector3 start, float radius, AabbBvh bvh,
        RoutingConfig config, int maxJunctions)
    {
        // Search over azimuth directions for a bridge that clears the obstruction
        int azimuthSteps = 8;
        int lengthSteps = 4;
        float slopeAngle = config.MaxBridgeSlope;

        Waypoint? bestBridgeEnd = null;
        List<Waypoint>? bestDescentPath = null;
        float bestEndZ = float.MaxValue; // prefer lower endpoints (closer to ground)

        for (int ai = 0; ai < azimuthSteps; ai++)
        {
            float azimuth = 2f * MathF.PI * ai / azimuthSteps;

            for (int li = 1; li <= lengthSteps; li++)
            {
                float bridgeLen = config.MaxBridgeLengthMm * li / lengthSteps;

                // Bridge direction: angle downward at max slope in the given azimuth
                var bridgeDir = new Vector3(
                    MathF.Sin(slopeAngle) * MathF.Cos(azimuth),
                    MathF.Sin(slopeAngle) * MathF.Sin(azimuth),
                    -MathF.Cos(slopeAngle)
                );
                bridgeDir = Vector3.Normalize(bridgeDir);

                var bridgeEnd = start + bridgeDir * bridgeLen;
                if (bridgeEnd.Z < config.BaseZ + config.BaseHeightMm + 1f) continue; // too low

                // Check bridge path clearance
                float bridgeClearance = bvh.BeamCast(start, bridgeDir, radius, config.CollisionRays, bridgeLen);
                if (bridgeClearance < bridgeLen - 0.5f) continue; // bridge blocked

                // Check if the bridge endpoint is inside the mesh
                if (bvh.IsInside(bridgeEnd)) continue;

                // Try direct descent from bridge endpoint
                var descentPath = TryDirectDescent(bridgeEnd, radius, bvh, config);
                if (descentPath != null && bridgeEnd.Z < bestEndZ)
                {
                    bestEndZ = bridgeEnd.Z;
                    bestBridgeEnd = new Waypoint { Position = bridgeEnd, Radius = radius, Type = "bridge" };
                    bestDescentPath = descentPath;
                }
            }
        }

        if (bestBridgeEnd != null && bestDescentPath != null)
        {
            var result = new List<Waypoint> { bestBridgeEnd };
            result.AddRange(bestDescentPath);
            return result;
        }

        // Multi-junction: try chaining bridges (recursive, up to maxJunctions)
        if (maxJunctions > 1)
        {
            for (int ai = 0; ai < azimuthSteps; ai++)
            {
                float azimuth = 2f * MathF.PI * ai / azimuthSteps;
                float bridgeLen = config.MaxBridgeLengthMm * 0.6f; // shorter bridges for chaining

                var bridgeDir = new Vector3(
                    MathF.Sin(slopeAngle) * MathF.Cos(azimuth),
                    MathF.Sin(slopeAngle) * MathF.Sin(azimuth),
                    -MathF.Cos(slopeAngle)
                );
                bridgeDir = Vector3.Normalize(bridgeDir);

                var bridgeEnd = start + bridgeDir * bridgeLen;
                if (bridgeEnd.Z < config.BaseZ + 2f) continue;

                float clearance = bvh.BeamCast(start, bridgeDir, radius, config.CollisionRays, bridgeLen);
                if (clearance < bridgeLen - 0.5f) continue;
                if (bvh.IsInside(bridgeEnd)) continue;

                // Recursively try to route from bridge endpoint
                var subRoute = TryBridgeAndDescend(bridgeEnd, radius, bvh, config, maxJunctions - 1);
                if (subRoute != null)
                {
                    var result = new List<Waypoint>
                    {
                        new() { Position = bridgeEnd, Radius = radius, Type = "bridge" }
                    };
                    result.AddRange(subRoute);
                    return result;
                }
            }
        }

        return null;
    }

    // ── Strategy 3: Anchor on model ──────────────────────────────────────

    private static (List<Waypoint> waypoints, Vector3 anchorPoint, Vector3 anchorNormal)?
        TryAnchor(Vector3 start, float radius, AabbBvh bvh, RoutingConfig config)
    {
        // Cast ray downward to find model surface below
        var hit = bvh.RayCast(start, -Vector3.UnitZ);
        if (!hit.HasValue) return null;

        // Verify the hit surface is upward-facing (can anchor on top of it)
        if (hit.Value.Normal.Z < 0.3f) return null; // surface too steep for anchor

        // Verify the anchor point isn't too close to the start (need some pillar length)
        float distance = hit.Value.Distance;
        if (distance < 2.0f) return null; // too close

        var anchorPoint = hit.Value.Point;
        var anchorNormal = hit.Value.Normal;

        // Build a short pillar from junction to just above anchor point
        float pillarEndZ = anchorPoint.Z + 0.5f; // 0.5mm above surface
        var waypoints = new List<Waypoint>();

        // Pillar segments with widening
        float pillarHeight = start.Z - pillarEndZ;
        if (pillarHeight > 0.5f)
        {
            int segments = Math.Max(1, (int)(pillarHeight / 10f));
            for (int i = 1; i <= segments; i++)
            {
                float t = (float)i / segments;
                float z = start.Z - pillarHeight * t;
                float r = radius + config.WideningFactor * pillarHeight * t;
                waypoints.Add(new Waypoint { Position = new Vector3(start.X, start.Y, z), Radius = r, Type = "pillar" });
            }
        }

        waypoints.Add(new Waypoint { Position = anchorPoint + new Vector3(0, 0, 0.2f), Radius = radius * 1.5f, Type = "anchor" });
        return (waypoints, anchorPoint, anchorNormal);
    }

    // ── Pillar building ──────────────────────────────────────────────────

    private static List<Waypoint> BuildVerticalPillar(Vector3 start, float radius, RoutingConfig config)
    {
        var waypoints = new List<Waypoint>();
        float pillarTop = start.Z;
        float pillarBot = config.BaseZ + config.BaseHeightMm;
        float pillarHeight = pillarTop - pillarBot;

        if (pillarHeight < 0.1f)
        {
            // Very short — just base
            waypoints.Add(new Waypoint
            {
                Position = new Vector3(start.X, start.Y, config.BaseZ),
                Radius = config.BaseRadiusMm,
                Type = "base",
            });
            return waypoints;
        }

        // Pillar segments with widening (every 10mm or at least 3 segments)
        int segments = Math.Max(3, (int)(pillarHeight / 10f));
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float z = pillarTop - pillarHeight * t;
            float wideningR = radius + config.WideningFactor * pillarHeight * t;
            waypoints.Add(new Waypoint
            {
                Position = new Vector3(start.X, start.Y, z),
                Radius = wideningR,
                Type = "pillar",
            });
        }

        // Base pedestal
        waypoints.Add(new Waypoint
        {
            Position = new Vector3(start.X, start.Y, config.BaseZ),
            Radius = config.BaseRadiusMm,
            Type = "base",
        });

        return waypoints;
    }

    private static float ComputePathLength(List<Waypoint> path)
    {
        float len = 0;
        for (int i = 1; i < path.Count; i++)
            len += Vector3.Distance(path[i - 1].Position, path[i].Position);
        return len;
    }
}
