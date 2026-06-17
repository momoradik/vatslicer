namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Optimizes overall print speed by adjusting exposure, lift, and rest
/// times based on model geometry. Reports total time savings.
/// </summary>
public static class PrintSpeedOptimizer
{
    public sealed record OptimizationPlan
    {
        public required float OriginalTimeMinutes { get; init; }
        public required float OptimizedTimeMinutes { get; init; }
        public required float TimeSavedMinutes { get; init; }
        public required float TimeSavedPct { get; init; }
        public required List<string> Optimizations { get; init; }
    }

    public static OptimizationPlan Optimize(
        int totalLayers, int bottomLayers,
        float normalExposureMs, float bottomExposureMs,
        float liftDistMm, float liftSpeedMmPerMin,
        float retractSpeedMmPerMin,
        float lightOffDelayMs,
        float maxCrossSectionAreaMm2,
        float ambientTempC = 25f)
    {
        var optimizations = new List<string>();
        float origTime = 0, optTime = 0;

        // Original timing
        float origLayerTime = normalExposureMs / 60000f +
            (liftDistMm / (liftSpeedMmPerMin / 60f) + liftDistMm / (retractSpeedMmPerMin / 60f)) / 60f +
            lightOffDelayMs / 60000f;
        float origBottomTime = bottomExposureMs / 60000f +
            (liftDistMm / (liftSpeedMmPerMin / 60f) + liftDistMm / (retractSpeedMmPerMin / 60f)) / 60f +
            lightOffDelayMs / 60000f;
        origTime = bottomLayers * origBottomTime + (totalLayers - bottomLayers) * origLayerTime;

        float optNormalExp = normalExposureMs;
        float optLiftDist = liftDistMm;
        float optLiftSpeed = liftSpeedMmPerMin;
        float optLightOff = lightOffDelayMs;

        // Optimization 1: reduce light-off delay if > 1s
        if (lightOffDelayMs > 1500)
        {
            optLightOff = 1000;
            optimizations.Add($"Reduce light-off delay: {lightOffDelayMs}ms → {optLightOff}ms");
        }

        // Optimization 2: reduce lift distance if cross-section is small
        if (maxCrossSectionAreaMm2 < 1000 && liftDistMm > 4)
        {
            optLiftDist = 4f;
            optimizations.Add($"Reduce lift distance: {liftDistMm}mm → {optLiftDist}mm (small cross-section)");
        }

        // Optimization 3: increase lift speed if force is low
        if (maxCrossSectionAreaMm2 < 500 && liftSpeedMmPerMin < 180)
        {
            optLiftSpeed = 180f;
            optimizations.Add($"Increase lift speed: {liftSpeedMmPerMin}mm/min → {optLiftSpeed}mm/min");
        }

        // Optimization 4: temperature-adjust exposure
        if (ambientTempC > 28)
        {
            var tempAdj = TemperatureCompensator.Compensate(normalExposureMs, ambientTempC);
            if (tempAdj.AdjustedExposureMs < normalExposureMs * 0.9f)
            {
                optNormalExp = tempAdj.AdjustedExposureMs;
                optimizations.Add($"Temperature-adjusted exposure: {normalExposureMs}ms → {optNormalExp:F0}ms ({ambientTempC}°C)");
            }
        }

        float optLayerTime = optNormalExp / 60000f +
            (optLiftDist / (optLiftSpeed / 60f) + optLiftDist / (retractSpeedMmPerMin / 60f)) / 60f +
            optLightOff / 60000f;
        optTime = bottomLayers * origBottomTime + (totalLayers - bottomLayers) * optLayerTime;

        if (optimizations.Count == 0) optimizations.Add("No optimizations applicable — settings already optimal");

        return new OptimizationPlan
        {
            OriginalTimeMinutes = origTime,
            OptimizedTimeMinutes = optTime,
            TimeSavedMinutes = origTime - optTime,
            TimeSavedPct = origTime > 0 ? (origTime - optTime) / origTime * 100f : 0,
            Optimizations = optimizations,
        };
    }
}
