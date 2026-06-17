namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates the visual quality improvement from anti-aliasing at different levels.
/// Reports edge smoothness gain, grayscale pixel count, and recommended AA level
/// based on printer pixel pitch and layer height.
/// </summary>
public static class AntiAliasingQualityEstimator
{
    public sealed record AAEstimate
    {
        public required int RecommendedLevel { get; init; }
        public required float EdgeSmoothnessGain { get; init; }
        public required float PixelPitchMm { get; init; }
        public required float StairstepHeightMm { get; init; }
        public required string Recommendation { get; init; }
    }

    public static AAEstimate Estimate(float pixelPitchMm, float layerHeightMm)
    {
        // Stairstepping is visible when pixel pitch > layer height
        float stairstep = MathF.Sqrt(pixelPitchMm * pixelPitchMm + layerHeightMm * layerHeightMm);

        // AA benefit: sub-pixel edge smoothing
        // Fine pixels (< 0.04mm) benefit less from AA
        // Coarse pixels (> 0.06mm) benefit more
        float aaBenefit = Math.Clamp((pixelPitchMm - 0.03f) / 0.04f, 0, 1);

        int recommended;
        string rec;
        if (pixelPitchMm < 0.035f)
        {
            recommended = 1; // No AA needed for very fine pixels
            rec = "AA not needed — pixel pitch is fine enough for smooth edges";
        }
        else if (pixelPitchMm < 0.05f)
        {
            recommended = 2; // Light AA
            rec = "Light AA (2x) recommended — moderate pixel pitch benefits from edge smoothing";
        }
        else if (pixelPitchMm < 0.07f)
        {
            recommended = 4; // Medium AA
            rec = "Medium AA (4x) recommended — coarser pixels need more grayscale levels";
        }
        else
        {
            recommended = 8; // Heavy AA
            rec = "Heavy AA (8x) recommended — large pixels cause visible stairstepping";
        }

        return new AAEstimate
        {
            RecommendedLevel = recommended,
            EdgeSmoothnessGain = aaBenefit * 100,
            PixelPitchMm = pixelPitchMm,
            StairstepHeightMm = stairstep,
            Recommendation = rec,
        };
    }
}
