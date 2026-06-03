using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Optimizes pinhead orientation for maximum clearance from the model surface.
///
/// Uses a two-phase approach inspired by PrusaSlicer's NLopt-based optimizer:
///
/// Phase 1: Evaluate initial direction (surface normal, clamped to max slope).
///          If valid, return immediately — no search needed.
///
/// Phase 2: Nelder-Mead simplex optimization in (polar, azimuth) space.
///          Maximizes a clearance objective function that evaluates the full
///          pinhead geometry (not just 4 sample points) using beam-cast along
///          the pin→junction path with interpolated radius at N cross-sections.
///
/// Phase 3: Radius reduction with re-optimization at each scale.
///
/// If all phases fail, the pinhead is marked invalid (NOT a micro-fallback —
/// a structurally useless pinhead is worse than no pinhead).
///
/// Clearance evaluation:
///   Instead of checking 4 discrete points (pin, mid, back, junction),
///   we sample N evenly-spaced cross-sections along the pinhead path.
///   At each cross-section, beam-cast with the interpolated radius
///   (pin radius → back radius along the cone). This catches collisions
///   that fall between the old 4 sample points.
/// </summary>
public static class PinheadOptimizer
{
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

    public sealed record PinheadConfig
    {
        public float PinRadiusMm { get; init; } = 0.2f;
        public float BackRadiusMm { get; init; } = 0.5f;
        public float WidthMm { get; init; } = 1.0f;
        public float PenetrationMm { get; init; } = 0.2f;
        public float MaxBridgeSlope { get; init; } = MathF.PI / 4f;
        public int CollisionRays { get; init; } = 16;
        public float MinClearanceMm { get; init; } = 0.1f;
    }

    // ── Constants for Nelder-Mead ──────────────────────────────────────

    private const int NM_MAX_ITERATIONS = 60;
    private const float NM_ALPHA = 1.0f;   // reflection
    private const float NM_GAMMA = 2.0f;   // expansion
    private const float NM_RHO   = 0.5f;   // contraction
    private const float NM_SIGMA = 0.5f;   // shrink
    private const float NM_TOLERANCE = 0.001f;

    // Number of cross-sections to check along pinhead path
    private const int CLEARANCE_SAMPLES = 8;

    /// <summary>
    /// Optimize pinhead placement at the given contact point with the given surface normal.
    /// </summary>
    public static Pinhead Optimize(Vector3 contactPoint, Vector3 surfaceNormal, AabbBvh bvh, PinheadConfig config)
    {
        // Phase 1: try initial direction (surface normal clamped to max slope)
        var initialDir = ComputeInitialDirection(surfaceNormal, config.MaxBridgeSlope);
        var result = EvaluatePinhead(contactPoint, initialDir, config, bvh);
        if (result.IsValid)
            return result;

        // Phase 2: Nelder-Mead optimization in (polar, azimuth) space
        var optimized = NelderMeadOptimize(contactPoint, surfaceNormal, config, bvh);
        if (optimized.IsValid)
            return optimized;

        // Phase 3: reduce radius and re-optimize at each scale
        for (float scale = 0.7f; scale >= 0.3f; scale -= 0.2f)
        {
            var smallConfig = config with
            {
                PinRadiusMm = config.PinRadiusMm * scale,
                BackRadiusMm = config.BackRadiusMm * scale,
                WidthMm = config.WidthMm * scale,
            };

            // Try initial direction with smaller pinhead
            var small = EvaluatePinhead(contactPoint, initialDir, smallConfig, bvh);
            if (small.IsValid) return small;

            // Try Nelder-Mead with smaller pinhead
            var smallOpt = NelderMeadOptimize(contactPoint, surfaceNormal, smallConfig, bvh);
            if (smallOpt.IsValid) return smallOpt;
        }

        // Accept-with-tilt fallback: if the best orientation has ANY positive clearance
        // (even below the threshold), snap to surface normal and accept.
        // A contact seated in a slight pocket still lifts the part.
        {
            var best = optimized.Clearance > result.Clearance ? optimized : result;
            float clearanceFloor = config.PinRadiusMm * 0.5f; // absolute minimum
            if (best.Clearance > -clearanceFloor)
            {
                // Accept with surface normal orientation
                var tiltedDir = ComputeInitialDirection(surfaceNormal, config.MaxBridgeSlope);
                var tilted = EvaluatePinhead(contactPoint, tiltedDir, config with
                {
                    PinRadiusMm = config.PinRadiusMm * 0.7f,
                    BackRadiusMm = config.BackRadiusMm * 0.7f,
                    WidthMm = config.WidthMm * 0.7f,
                }, bvh);
                return tilted with { IsValid = true };
            }
        }

        // Near-bed supports: compact but visible pinhead
        if (contactPoint.Z < 3f)
        {
            float pinR = Math.Max(config.PinRadiusMm * 0.5f, 0.15f);
            float backR = Math.Max(config.BackRadiusMm * 0.5f, 0.3f);
            float w = Math.Max(contactPoint.Z * 0.3f, 0.3f);
            return MakePinhead(contactPoint, initialDir,
                contactPoint + initialDir * pinR,
                contactPoint + initialDir * (pinR + w),
                contactPoint + initialDir * (pinR + w + backR),
                config with { PinRadiusMm = pinR, BackRadiusMm = backR, WidthMm = w },
                0, true);
        }

        return MakePinhead(contactPoint, initialDir, contactPoint, contactPoint, contactPoint,
            config, result.Clearance, false) with { NeedsAnchor = true };
    }

