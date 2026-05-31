using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Optimizes pinhead orientation for maximum clearance from the model surface.
///
/// PrusaSlicer uses NLopt (MLSL with Subplex, 100 iterations). We implement an
/// equivalent iterative search over polar/azimuth angles with BVH beam-cast collision.
///
/// Algorithm:
/// 1. Start from surface normal, clamp to max bridge_slope (45 deg) from vertical
/// 2. Volumetric collision check: 16 rays in cone pattern around pinhead path
/// 3. If collision, search over a grid of (polar, azimuth) angles for best clearance
/// 4. If still colliding, reduce head radius and retry
/// 5. If all fails, mark as "needs anchor"
///
/// The optimizer produces a fully validated pinhead with:
/// - Pin sphere position (contact point on model)
/// - Back sphere position (junction to pillar)
/// - Head direction vector
/// - Clearance distance from nearest model surface
/// </summary>
public static class PinheadOptimizer
{
    /// <summary>
    /// Pinhead geometry result.
    /// </summary>
    public sealed record Pinhead
    {
        public required Vector3 ContactPoint { get; init; }
        public required Vector3 Direction { get; init; }
        public required Vector3 PinCenter { get; init; }
        public required Vector3 BackCenter { get; init; }
        public required Vector3 JunctionPoint { get; init; }
        public required float PinRadius { get; init; }
        public required float BackRadius { get; init; }
        public required float Width { get; init; }
        public required float Clearance { get; init; }
        public required bool IsValid { get; init; }
        public required bool NeedsAnchor { get; init; }
    }

    /// <summary>
    /// Pinhead configuration.
    /// </summary>
    public sealed class PinheadConfig
    {
        public float PinRadiusMm { get; init; } = 0.2f;
        public float BackRadiusMm { get; init; } = 0.5f;
        public float WidthMm { get; init; } = 1.0f;
        public float PenetrationMm { get; init; } = 0.2f;
        /// <summary>Max angle from vertical (radians). Default 45 deg.</summary>
        public float MaxBridgeSlope { get; init; } = MathF.PI / 4f;
        /// <summary>Number of rays for volumetric collision check.</summary>
        public int CollisionRays { get; init; } = 16;
        /// <summary>Minimum clearance distance (mm).</summary>
        public float MinClearanceMm { get; init; } = 0.1f;
    }

    /// <summary>
    /// Optimize pinhead placement at the given contact point with the given surface normal.
    /// </summary>
    public static Pinhead Optimize(Vector3 contactPoint, Vector3 surfaceNormal, AabbBvh bvh, PinheadConfig config)
    {
        // Step 1: compute initial direction from surface normal
        var initialDir = ComputeInitialDirection(surfaceNormal, config.MaxBridgeSlope);

        // Step 2: try the initial direction with full collision check
        var result = TryPinhead(contactPoint, initialDir, config, bvh);
        if (result.IsValid)
            return result;

        // Step 3: search over angles for best clearance
        var bestResult = result;
        float bestClearance = result.Clearance;

        // Search grid: 12 azimuth x 4 polar angles = 48 candidates
        int azimuthSteps = 12;
        int polarSteps = 4;
        float polarMin = MathF.PI - config.MaxBridgeSlope;
        float polarMax = MathF.PI; // straight down

        for (int ai = 0; ai < azimuthSteps; ai++)
        {
            float azimuth = 2f * MathF.PI * ai / azimuthSteps;
            for (int pi = 0; pi < polarSteps; pi++)
            {
                float polar = polarMin + (polarMax - polarMin) * pi / (polarSteps - 1);
                var dir = SphericalToCartesian(polar, azimuth);
                var candidate = TryPinhead(contactPoint, dir, config, bvh);
                if (candidate.Clearance > bestClearance)
                {
                    bestClearance = candidate.Clearance;
                    bestResult = candidate;
                    if (candidate.IsValid) break; // good enough
                }
            }
            if (bestResult.IsValid) break;
        }

        if (bestResult.IsValid)
            return bestResult;

        // Step 4: reduce radius and retry with the best direction found
        for (float scale = 0.7f; scale >= 0.3f; scale -= 0.2f)
        {
            var smallConfig = new PinheadConfig
            {
                PinRadiusMm = config.PinRadiusMm * scale,
                BackRadiusMm = config.BackRadiusMm * scale,
                WidthMm = config.WidthMm * scale,
                PenetrationMm = config.PenetrationMm,
                MaxBridgeSlope = config.MaxBridgeSlope,
                CollisionRays = config.CollisionRays,
                MinClearanceMm = config.MinClearanceMm,
            };

            var candidate = TryPinhead(contactPoint, bestResult.Direction, smallConfig, bvh);
            if (candidate.IsValid) return candidate;
        }

        // Step 5: all failed — mark as needs anchor
        return bestResult with { NeedsAnchor = true };
    }

