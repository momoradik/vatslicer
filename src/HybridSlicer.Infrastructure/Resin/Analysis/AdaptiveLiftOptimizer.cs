namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes per-layer lift speed based on peel force profile.
/// High-force layers get slower lift to reduce failure risk.
/// Low-force layers get faster lift to save time.
/// </summary>
public static class AdaptiveLiftOptimizer
{
    public sealed record LayerLiftParams
    {
        public required float ZMm { get; init; }
        public required float LiftSpeedMmPerMin { get; init; }
        public required float LiftDistanceMm { get; init; }
        public required float RetractSpeedMmPerMin { get; init; }
    }

    public sealed record OptimizationResult
    {
        public required List<LayerLiftParams> Layers { get; init; }
        public required float TimeSavedMinutes { get; init; }
        public required float TimeSavedPct { get; init; }
        public required int SlowedLayers { get; init; }
        public required int AcceleratedLayers { get; init; }
    }

    /// <summary>
    /// Optimize lift parameters per-layer based on peel force.
    /// </summary>
    public static OptimizationResult Optimize(
        PeelForceProfiler.ForceProfile profile,
        float baseLiftSpeedMmPerMin = 120f,
        float minLiftSpeedMmPerMin = 30f,
        float maxLiftSpeedMmPerMin = 300f,
        float baseLiftDistMm = 5f,
        float retractSpeedMmPerMin = 240f)
    {
        if (profile.Layers.Count == 0)
            return new OptimizationResult { Layers = new(), TimeSavedMinutes = 0, TimeSavedPct = 0, SlowedLayers = 0, AcceleratedLayers = 0 };

        float avgForce = profile.AvgPeelForceN;
        if (avgForce < 0.001f) avgForce = 0.001f;

        var layers = new List<LayerLiftParams>(profile.Layers.Count);
        float originalTime = 0;
        float optimizedTime = 0;
        int slowed = 0, accelerated = 0;

        foreach (var layer in profile.Layers)
        {
            float forceRatio = layer.PeelForceN / avgForce;

            // Speed inversely proportional to force ratio
            // High force → slow down, low force → speed up
            float speedFactor = 1f / MathF.Max(forceRatio, 0.2f);
            float liftSpeed = Math.Clamp(baseLiftSpeedMmPerMin * speedFactor, minLiftSpeedMmPerMin, maxLiftSpeedMmPerMin);

            // Higher force → more lift distance (reduce suction)
            float liftDist = forceRatio > 1.5f ? baseLiftDistMm * 1.5f : baseLiftDistMm;

            if (liftSpeed < baseLiftSpeedMmPerMin * 0.9f) slowed++;
            else if (liftSpeed > baseLiftSpeedMmPerMin * 1.1f) accelerated++;

            float origLayerTime = (baseLiftDistMm * 2) / (baseLiftSpeedMmPerMin / 60f);
            float optLayerTime = (liftDist / (liftSpeed / 60f)) + (liftDist / (retractSpeedMmPerMin / 60f));
            originalTime += origLayerTime;
            optimizedTime += optLayerTime;

            layers.Add(new LayerLiftParams
            {
                ZMm = layer.ZMm,
                LiftSpeedMmPerMin = liftSpeed,
                LiftDistanceMm = liftDist,
                RetractSpeedMmPerMin = retractSpeedMmPerMin,
            });
        }

        float saved = (originalTime - optimizedTime) / 60f;
        float savedPct = originalTime > 0 ? (originalTime - optimizedTime) / originalTime * 100f : 0;

        return new OptimizationResult
        {
            Layers = layers,
            TimeSavedMinutes = saved,
            TimeSavedPct = savedPct,
            SlowedLayers = slowed,
            AcceleratedLayers = accelerated,
        };
    }
}