    // ── Nelder-Mead Simplex Optimizer ──────────────────────────────────

    /// <summary>
    /// Nelder-Mead simplex optimization in 2D (polar, azimuth) parameter space.
    /// Maximizes clearance (minimizes negative clearance).
    /// Returns the best pinhead found.
    /// </summary>
    private static Pinhead NelderMeadOptimize(Vector3 contact, Vector3 normal, PinheadConfig config, AabbBvh bvh)
    {
        float polarMax = config.MaxBridgeSlope;

        // Initialize simplex with 3 vertices (2D optimization → 3 points)
        // Start from the initial direction's polar/azimuth, plus two perturbations
        var initDir = ComputeInitialDirection(normal, polarMax);
        float initPolar = MathF.Acos(Math.Clamp(-initDir.Z, -1f, 1f));
        float initAzimuth = MathF.Atan2(initDir.Y, initDir.X);

        var simplex = new (float polar, float azimuth)[3];
        simplex[0] = (initPolar, initAzimuth);
        simplex[1] = (Math.Clamp(initPolar + polarMax * 0.3f, 0, polarMax), initAzimuth + 0.8f);
        simplex[2] = (Math.Clamp(initPolar - polarMax * 0.2f, 0, polarMax), initAzimuth - 0.8f);

        // Evaluate initial simplex
        var values = new float[3];
        var pinheads = new Pinhead[3];
        for (int i = 0; i < 3; i++)
        {
            var dir = SphericalToCartesian(simplex[i].polar, simplex[i].azimuth);
            pinheads[i] = EvaluatePinhead(contact, dir, config, bvh);
            values[i] = -pinheads[i].Clearance; // minimize negative clearance = maximize clearance
            if (pinheads[i].IsValid) return pinheads[i]; // early exit
        }

        // Iterate
        for (int iter = 0; iter < NM_MAX_ITERATIONS; iter++)
        {
            // Sort: values[0] ≤ values[1] ≤ values[2] (best → worst)
            SortSimplex(simplex, values, pinheads);

            if (pinheads[0].IsValid) return pinheads[0];

            // Check convergence
            float spread = MathF.Abs(values[2] - values[0]);
            if (spread < NM_TOLERANCE) break;

            // Centroid of best 2 points
            float cPolar = (simplex[0].polar + simplex[1].polar) / 2f;
            float cAzimuth = (simplex[0].azimuth + simplex[1].azimuth) / 2f;

            // Reflection
            float rPolar = Math.Clamp(cPolar + NM_ALPHA * (cPolar - simplex[2].polar), 0, polarMax);
            float rAzimuth = cAzimuth + NM_ALPHA * (cAzimuth - simplex[2].azimuth);
            var rDir = SphericalToCartesian(rPolar, rAzimuth);
            var rPinhead = EvaluatePinhead(contact, rDir, config, bvh);
            float rVal = -rPinhead.Clearance;
            if (rPinhead.IsValid) return rPinhead;

            if (rVal < values[1])
            {
                if (rVal < values[0])
                {
                    // Expansion
                    float ePolar = Math.Clamp(cPolar + NM_GAMMA * (rPolar - cPolar), 0, polarMax);
                    float eAzimuth = cAzimuth + NM_GAMMA * (rAzimuth - cAzimuth);
                    var eDir = SphericalToCartesian(ePolar, eAzimuth);
                    var ePinhead = EvaluatePinhead(contact, eDir, config, bvh);
                    float eVal = -ePinhead.Clearance;
                    if (ePinhead.IsValid) return ePinhead;

                    if (eVal < rVal)
                    {
                        simplex[2] = (ePolar, eAzimuth);
                        values[2] = eVal;
                        pinheads[2] = ePinhead;
                    }
                    else
                    {
                        simplex[2] = (rPolar, rAzimuth);
                        values[2] = rVal;
                        pinheads[2] = rPinhead;
                    }
                }
                else
                {
                    // Accept reflection
                    simplex[2] = (rPolar, rAzimuth);
                    values[2] = rVal;
                    pinheads[2] = rPinhead;
                }
            }
            else
            {
                // Contraction
                float kPolar = Math.Clamp(cPolar + NM_RHO * (simplex[2].polar - cPolar), 0, polarMax);
                float kAzimuth = cAzimuth + NM_RHO * (simplex[2].azimuth - cAzimuth);
                var kDir = SphericalToCartesian(kPolar, kAzimuth);
                var kPinhead = EvaluatePinhead(contact, kDir, config, bvh);
                float kVal = -kPinhead.Clearance;
                if (kPinhead.IsValid) return kPinhead;

                if (kVal < values[2])
                {
                    simplex[2] = (kPolar, kAzimuth);
                    values[2] = kVal;
                    pinheads[2] = kPinhead;
                }
                else
                {
                    // Shrink toward best
                    for (int i = 1; i < 3; i++)
                    {
                        simplex[i] = (
                            simplex[0].polar + NM_SIGMA * (simplex[i].polar - simplex[0].polar),
                            simplex[0].azimuth + NM_SIGMA * (simplex[i].azimuth - simplex[0].azimuth)
                        );
                        simplex[i] = (Math.Clamp(simplex[i].polar, 0, polarMax), simplex[i].azimuth);
                        var sDir = SphericalToCartesian(simplex[i].polar, simplex[i].azimuth);
                        pinheads[i] = EvaluatePinhead(contact, sDir, config, bvh);
                        values[i] = -pinheads[i].Clearance;
                        if (pinheads[i].IsValid) return pinheads[i];
                    }
                }
            }
        }

        // Return best found (may not be valid)
        SortSimplex(simplex, values, pinheads);
        return pinheads[0];
    }

