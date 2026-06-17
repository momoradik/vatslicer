namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes optimal per-layer exposure times based on cross-section area,
/// temperature, and layer position. Bottom layers get more exposure,
/// large cross-sections get slightly more, and temperature compensation
/// is applied throughout.
/// </summary>
public static class SmartExposureOptimizer
{
    public sealed record LayerExposure
    {
        public required int LayerIndex { get; init; }
        public required float ExposureMs { get; init; }
        public required string Reason { get; init; }
    }

    public sealed record OptimizationResult
    {
        public required List<LayerExposure> Layers { get; init; }
        public required float AvgExposureMs { get; init; }
        public required float MinExposureMs { get; init; }
        public required float MaxExposureMs { get; init; }
        public required float TimeSavedPct { get; init; }
    }

    public static OptimizationResult Optimize(
        CrossSectionAreaCalculator.AreaProfile areaProfile,
        float baseNormalExposureMs,
        float baseBottomExposureMs,
        int bottomLayerCount,
        float ambientTempC = 25f)
    {
        var layers = new List<LayerExposure>();
        float avgArea = areaProfile.AreasPerLayer.Average();
        float maxArea = areaProfile.MaxAreaMm2;

        // Temperature compensation
        var tempAdj = TemperatureCompensator.Compensate(baseNormalExposureMs, ambientTempC);
        float tempFactor = tempAdj.AdjustedExposureMs / baseNormalExposureMs;

        float totalOriginal = 0, totalOptimized = 0;

        for (int i = 0; i < areaProfile.LayerCount; i++)
        {
            bool isBottom = i < bottomLayerCount;
            float baseMs = isBottom ? baseBottomExposureMs : baseNormalExposureMs;
            totalOriginal += baseMs;

            // Area-based adjustment: larger area → slightly more exposure for adhesion
            float areaFactor = 1f;
            if (!isBottom && maxArea > 0)
            {
                float areaRatio = areaProfile.AreasPerLayer[i] / maxArea;
                areaFactor = 0.95f + 0.1f * areaRatio; // 0.95x to 1.05x
            }

            float optimizedMs = baseMs * tempFactor * areaFactor;
            totalOptimized += optimizedMs;

            string reason = isBottom ? "bottom layer" :
                areaFactor > 1.02f ? "large cross-section" :
                areaFactor < 0.97f ? "small cross-section" : "normal";

            layers.Add(new LayerExposure
            {
                LayerIndex = i,
                ExposureMs = optimizedMs,
                Reason = reason,
            });
        }

        float savedPct = totalOriginal > 0 ? (totalOriginal - totalOptimized) / totalOriginal * 100f : 0;

        return new OptimizationResult
        {
            Layers = layers,
            AvgExposureMs = layers.Count > 0 ? layers.Average(l => l.ExposureMs) : 0,
            MinExposureMs = layers.Count > 0 ? layers.Min(l => l.ExposureMs) : 0,
            MaxExposureMs = layers.Count > 0 ? layers.Max(l => l.ExposureMs) : 0,
            TimeSavedPct = savedPct,
        };
    }
}
