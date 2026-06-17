namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Compensates for elephant foot effect on bottom layers.
/// Bottom layers get over-exposed for bed adhesion, causing the first
/// few layers to spread wider than intended. This calculates the XY
/// inset needed per bottom layer to counteract the spread.
/// </summary>
public static class ElephantFootCompensator
{
    public sealed record CompensationPlan
    {
        public required float[] InsetPerLayerMm { get; init; }
        public required int AffectedLayers { get; init; }
        public required float MaxInsetMm { get; init; }
        public required string Description { get; init; }
    }

    public static CompensationPlan Calculate(
        int bottomLayerCount,
        float bottomExposureMs,
        float normalExposureMs,
        float pixelPitchMm,
        int transitionLayers = 3)
    {
        float overexposureRatio = normalExposureMs > 0 ? bottomExposureMs / normalExposureMs : 1;
        float maxBleedMm = (overexposureRatio - 1f) * pixelPitchMm * 0.8f;
        maxBleedMm = Math.Clamp(maxBleedMm, 0, pixelPitchMm * 3);

        int totalAffected = bottomLayerCount + transitionLayers;
        var insets = new float[totalAffected];

        for (int i = 0; i < totalAffected; i++)
        {
            float t;
            if (i < bottomLayerCount)
                t = 1f; // full compensation for bottom layers
            else
                t = 1f - (float)(i - bottomLayerCount + 1) / (transitionLayers + 1); // fade out

            insets[i] = maxBleedMm * t;
        }

        return new CompensationPlan
        {
            InsetPerLayerMm = insets,
            AffectedLayers = totalAffected,
            MaxInsetMm = maxBleedMm,
            Description = $"Elephant foot compensation: {maxBleedMm:F3}mm inset on {bottomLayerCount} bottom layers, fading over {transitionLayers} transition layers",
        };
    }
}
