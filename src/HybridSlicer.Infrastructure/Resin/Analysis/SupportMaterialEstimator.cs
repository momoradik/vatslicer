namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates support structure material usage as a percentage of model volume.
/// Helps users understand the cost overhead of supports and compare strategies.
/// </summary>
public static class SupportMaterialEstimator
{
    public sealed record MaterialBreakdown
    {
        public required float ModelVolumeMl { get; init; }
        public required float SupportVolumeMl { get; init; }
        public required float TotalVolumeMl { get; init; }
        public required float SupportPct { get; init; }
        public required float SupportCostUsd { get; init; }
        public required string Efficiency { get; init; } // "excellent", "good", "moderate", "poor"
    }

    public static MaterialBreakdown Estimate(
        float modelVolumeMm3,
        float supportVolumeMm3,
        float resinCostPerMl = 0.05f)
    {
        float modelMl = modelVolumeMm3 / 1000f;
        float supportMl = supportVolumeMm3 / 1000f;
        float totalMl = modelMl + supportMl;
        float supportPct = totalMl > 0 ? supportMl / totalMl * 100f : 0;

        string efficiency = supportPct < 10 ? "excellent" :
            supportPct < 25 ? "good" :
            supportPct < 50 ? "moderate" : "poor";

        return new MaterialBreakdown
        {
            ModelVolumeMl = modelMl,
            SupportVolumeMl = supportMl,
            TotalVolumeMl = totalMl,
            SupportPct = supportPct,
            SupportCostUsd = supportMl * resinCostPerMl,
            Efficiency = efficiency,
        };
    }
}
