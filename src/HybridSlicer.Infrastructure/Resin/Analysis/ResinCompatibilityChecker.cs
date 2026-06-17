namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Checks compatibility between printer settings and resin requirements.
/// Warns if exposure, wavelength, or lift parameters are outside recommended ranges.
/// </summary>
public static class ResinCompatibilityChecker
{
    public sealed record CompatibilityResult
    {
        public required bool Compatible { get; init; }
        public required List<string> Warnings { get; init; }
        public required List<string> Errors { get; init; }
    }

    public static CompatibilityResult Check(
        float printerWavelengthNm,
        float resinWavelengthNm,
        float normalExposureMs,
        float resinRecommendedExposureMs,
        float bottomExposureMs,
        float resinRecommendedBottomMs,
        float liftSpeedMmPerMin,
        float maxRecommendedLiftSpeed = 180f)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        // Wavelength check
        if (Math.Abs(printerWavelengthNm - resinWavelengthNm) > 10)
            errors.Add($"Wavelength mismatch: printer {printerWavelengthNm}nm vs resin {resinWavelengthNm}nm (>10nm difference)");
        else if (Math.Abs(printerWavelengthNm - resinWavelengthNm) > 5)
            warnings.Add($"Wavelength difference: printer {printerWavelengthNm}nm vs resin {resinWavelengthNm}nm (5-10nm)");

        // Exposure check
        if (normalExposureMs < resinRecommendedExposureMs * 0.5f)
            errors.Add($"Normal exposure too low: {normalExposureMs}ms vs recommended {resinRecommendedExposureMs}ms — undercure risk");
        else if (normalExposureMs > resinRecommendedExposureMs * 2f)
            warnings.Add($"Normal exposure very high: {normalExposureMs}ms vs recommended {resinRecommendedExposureMs}ms — overcure/bleed");
        else if (normalExposureMs < resinRecommendedExposureMs * 0.8f)
            warnings.Add($"Normal exposure slightly low: {normalExposureMs}ms (recommended {resinRecommendedExposureMs}ms)");

        // Bottom exposure
        if (bottomExposureMs < resinRecommendedBottomMs * 0.3f)
            errors.Add($"Bottom exposure too low: {bottomExposureMs}ms — adhesion failure risk");

        // Lift speed
        if (liftSpeedMmPerMin > maxRecommendedLiftSpeed * 1.5f)
            warnings.Add($"Lift speed very high: {liftSpeedMmPerMin}mm/min — layer separation risk for viscous resins");

        return new CompatibilityResult
        {
            Compatible = errors.Count == 0,
            Warnings = warnings,
            Errors = errors,
        };
    }
}
