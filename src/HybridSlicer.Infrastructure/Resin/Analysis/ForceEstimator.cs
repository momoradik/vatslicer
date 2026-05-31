using System.Numerics;
using HybridSlicer.Domain.Enums;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates forces on each support point for structural sizing.
///
/// Forces considered:
/// - Gravity: weight of resin/part above the support
/// - Peel force (bottom-up): suction from FEP separation, proportional to cross-section area
/// - Recoat force (top-down): lateral shear from blade/roller sweep
/// - Moment arm: bending from lateral distance to nearest supported region
///
/// Output: required support diameter and weight class (light/medium/heavy).
/// </summary>
public static class ForceEstimator
{
    // Material properties (approximate for standard resin)
    private const float RESIN_DENSITY_KG_PER_MM3 = 1.1e-6f;    // ~1.1 g/cm³
    private const float GRAVITY_MM_PER_S2 = 9810f;               // mm/s²
    private const float RESIN_TENSILE_STRENGTH_MPA = 40f;        // typical LCD resin
    private const float RESIN_ELASTIC_MODULUS_MPA = 2000f;        // ~2 GPa
    private const float PEEL_FORCE_COEFFICIENT = 0.015f;          // N per mm² of cross-section area
    private const float RECOAT_FORCE_COEFFICIENT = 0.002f;        // N per mm of cross-section width

    /// <summary>
    /// Force analysis result for a single support point.
    /// </summary>
    public sealed class ForceResult
    {
        /// <summary>Gravity force in Newtons.</summary>
        public required float GravityForceN { get; init; }
        /// <summary>Peel/recoat force in Newtons.</summary>
        public required float PeelForceN { get; init; }
        /// <summary>Total axial force the support must resist.</summary>
        public required float TotalAxialForceN { get; init; }
        /// <summary>Bending moment from lateral offset (N·mm).</summary>
        public required float BendingMomentNmm { get; init; }
        /// <summary>Minimum required support diameter to resist buckling (mm).</summary>
        public required float MinDiameterMm { get; init; }
        /// <summary>Recommended weight class.</summary>
        public required SupportWeight Weight { get; init; }
        /// <summary>Safety factor (actual capacity / required).</summary>
        public required float SafetyFactor { get; init; }
    }

    public enum SupportWeight { Light, Medium, Heavy }

    /// <summary>
    /// Estimate forces on a support point and determine required sizing.
    /// </summary>
    /// <param name="supportZ">Z height of the contact point (mm).</param>
    /// <param name="overhangArea">Area of the overhang region this support serves (mm²).</param>
    /// <param name="numSupportsInRegion">How many supports share this overhang region.</param>
    /// <param name="layerAreaAbove">Total cross-section area of model above this point (mm²).</param>
    /// <param name="unsupportedLength">Free span length from nearest support/anchor (mm).</param>
    /// <param name="orientation">Printer orientation.</param>
    /// <param name="recoaterSpeedMmS">Recoater speed (mm/s), 0 if no recoater.</param>
    public static ForceResult Estimate(
        float supportZ,
        float overhangArea,
        int numSupportsInRegion,
        float layerAreaAbove,
        float unsupportedLength,
        PrinterOrientation orientation,
        float recoaterSpeedMmS = 0)
    {
        // Per-support share of the overhang
        float areaPerSupport = overhangArea / Math.Max(1, numSupportsInRegion);

        // ── Gravity force ──
        // Approximate: volume above = area * height * fill_factor
        float heightAbove = supportZ; // conservative: full height
        float volumeAboveMm3 = layerAreaAbove * heightAbove * 0.3f; // ~30% fill for partial model
        float gravityForceN = volumeAboveMm3 * RESIN_DENSITY_KG_PER_MM3 * GRAVITY_MM_PER_S2;
        // Distribute across supports in the region
        float gravityPerSupport = gravityForceN / Math.Max(1, numSupportsInRegion);

        // ── Peel / Recoat force ──
        float peelForce = 0;
        if (orientation == PrinterOrientation.BottomUp)
        {
            // Peel force proportional to layer cross-section area
            peelForce = areaPerSupport * PEEL_FORCE_COEFFICIENT;
        }
        else if (recoaterSpeedMmS > 0)
        {
            // Recoat lateral force proportional to width and speed
            float width = MathF.Sqrt(areaPerSupport); // approximate width
            peelForce = width * recoaterSpeedMmS * RECOAT_FORCE_COEFFICIENT;
        }

        // ── Total axial force ──
        float totalAxial = gravityPerSupport + peelForce;

        // ── Bending moment ──
        float bendingMoment = totalAxial * unsupportedLength;

        // ── Euler buckling check ──
        // Critical load for a column: P_cr = π² × E × I / L²
        // where I = π × r⁴ / 4 for a circular cross-section
        // Solving for minimum radius: r = (P × L² × 4 / (π³ × E))^(1/4)
        float columnLength = supportZ; // full support height
        float minRadius = 0;
        if (columnLength > 0.1f && totalAxial > 0)
        {
            float rPow4 = totalAxial * columnLength * columnLength * 4f /
                          (MathF.PI * MathF.PI * MathF.PI * RESIN_ELASTIC_MODULUS_MPA);
            minRadius = MathF.Pow(Math.Max(0, rPow4), 0.25f);
        }
        float minDiameter = minRadius * 2f;

        // Ensure minimum practical diameter
        minDiameter = Math.Max(minDiameter, 0.3f);

        // ── Weight classification ──
        SupportWeight weight;
        if (minDiameter <= 0.5f && totalAxial < 0.5f)
            weight = SupportWeight.Light;
        else if (minDiameter <= 1.0f && totalAxial < 2.0f)
            weight = SupportWeight.Medium;
        else
            weight = SupportWeight.Heavy;

        // ── Safety factor ──
        // Actual capacity of a medium support (0.8mm diameter)
        float actualRadius = weight == SupportWeight.Light ? 0.2f :
                             weight == SupportWeight.Medium ? 0.4f : 0.75f;
        float actualI = MathF.PI * MathF.Pow(actualRadius, 4) / 4f;
        float criticalLoad = MathF.PI * MathF.PI * RESIN_ELASTIC_MODULUS_MPA * actualI /
                            Math.Max(1f, columnLength * columnLength);
        float safetyFactor = totalAxial > 0 ? criticalLoad / totalAxial : 10f;
        safetyFactor = Math.Min(safetyFactor, 10f); // cap at 10

        return new ForceResult
        {
            GravityForceN = gravityPerSupport,
            PeelForceN = peelForce,
            TotalAxialForceN = totalAxial,
            BendingMomentNmm = bendingMoment,
            MinDiameterMm = minDiameter,
            Weight = weight,
            SafetyFactor = safetyFactor,
        };
    }

    /// <summary>
    /// Check if a support with the given diameter can withstand the given force
    /// at the given column height without buckling.
    /// </summary>
    public static bool CheckBuckling(float diameterMm, float heightMm, float axialForceN)
    {
        if (heightMm < 0.1f) return true;
        float r = diameterMm / 2f;
        float I = MathF.PI * MathF.Pow(r, 4) / 4f;
        float criticalLoad = MathF.PI * MathF.PI * RESIN_ELASTIC_MODULUS_MPA * I / (heightMm * heightMm);
        return criticalLoad > axialForceN * 2f; // 2x safety factor
    }
}
