using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Routes support pillars from pinhead junction points down to the build plate.
///
/// Strategy order (PrusaSlicer-style):
/// 1. Direct descent: beam-cast straight down. If clear → vertical pillar.
/// 2. Bridge-and-descend: Nelder-Mead optimization in (azimuth, slope, length) space
///    to find a bridge that avoids obstacles, then descend from endpoint.
///    Recursive up to 3 junctions for complex geometry.
/// 3. Anchor: last resort — terminate on upward-facing model surface.
/// 4. Reject: if nothing works, return empty path (do NOT force a collision).
/// </summary>
public static class PillarRouter
{
    public sealed class PillarRoute
    {
        public required List<Waypoint> Path { get; init; }
        public required bool ReachesGround { get; init; }
        public Vector3? AnchorPoint { get; init; }
        public Vector3? AnchorNormal { get; init; }
        public required float TotalLength { get; init; }
    }

    public sealed class Waypoint
    {
        public required Vector3 Position { get; init; }
        public required float Radius { get; init; }
        public required string Type { get; init; }
    }

    public sealed record RoutingConfig
    {
        public float BaseZ { get; init; } = 0;
        public float PillarRadiusMm { get; init; } = 0.5f;
        public float BaseRadiusMm { get; init; } = 2.0f;
        public float BaseHeightMm { get; init; } = 0.5f;
        public float WideningFactor { get; init; } = 0.01f;
        public float MaxBridgeLengthMm { get; init; } = 15f;
        public float MaxBridgeSlope { get; init; } = MathF.PI / 4f;
        public int CollisionRays { get; init; } = 8;
    }

    public static PillarRoute Route(Vector3 junctionPoint, float junctionRadius, AabbBvh bvh, RoutingConfig config)
    {
        var path = new List<Waypoint>();
        path.Add(new Waypoint { Position = junctionPoint, Radius = junctionRadius, Type = "junction" });

        // Strategy 1: direct descent
        var direct = TryDirectDescent(junctionPoint, junctionRadius, bvh, config);
        if (direct != null)
        {
            path.AddRange(direct);
            return new PillarRoute { Path = path, ReachesGround = true, TotalLength = ComputePathLength(path) };
        }

        // Strategy 2: bridge-and-descend (optimized search + recursive chaining)
        var bridge = TryBridgeAndDescend(junctionPoint, junctionRadius, bvh, config, maxJunctions: 3);
        if (bridge != null)
        {
            path.AddRange(bridge);
            return new PillarRoute { Path = path, ReachesGround = true, TotalLength = ComputePathLength(path) };
        }

        // Strategy 3: anchor on model surface (last resort)
        var anchor = TryAnchor(junctionPoint, junctionRadius, bvh, config);
        if (anchor.HasValue)
        {
            path.AddRange(anchor.Value.waypoints);
            return new PillarRoute
            {
                Path = path, ReachesGround = false,
                AnchorPoint = anchor.Value.anchorPoint,
                AnchorNormal = anchor.Value.anchorNormal,
                TotalLength = ComputePathLength(path),
            };
        }

        // Reject — do NOT create a colliding support
        return new PillarRoute { Path = path, ReachesGround = false, TotalLength = 0 };
    }

    // ── Strategy 1: Direct descent ────────────────────────────────────

    private static List<Waypoint>? TryDirectDescent(Vector3 start, float radius, AabbBvh bvh, RoutingConfig config)
    {
        float heightToBase = start.Z - config.BaseZ - config.BaseHeightMm;
        if (heightToBase < 0.5f) return BuildVerticalPillar(start, radius, config);

        float clearance = bvh.BeamCast(start, -Vector3.UnitZ, radius, config.CollisionRays, heightToBase);
        if (clearance >= heightToBase - 0.1f)
            return BuildVerticalPillar(start, radius, config);

        return null;
    }

    // ── Strategy 2: Bridge-and-descend with optimized search ──────────

