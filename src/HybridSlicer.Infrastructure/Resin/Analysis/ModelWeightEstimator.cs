namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates the weight of a printed model including supports.
/// Uses mesh volume (from the validator) and resin density.
/// </summary>
public static class ModelWeightEstimator
{
    public sealed record WeightEstimate
    {
        public required float ModelVolumeMm3 { get; init; }
        public required float SupportVolumeMm3 { get; init; }
        public required float TotalVolumeMm3 { get; init; }
        public required float TotalVolumeMl { get; init; }
        public required float TotalWeightG { get; init; }
        public required float EstimatedCostUsd { get; init; }
    }

    /// <summary>
    /// Estimate weight and cost.
    /// </summary>
    /// <param name="modelVolumeMm3">Model volume (from MeshValidator or pixel counting).</param>
    /// <param name="supportVolumeMm3">Support structure volume.</param>
    /// <param name="densityGPerCm3">Resin density. Standard: ~1.1 g/cm3.</param>
    /// <param name="costPerMl">Resin cost per ml. Typical: $0.03-0.08/ml.</param>
    public static WeightEstimate Estimate(
        float modelVolumeMm3,
        float supportVolumeMm3 = 0,
        float densityGPerCm3 = 1.1f,
        float costPerMl = 0.05f)
    {
        float total = modelVolumeMm3 + supportVolumeMm3;
        float ml = total / 1000f;
        float weightG = ml * densityGPerCm3;
        float cost = ml * costPerMl;

        return new WeightEstimate
        {
            ModelVolumeMm3 = modelVolumeMm3,
            SupportVolumeMm3 = supportVolumeMm3,
            TotalVolumeMm3 = total,
            TotalVolumeMl = ml,
            TotalWeightG = weightG,
            EstimatedCostUsd = cost,
        };
    }
}