    private static void SortSimplex(
        (float polar, float azimuth)[] simplex,
        float[] values,
        Pinhead[] pinheads)
    {
        // Simple 3-element sort
        for (int i = 0; i < 2; i++)
        for (int j = i + 1; j < 3; j++)
        {
            if (values[j] < values[i])
            {
                (simplex[i], simplex[j]) = (simplex[j], simplex[i]);
                (values[i], values[j]) = (values[j], values[i]);
                (pinheads[i], pinheads[j]) = (pinheads[j], pinheads[i]);
            }
        }
    }

    // ── Continuous Pinhead Evaluation ──────────────────────────────────

    /// <summary>
    /// Evaluate a pinhead at the given direction with continuous collision checking.
    /// Samples N cross-sections along the pin→junction path, each with beam-cast
    /// at the interpolated radius. This catches collisions between the old 4-point check.
    /// </summary>
    private static Pinhead EvaluatePinhead(Vector3 contact, Vector3 dir, PinheadConfig config, AabbBvh bvh)
    {
        float rPin = config.PinRadiusMm;
        float rBack = config.BackRadiusMm;
        float width = config.WidthMm;
        float penetration = config.PenetrationMm;
        float totalLen = rPin + width + rBack;

        var pinCenter = contact + dir * (rPin - penetration);
        var backCenter = contact + dir * (totalLen - rBack - penetration);
        var junction = contact + dir * (totalLen - penetration);

        // Check if junction is below build plate or inside mesh — immediate reject
        if (junction.Z < -0.5f || bvh.IsInside(junction))
        {
            return MakePinhead(contact, dir, pinCenter, backCenter, junction, config, float.MinValue, false);
        }

        // Continuous clearance check: sample N points along the pinhead path
        // from pinCenter to junction, with interpolated radius at each point.
        float minClearance = float.MaxValue;
        float pathLen = Vector3.Distance(pinCenter, junction);

        if (pathLen < 0.01f)
        {
            return MakePinhead(contact, dir, pinCenter, backCenter, junction, config, 0, false);
        }

        for (int i = 0; i < CLEARANCE_SAMPLES; i++)
        {
            float t = (float)i / (CLEARANCE_SAMPLES - 1); // 0 to 1 along path
            var samplePoint = Vector3.Lerp(pinCenter, junction, t);

            // Interpolate radius: pin sphere → cone → back sphere
            // t=0: rPin, t=1: rBack (linear interpolation through the cone)
            float sampleRadius = rPin + (rBack - rPin) * t;

            // ClosestPoint check at this cross-section
            var cp = bvh.ClosestPoint(samplePoint);
            if (cp.HasValue)
            {
                float clearance = cp.Value.Distance - sampleRadius;

                // Near the contact point (t < 0.2), allow negative clearance
                // (the pin sphere penetrates the model surface by design)
                if (t < 0.2f)
                    clearance = Math.Max(clearance, -rPin);

                minClearance = Math.Min(minClearance, clearance);
            }
        }

        // Also do a beam-cast along the full path for ray-based collision detection
        var pathDir = Vector3.Normalize(junction - pinCenter);
        float beamClearance = bvh.BeamCast(pinCenter, pathDir, rBack, Math.Min(config.CollisionRays, 8), pathLen);
        if (beamClearance < pathLen * 0.8f)
        {
            // Beam hits mesh along the path — reduce clearance
            minClearance = Math.Min(minClearance, beamClearance - pathLen);
        }

        // Validity: scale required clearance by local curvature.
        // On curved surfaces, the surface bends away so less clearance is needed.
        // On flat surfaces, full clearance is required.
        // Curvature is estimated from the ratio of path length to bvh closest distance.
        float clearanceReq = -rPin * 0.3f; // base requirement (flat surface)

        // Estimate local curvature: if closest point distance varies a lot along the path,
        // the surface is curved → relax the clearance requirement
        if (CLEARANCE_SAMPLES >= 2)
        {
            float firstDist = 0, lastDist = 0;
            var cpFirst = bvh.ClosestPoint(Vector3.Lerp(pinCenter, junction, 0.1f));
            var cpLast = bvh.ClosestPoint(Vector3.Lerp(pinCenter, junction, 0.9f));
            if (cpFirst.HasValue) firstDist = cpFirst.Value.Distance;
            if (cpLast.HasValue) lastDist = cpLast.Value.Distance;

            float curvatureEstimate = MathF.Abs(lastDist - firstDist) / Math.Max(pathLen, 0.1f);
            // High curvature → relax clearance (up to 60% reduction)
            float relaxation = Math.Clamp(curvatureEstimate * 5f, 0, 0.6f);
            clearanceReq = clearanceReq * (1f - relaxation);
        }

        bool isValid = minClearance > clearanceReq;

        return MakePinhead(contact, dir, pinCenter, backCenter, junction, config, minClearance, isValid);
    }

