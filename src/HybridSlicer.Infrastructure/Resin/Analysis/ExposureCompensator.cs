namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes exposure compensation for resin shrinkage and xy pixel bleed.
/// Standard resins shrink ~1-3% on curing; this affects dimensional accuracy.
/// XY pixel bleed adds ~0.02-0.05mm per edge from light diffraction.
/// </summary>
public static class ExposureCompensator
{
    public sealed record Compensation
    {
        public required float XYCompensationMm { get; init; }
        public required float ZCompensationMm { get; init; }
        public required float ScaleFactor { get; init; }
        public required string Description { get; init; }
    }

    /// <summary>
    /// Compute dimensional compensation values.
    /// </summary>
    /// <param name="shrinkagePct">Resin volumetric shrinkage (%). Typical: 1-4%.</param>
    /// <param name="xyBleedMm">XY pixel bleed per edge (mm). Typical: 0.02-0.05mm.</param>
    /// <param name="exposureTimeMs">Exposure time (ms). Higher → more bleed.</param>
    /// <param name="pixelPitchMm">Pixel pitch (mm). Smaller → less bleed.</param>
    public static Compensation Compute(
        float shrinkagePct = 2f,
        float xyBleedMm = 0.03f,
        float exposureTimeMs = 2000f,
        float pixelPitchMm = 0.05f)
    {
        // Linear shrinkage from volumetric: ~shrinkage/3
        float linearShrinkage = shrinkagePct / 300f;
        float scaleFactor = 1f + linearShrinkage;

        // XY compensation: subtract bleed from contour offset
        // More exposure → more bleed (roughly linear in the 1-4s range)
        float bleedScale = exposureTimeMs / 2000f;
        float effectiveBleed = xyBleedMm * bleedScale;

        // Z compensation: negligible for most resins (shrinkage is mostly in-plane)
        float zComp = linearShrinkage * 0.5f; // small Z stretch

        return new Compensation
        {
            XYCompensationMm = -effectiveBleed, // negative = inset contours
            ZCompensationMm = zComp,
            ScaleFactor = scaleFactor,
            Description = $"Shrinkage {shrinkagePct}% → scale {scaleFactor:F4}x, XY offset {-effectiveBleed:F3}mm, Z offset {zComp:F4}mm",
        };
    }
}
