namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Predicts dimensional accuracy of the printed part based on:
/// - Pixel pitch (XY resolution limit)
/// - Layer height (Z resolution limit)
/// - Resin shrinkage
/// - XY pixel bleed
/// Reports expected deviation per axis.
/// </summary>
public static class DimensionalAccuracyPredictor
{
    public sealed record AccuracyPrediction
    {
        public required float XYDeviationMm { get; init; }
        public required float ZDeviationMm { get; init; }
        public required float OverallDeviationMm { get; init; }
        public required string Grade { get; init; } // "precision", "standard", "draft"
        public required string Description { get; init; }
    }

    public static AccuracyPrediction Predict(
        float pixelPitchMm,
        float layerHeightMm,
        float shrinkagePct = 2f,
        float xyBleedMm = 0.03f)
    {
        // XY deviation: pixel pitch + bleed + shrinkage
        float linearShrinkage = shrinkagePct / 300f;
        float xyDev = pixelPitchMm + xyBleedMm + linearShrinkage * 10f; // shrinkage on 10mm feature

        // Z deviation: layer height quantization + shrinkage
        float zDev = layerHeightMm / 2f + linearShrinkage * 10f;

        float overall = MathF.Sqrt(xyDev * xyDev + zDev * zDev);

        string grade = overall < 0.05f ? "precision" : overall < 0.1f ? "standard" : "draft";
        string desc = $"XY: +/-{xyDev:F3}mm, Z: +/-{zDev:F3}mm (overall +/-{overall:F3}mm, {grade})";

        return new AccuracyPrediction
        {
            XYDeviationMm = xyDev,
            ZDeviationMm = zDev,
            OverallDeviationMm = overall,
            Grade = grade,
            Description = desc,
        };
    }
}