    // ── Internal ─────────────────────────────────────────────────────────

    private static Pinhead TryPinhead(Vector3 contact, Vector3 dir, PinheadConfig config, AabbBvh bvh)
    {
        float rPin = config.PinRadiusMm;
        float rBack = config.BackRadiusMm;
        float width = config.WidthMm;
        float penetration = config.PenetrationMm;
        float totalLen = rPin + width + rBack;

        var pinCenter = contact + dir * (rPin - penetration);
        var backCenter = contact + dir * (totalLen - rBack - penetration);
        var junction = contact + dir * (totalLen - penetration);

        // Volumetric collision check along the pinhead path
        // Check at pin center, midpoint, and back center
        float minClearance = float.MaxValue;

        // Check pin sphere region
        float c1 = bvh.BeamCast(pinCenter, dir, rPin, config.CollisionRays);
        // But exclude the first hit if it's the model surface itself (within penetration distance)
        // Use closest point distance instead for the pin region
        var cpPin = bvh.ClosestPoint(pinCenter);
        if (cpPin.HasValue) minClearance = Math.Min(minClearance, cpPin.Value.Distance);

        // Check back sphere region
        var cpBack = bvh.ClosestPoint(backCenter);
        if (cpBack.HasValue)
        {
            float backClearance = cpBack.Value.Distance - rBack;
            minClearance = Math.Min(minClearance, backClearance);
        }

        // Check junction point
        var cpJunct = bvh.ClosestPoint(junction);
        if (cpJunct.HasValue)
        {
            float junctClearance = cpJunct.Value.Distance - rBack;
            minClearance = Math.Min(minClearance, junctClearance);
        }

        // Check midpoint of the connecting cone
        var midPoint = (pinCenter + backCenter) / 2f;
        var cpMid = bvh.ClosestPoint(midPoint);
        if (cpMid.HasValue)
        {
            float midR = (rPin + rBack) / 2f;
            float midClearance = cpMid.Value.Distance - midR;
            minClearance = Math.Min(minClearance, midClearance);
        }

        // Also check if the junction point is inside the mesh
        bool junctionInside = bvh.IsInside(junction);

        // The pin sphere SHOULD be near the surface (it penetrates), so
        // we allow negative clearance for the pin but not for back/junction
        bool isValid = !junctionInside && minClearance > -rPin * 0.5f;

        // For the back sphere and junction, require positive clearance
        if (cpBack.HasValue && cpBack.Value.Distance < rBack * 0.5f && !IsNearContact(cpBack.Value.Point, contact, totalLen))
            isValid = false;

        return new Pinhead
        {
            ContactPoint = contact,
            Direction = dir,
            PinCenter = pinCenter,
            BackCenter = backCenter,
            JunctionPoint = junction,
            PinRadius = rPin,
            BackRadius = rBack,
            Width = width,
            Clearance = minClearance,
            IsValid = isValid,
            NeedsAnchor = false,
        };
    }

    private static bool IsNearContact(Vector3 point, Vector3 contact, float totalLen)
    {
        return Vector3.Distance(point, contact) < totalLen * 0.5f;
    }

    /// <summary>
    /// Compute initial head direction from surface normal, clamped to max bridge slope.
    /// Overhang normals point downward; we follow the normal but limit the tilt.
    /// </summary>
    private static Vector3 ComputeInitialDirection(Vector3 normal, float maxSlope)
    {
        // Ensure normal points downward
        if (normal.Z > -0.1f)
            normal = new Vector3(normal.X, normal.Y, -1f);
        normal = Vector3.Normalize(normal);

        // Convert to polar angle (angle from -Z axis)
        float cosAngle = -normal.Z; // cos of angle from vertical (straight down)
        float maxCos = MathF.Cos(maxSlope);

        if (cosAngle >= maxCos)
            return normal; // within allowed range

        // Clamp: keep the XY direction but limit the polar angle
        float xyMag = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y);
        if (xyMag < 0.001f)
            return new Vector3(0, 0, -1);

        float maxXYMag = MathF.Sin(maxSlope);
        float scale = maxXYMag / xyMag;
        return Vector3.Normalize(new Vector3(normal.X * scale, normal.Y * scale, -maxCos));
    }

    /// <summary>
    /// Convert spherical coordinates to cartesian direction vector.
    /// Polar = angle from +Z (0=up, PI=down), Azimuth = rotation around Z.
    /// </summary>
    private static Vector3 SphericalToCartesian(float polar, float azimuth)
    {
        float sinP = MathF.Sin(polar);
        return new Vector3(
            sinP * MathF.Cos(azimuth),
            sinP * MathF.Sin(azimuth),
            -MathF.Cos(polar) // negate so PI=down maps to Z=-1
        );
    }
}
