namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Calculates resin mixing ratios for blended properties.
/// Users sometimes mix standard + flexible resins for custom hardness,
/// or add pigment. This computes exposure and property estimates.
/// </summary>
public static class ResinMixingCalculator
{
    public sealed record MixResult
    {
        public required float TotalVolumeMl { get; init; }
        public required float EstimatedExposureMs { get; init; }
        public required float EstimatedDensity { get; init; }
        public required float EstimatedShrinkagePct { get; init; }
        public required string Warning { get; init; }
    }

    public static MixResult Calculate(
        float volumeAMl, float exposureAMs, float densityA, float shrinkageAPct,
        float volumeBMl, float exposureBMs, float densityB, float shrinkageBPct)
    {
        float total = volumeAMl + volumeBMl;
        if (total <= 0) return new MixResult { TotalVolumeMl = 0, EstimatedExposureMs = 0, EstimatedDensity = 0, EstimatedShrinkagePct = 0, Warning = "No volume" };

        float ratioA = volumeAMl / total;
        float ratioB = volumeBMl / total;

        // Weighted average for most properties
        float estExposure = exposureAMs * ratioA + exposureBMs * ratioB;
        float estDensity = densityA * ratioA + densityB * ratioB;
        float estShrinkage = shrinkageAPct * ratioA + shrinkageBPct * ratioB;

        string warning;
        float exposureDiff = Math.Abs(exposureAMs - exposureBMs);
        if (exposureDiff > exposureAMs * 0.5f)
            warning = "Large exposure difference between resins — test a small print first. Mixed properties may be unpredictable.";
        else if (Math.Abs(densityA - densityB) > 0.3f)
            warning = "Significant density difference — shake well before each use to prevent separation.";
        else
            warning = "Mix should be compatible. Run a test exposure before production prints.";

        return new MixResult
        {
            TotalVolumeMl = total,
            EstimatedExposureMs = estExposure,
            EstimatedDensity = estDensity,
            EstimatedShrinkagePct = estShrinkage,
            Warning = warning,
        };
    }
}
