namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Checks if the model contains features smaller than the printer's minimum
/// feature size (determined by pixel pitch and exposure). Features below
/// this threshold will not resolve correctly.
/// </summary>
public static class MinimumFeatureSizeChecker
{
    public sealed record FeatureSizeResult
    {
        public required float MinFeatureSizeMm { get; init; }
        public required float PrinterMinFeatureMm { get; init; }
        public required bool AllFeaturesResolvable { get; init; }
        public required int SubResolutionFeatures { get; init; }
        public required string Recommendation { get; init; }
    }

    public static FeatureSizeResult Check(
        float modelMinWallMm,
        float modelMinHoleMm,
        float pixelPitchMm,
        float xyBleedMm = 0.03f)
    {
        // Minimum resolvable feature: ~2 pixels + bleed compensation
        float minFeature = pixelPitchMm * 2 + xyBleedMm * 2;
        float smallestModel = Math.Min(modelMinWallMm, modelMinHoleMm);
        bool allResolvable = smallestModel >= minFeature;
        int subRes = 0;
        if (modelMinWallMm < minFeature) subRes++;
        if (modelMinHoleMm < minFeature) subRes++;

        string rec;
        if (allResolvable)
            rec = $"All features above minimum ({minFeature:F3}mm). Good resolution.";
        else if (smallestModel > minFeature * 0.5f)
            rec = $"Features near resolution limit ({minFeature:F3}mm). May print with reduced accuracy.";
        else
            rec = $"Features below resolution limit ({minFeature:F3}mm). Scale up or use a higher-resolution printer.";

        return new FeatureSizeResult
        {
            MinFeatureSizeMm = smallestModel,
            PrinterMinFeatureMm = minFeature,
            AllFeaturesResolvable = allResolvable,
            SubResolutionFeatures = subRes,
            Recommendation = rec,
        };
    }
}