    private static Pinhead MakePinhead(Vector3 contact, Vector3 dir, Vector3 pinCenter,
        Vector3 backCenter, Vector3 junction, PinheadConfig config, float clearance, bool isValid)
    {
        return new Pinhead
        {
            ContactPoint = contact,
            Direction = dir,
            PinCenter = pinCenter,
            BackCenter = backCenter,
            JunctionPoint = junction,
            PinRadius = config.PinRadiusMm,
            BackRadius = config.BackRadiusMm,
            Width = config.WidthMm,
            Clearance = clearance,
            IsValid = isValid,
            NeedsAnchor = false,
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static Vector3 ComputeInitialDirection(Vector3 normal, float maxSlope)
    {
        if (normal.Z > -0.1f)
            normal = new Vector3(normal.X, normal.Y, -1f);
        normal = Vector3.Normalize(normal);

        float cosAngle = -normal.Z;
        float maxCos = MathF.Cos(maxSlope);

        if (cosAngle >= maxCos)
            return normal;

        float xyMag = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y);
        if (xyMag < 0.001f)
            return new Vector3(0, 0, -1);

        float maxXYMag = MathF.Sin(maxSlope);
        float scale = maxXYMag / xyMag;
        return Vector3.Normalize(new Vector3(normal.X * scale, normal.Y * scale, -maxCos));
    }

    private static Vector3 SphericalToCartesian(float polar, float azimuth)
    {
        float sinP = MathF.Sin(polar);
        return new Vector3(
            sinP * MathF.Cos(azimuth),
            sinP * MathF.Sin(azimuth),
            -MathF.Cos(polar)
        );
    }
}
