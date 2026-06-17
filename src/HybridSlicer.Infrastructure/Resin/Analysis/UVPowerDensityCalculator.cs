namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Calculates UV power density at the build surface based on LED array specs.
/// Used to convert exposure time to energy dose (mJ/cm2) for resin datasheet matching.
/// </summary>
public static class UVPowerDensityCalculator
{
    public sealed record PowerDensityResult
    {
        public required float PowerDensityMwPerCm2 { get; init; }
        public required float EnergyDoseMjPerCm2 { get; init; }
        public required float RecommendedExposureMs { get; init; }
        public required string DoseCategory { get; init; }
    }

    /// <summary>
    /// Calculate energy dose from exposure time and UV power density.
    /// </summary>
    /// <param name="exposureMs">Exposure time in ms.</param>
    /// <param name="uvPowerMw">Total UV LED power (mW). Typical mono: 40-80W.</param>
    /// <param name="buildAreaCm2">Build plate area (cm2).</param>
    /// <param name="uniformityPct">UV uniformity across plate (%). Typical: 80-95%.</param>
    public static PowerDensityResult Calculate(
        float exposureMs,
        float uvPowerMw = 50000f,
        float buildAreaCm2 = 230f,
        float uniformityPct = 90f)
    {
        float effectivePower = uvPowerMw * uniformityPct / 100f;
        float densityMwCm2 = buildAreaCm2 > 0 ? effectivePower / buildAreaCm2 : 0;
        float doseMjCm2 = densityMwCm2 * exposureMs / 1000f;

        string category = doseMjCm2 < 10 ? "under-exposed" :
            doseMjCm2 < 30 ? "light" :
            doseMjCm2 < 60 ? "standard" :
            doseMjCm2 < 100 ? "heavy" : "over-exposed";

        // Recommended exposure for standard dose of 40 mJ/cm2
        float recMs = densityMwCm2 > 0 ? 40f * 1000f / densityMwCm2 : 2000;

        return new PowerDensityResult
        {
            PowerDensityMwPerCm2 = densityMwCm2,
            EnergyDoseMjPerCm2 = doseMjCm2,
            RecommendedExposureMs = recMs,
            DoseCategory = category,
        };
    }
}
