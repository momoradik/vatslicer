namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Maps grayscale pixel values to effective exposure times.
/// In anti-aliased printing, gray pixels get proportionally less UV exposure.
/// This calculator converts between grayscale value (0-255) and effective
/// exposure time, accounting for LCD response curve nonlinearity.
/// </summary>
public static class GrayScaleExposureMapper
{
    public sealed record ExposureMapping
    {
        public required byte GrayValue { get; init; }
        public required float EffectiveExposureMs { get; init; }
        public required float ExposurePct { get; init; }
        public required float CureDepthMm { get; init; }
    }

    /// <summary>
    /// Calculate effective exposure for a grayscale value.
    /// LCD panels have non-linear response: gamma correction.
    /// </summary>
    public static ExposureMapping Map(
        byte grayValue,
        float normalExposureMs,
        float layerHeightMm = 0.05f,
        float gamma = 2.2f)
    {
        float normalized = grayValue / 255f;
        // Apply gamma correction (LCD response curve)
        float linear = MathF.Pow(normalized, gamma);
        float effectiveMs = normalExposureMs * linear;
        float pct = linear * 100f;

        // Cure depth estimation (Beer-Lambert: depth ∝ log(dose))
        // At full exposure, cure depth ≈ 2-3x layer height
        float fullCureDepth = layerHeightMm * 2.5f;
        float cureDepth = linear > 0.01f ? fullCureDepth * (1f + MathF.Log(linear) / 5f) : 0;
        cureDepth = Math.Clamp(cureDepth, 0, fullCureDepth);

        return new ExposureMapping
        {
            GrayValue = grayValue,
            EffectiveExposureMs = effectiveMs,
            ExposurePct = pct,
            CureDepthMm = cureDepth,
        };
    }

    /// <summary>
    /// Find the gray value that produces a target cure depth.
    /// </summary>
    public static byte GrayValueForCureDepth(float targetDepthMm, float normalExposureMs, float layerHeightMm = 0.05f)
    {
        // Binary search
        int lo = 0, hi = 255;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            var mapping = Map((byte)mid, normalExposureMs, layerHeightMm);
            if (mapping.CureDepthMm < targetDepthMm) lo = mid;
            else hi = mid - 1;
        }
        return (byte)Math.Clamp(lo, 0, 255);
    }
}