    private static List<Waypoint>? TryBridgeAndDescend(Vector3 start, float radius, AabbBvh bvh,
        RoutingConfig config, int maxJunctions)
    {
        // Phase 1: coarse grid search — 16 azimuth × 3 slopes × 4 lengths = 192 candidates
        // This is wider than the old 8×4=32 grid and also searches multiple slope angles
        Waypoint? bestBridgeEnd = null;
        List<Waypoint>? bestDescentPath = null;
        float bestScore = float.MinValue;

        int azSteps = 16;
        int slopeSteps = 3;
        int lenSteps = 4;

        for (int ai = 0; ai < azSteps; ai++)
        {
            float azimuth = 2f * MathF.PI * ai / azSteps;

            for (int si = 0; si < slopeSteps; si++)
            {
                // Search slopes from 15° to maxSlope (45°)
                float slope = (config.MaxBridgeSlope * 0.3f)
                    + config.MaxBridgeSlope * 0.7f * si / (slopeSteps - 1);

                for (int li = 1; li <= lenSteps; li++)
                {
                    float bridgeLen = config.MaxBridgeLengthMm * li / lenSteps;

                    var result = EvaluateBridge(start, radius, azimuth, slope, bridgeLen, bvh, config);
                    if (result.HasValue && result.Value.score > bestScore)
                    {
                        bestScore = result.Value.score;
                        bestBridgeEnd = result.Value.bridgeEnd;
                        bestDescentPath = result.Value.descentPath;
                    }
                }
            }
        }

        // Phase 2: Nelder-Mead refinement around best candidate (if found but marginal)
        if (bestBridgeEnd == null)
        {
            // Try Nelder-Mead from multiple starting points
            for (int ai = 0; ai < 6; ai++)
            {
                float startAz = 2f * MathF.PI * ai / 6;
                var nmResult = NelderMeadBridge(start, radius, startAz, bvh, config);
                if (nmResult.HasValue && nmResult.Value.score > bestScore)
                {
                    bestScore = nmResult.Value.score;
                    bestBridgeEnd = nmResult.Value.bridgeEnd;
                    bestDescentPath = nmResult.Value.descentPath;
                }
            }
        }

        if (bestBridgeEnd != null && bestDescentPath != null)
        {
            var result = new List<Waypoint> { bestBridgeEnd };
            result.AddRange(bestDescentPath);
            return result;
        }

        // Multi-junction: chain bridges recursively
        if (maxJunctions > 1)
        {
            for (int ai = 0; ai < 12; ai++)
            {
                float azimuth = 2f * MathF.PI * ai / 12;
                float bridgeLen = config.MaxBridgeLengthMm * 0.5f;
                float slope = config.MaxBridgeSlope * 0.7f;

                var bridgeDir = MakeBridgeDir(azimuth, slope);
                var bridgeEnd = start + bridgeDir * bridgeLen;
                if (bridgeEnd.Z < config.BaseZ + 2f) continue;

                float clearance = bvh.BeamCast(start, bridgeDir, radius, config.CollisionRays, bridgeLen);
                if (clearance < bridgeLen - 0.5f) continue;
                if (bvh.IsInside(bridgeEnd)) continue;

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

    private static (Waypoint bridgeEnd, List<Waypoint> descentPath, float score)?
        EvaluateBridge(Vector3 start, float radius, float azimuth, float slope, float length,
            AabbBvh bvh, RoutingConfig config)
    {
        var bridgeDir = MakeBridgeDir(azimuth, slope);
        var bridgeEnd = start + bridgeDir * length;

        if (bridgeEnd.Z < config.BaseZ + config.BaseHeightMm + 1f) return null;

        float clearance = bvh.BeamCast(start, bridgeDir, radius, config.CollisionRays, length);
        if (clearance < length - 0.5f) return null;

        if (bvh.IsInside(bridgeEnd)) return null;

        var descentPath = TryDirectDescent(bridgeEnd, radius, bvh, config);
        if (descentPath == null) return null;

        // Score: prefer lower endpoints (more vertical travel = stable), penalize long bridges
        float score = (start.Z - bridgeEnd.Z) * 2f - length * 0.5f;
        var wp = new Waypoint { Position = bridgeEnd, Radius = radius, Type = "bridge" };
        return (wp, descentPath, score);
    }

    // ── Nelder-Mead bridge optimization ───────────────────────────────

    private static (Waypoint bridgeEnd, List<Waypoint> descentPath, float score)?
        NelderMeadBridge(Vector3 start, float radius, float startAzimuth, AabbBvh bvh, RoutingConfig config)
    {
        // Optimize in 2D: (azimuth, bridgeLength)
        // Slope is fixed at 0.7 * maxSlope (good default)
        float slope = config.MaxBridgeSlope * 0.7f;

        var simplex = new (float az, float len)[3];
        simplex[0] = (startAzimuth, config.MaxBridgeLengthMm * 0.5f);
        simplex[1] = (startAzimuth + 0.5f, config.MaxBridgeLengthMm * 0.8f);
        simplex[2] = (startAzimuth - 0.5f, config.MaxBridgeLengthMm * 0.3f);

        var scores = new float[3];
        (Waypoint? bridgeEnd, List<Waypoint>? descentPath)[] results = new (Waypoint?, List<Waypoint>?)[3];

        for (int i = 0; i < 3; i++)
        {
            var eval = EvaluateBridge(start, radius, simplex[i].az, slope, simplex[i].len, bvh, config);
            scores[i] = eval.HasValue ? -eval.Value.score : 1000f; // minimize negative score
            results[i] = eval.HasValue ? (eval.Value.bridgeEnd, eval.Value.descentPath) : (null, null);
        }

        for (int iter = 0; iter < 30; iter++)
        {
            // Sort
            for (int i = 0; i < 2; i++)
            for (int j = i + 1; j < 3; j++)
            {
                if (scores[j] < scores[i])
                {
                    (simplex[i], simplex[j]) = (simplex[j], simplex[i]);
                    (scores[i], scores[j]) = (scores[j], scores[i]);
                    (results[i], results[j]) = (results[j], results[i]);
                }
            }

            if (results[0].bridgeEnd != null && scores[0] < 0) // valid result with positive score
                return (results[0].bridgeEnd!, results[0].descentPath!, -scores[0]);

            // Centroid of best 2
            float cAz = (simplex[0].az + simplex[1].az) / 2f;
            float cLen = (simplex[0].len + simplex[1].len) / 2f;

            // Reflection
            float rAz = cAz + (cAz - simplex[2].az);
            float rLen = Math.Clamp(cLen + (cLen - simplex[2].len), 1f, config.MaxBridgeLengthMm);
            var rEval = EvaluateBridge(start, radius, rAz, slope, rLen, bvh, config);
            float rScore = rEval.HasValue ? -rEval.Value.score : 1000f;

            if (rScore < scores[1])
            {
                simplex[2] = (rAz, rLen);
                scores[2] = rScore;
                results[2] = rEval.HasValue ? (rEval.Value.bridgeEnd, rEval.Value.descentPath) : (null, null);
            }
            else
            {
                // Shrink
                for (int i = 1; i < 3; i++)
                {
                    simplex[i] = (
                        simplex[0].az + 0.5f * (simplex[i].az - simplex[0].az),
                        Math.Clamp(simplex[0].len + 0.5f * (simplex[i].len - simplex[0].len), 1f, config.MaxBridgeLengthMm)
                    );
                    var sEval = EvaluateBridge(start, radius, simplex[i].az, slope, simplex[i].len, bvh, config);
                    scores[i] = sEval.HasValue ? -sEval.Value.score : 1000f;
                    results[i] = sEval.HasValue ? (sEval.Value.bridgeEnd, sEval.Value.descentPath) : (null, null);
                }
            }
        }

        // Return best
        for (int i = 0; i < 2; i++)
        for (int j = i + 1; j < 3; j++)
            if (scores[j] < scores[i])
            {
                (scores[i], scores[j]) = (scores[j], scores[i]);
                (results[i], results[j]) = (results[j], results[i]);
            }

        if (results[0].bridgeEnd != null)
            return (results[0].bridgeEnd!, results[0].descentPath!, -scores[0]);
        return null;
    }

    private static Vector3 MakeBridgeDir(float azimuth, float slope)
    {
        return Vector3.Normalize(new Vector3(
            MathF.Sin(slope) * MathF.Cos(azimuth),
            MathF.Sin(slope) * MathF.Sin(azimuth),
            -MathF.Cos(slope)
        ));
    }

    // ── Strategy 3: Anchor ────────────────────────────────────────────

    private static (List<Waypoint> waypoints, Vector3 anchorPoint, Vector3 anchorNormal)?
        TryAnchor(Vector3 start, float radius, AabbBvh bvh, RoutingConfig config)
    {
        var hit = bvh.RayCast(start, -Vector3.UnitZ);
        if (!hit.HasValue) return null;
        if (hit.Value.Normal.Z < 0.3f) return null;
        if (hit.Value.Distance < 2.0f) return null;

        var anchorPoint = hit.Value.Point;
        float pillarEndZ = anchorPoint.Z + 0.5f;
        var waypoints = new List<Waypoint>();

        float pillarHeight = start.Z - pillarEndZ;
        if (pillarHeight > 0.5f)
        {
            int segments = Math.Max(1, (int)(pillarHeight / 10f));
            for (int i = 1; i <= segments; i++)
            {
                float t = (float)i / segments;
                waypoints.Add(new Waypoint
                {
                    Position = new Vector3(start.X, start.Y, start.Z - pillarHeight * t),
                    Radius = radius + config.WideningFactor * pillarHeight * t,
                    Type = "pillar",
                });
            }
        }

        waypoints.Add(new Waypoint
        {
            Position = anchorPoint + new Vector3(0, 0, 0.2f),
            Radius = radius * 1.5f,
            Type = "anchor",
        });
        return (waypoints, anchorPoint, hit.Value.Normal);
    }

    // ── Pillar building ───────────────────────────────────────────────

    private static List<Waypoint> BuildVerticalPillar(Vector3 start, float radius, RoutingConfig config)
    {
        var waypoints = new List<Waypoint>();
        float pillarTop = start.Z;
        float pillarBot = config.BaseZ + config.BaseHeightMm;
        float pillarHeight = pillarTop - pillarBot;

        if (pillarHeight < 0.1f)
        {
            waypoints.Add(new Waypoint
            {
                Position = new Vector3(start.X, start.Y, config.BaseZ),
                Radius = config.BaseRadiusMm,
                Type = "base",
            });
            return waypoints;
        }

        int segments = Math.Max(3, (int)(pillarHeight / 10f));
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            waypoints.Add(new Waypoint
            {
                Position = new Vector3(start.X, start.Y, pillarTop - pillarHeight * t),
                Radius = radius + config.WideningFactor * pillarHeight * t,
                Type = "pillar",
            });
        }

        waypoints.Add(new Waypoint
        {
            Position = new Vector3(start.X, start.Y, config.BaseZ),
            Radius = config.BaseRadiusMm,
            Type = "base",
        });

        return waypoints;
    }

    /// <summary>
    /// Fast path: build a straight vertical route without BVH collision checks.
    /// Used by the column occupancy accelerator when the XY column is known to be clear.
    /// </summary>
    public static PillarRoute FastVerticalRoute(Vector3 junctionPoint, float junctionRadius, RoutingConfig config)
    {
        var path = new List<Waypoint>();
        path.Add(new Waypoint { Position = junctionPoint, Radius = junctionRadius, Type = "junction" });
        path.AddRange(BuildVerticalPillar(junctionPoint, junctionRadius, config));
        return new PillarRoute { Path = path, ReachesGround = true, TotalLength = junctionPoint.Z - config.BaseZ };
    }

    private static float ComputePathLength(List<Waypoint> path)
    {
        float len = 0;
        for (int i = 1; i < path.Count; i++)
            len += Vector3.Distance(path[i - 1].Position, path[i].Position);
        return len;
    }
}
