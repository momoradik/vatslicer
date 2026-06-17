namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Calculates build plate adhesion requirements based on model footprint,
/// bottom exposure, and support base configuration. Recommends bottom
/// layer count and exposure for reliable first-layer adhesion.
/// </summary>
public static class BuildPlateAdhesionCalculator
{
    public sealed record AdhesionPlan
    {
        public required int RecommendedBottomLayers { get; init; }
        public required float RecommendedBottomExposureMs { get; init; }
        public required float ModelFootprintMm2 { get; init; }
        public required float AdhesionForceFactor { get; init; }
        public required string Confidence { get; init; }
    }

    public static AdhesionPlan Calculate(
        float modelFootprintMm2,
        float modelHeightMm,
        float modelVolumeMm3,
        float baseExposureMs = 30000,
        string plateType = "aluminum")
    {
        // Adhesion force factor: footprint × exposure intensity
        float densityFactor = modelVolumeMm3 > 0 ? modelFootprintMm2 / (modelVolumeMm3 / modelHeightMm) : 1;

        // More bottom layers for: heavy models, small footprint, tall models
        float heightFactor = Math.Clamp(modelHeightMm / 50f, 0.5f, 2f);
        float footprintFactor = Math.Clamp(1000f / Math.Max(modelFootprintMm2, 100f), 0.5f, 2f);

        int recLayers = (int)Math.Clamp(3 * heightFactor * footprintFactor, 3, 12);

        // More exposure for aluminum (vs flex plate)
        float plateMult = plateType.ToLowerInvariant() switch
        {
            "flex" or "pei" => 0.7f,
            "glass" => 1.2f,
            _ => 1.0f,
        };
        float recExposure = baseExposureMs * plateMult * Math.Clamp(heightFactor, 0.8f, 1.5f);

        string confidence = modelFootprintMm2 > 500 ? "high" :
            modelFootprintMm2 > 100 ? "medium" : "low — small footprint may detach";

        return new AdhesionPlan
        {
            RecommendedBottomLayers = recLayers,
            RecommendedBottomExposureMs = recExposure,
            ModelFootprintMm2 = modelFootprintMm2,
            AdhesionForceFactor = densityFactor,
            Confidence = confidence,
        };
    }
}
