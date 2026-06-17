namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Calculates optimal layer height based on model geometry and target quality.
/// Thinner layers = smoother surface but longer print time.
/// Reports the quality/speed tradeoff at different heights.
/// </summary>
public static class OptimalLayerHeightCalculator
{
    public sealed record LayerHeightOption
    {
        public required float HeightMm { get; init; }
        public required int EstimatedLayers { get; init; }
        public required float EstimatedTimeMinutes { get; init; }
        public required float QualityScore { get; init; }
        public required bool Recommended { get; init; }
    }

    public static List<LayerHeightOption> Calculate(
        float modelHeightMm,
        float normalExposureMs = 2000f,
        float liftTimePerLayerSec = 5f,
        string quality = "standard")
    {
        float[] heights = quality switch
        {
            "precision" => new[] { 0.01f, 0.025f, 0.05f },
            "draft" => new[] { 0.05f, 0.1f, 0.15f },
            _ => new[] { 0.025f, 0.05f, 0.1f },
        };

        float bestScore = 0;
        var options = new List<LayerHeightOption>();

        foreach (float h in heights)
        {
            int layers = (int)Math.Ceiling(modelHeightMm / h);
            float timeMin = layers * (normalExposureMs / 60000f + liftTimePerLayerSec / 60f);
            // Quality: inversely proportional to layer height
            float qualityScore = 1f / (h * 10f + 0.1f);
            // Efficiency: quality per minute
            float efficiency = timeMin > 0 ? qualityScore / timeMin : 0;

            if (efficiency > bestScore) bestScore = efficiency;

            options.Add(new LayerHeightOption
            {
                HeightMm = h,
                EstimatedLayers = layers,
                EstimatedTimeMinutes = timeMin,
                QualityScore = qualityScore,
                Recommended = false,
            });
        }

        // Mark the best efficiency option as recommended
        if (options.Count > 0)
        {
            var best = options.OrderByDescending(o => o.QualityScore / Math.Max(o.EstimatedTimeMinutes, 0.1f)).First();
            var idx = options.IndexOf(best);
            options[idx] = best with { Recommended = true };
        }

        return options;
    }
}
