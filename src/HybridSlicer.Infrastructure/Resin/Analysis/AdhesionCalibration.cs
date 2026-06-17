namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Adhesion pressure (P_ADH) calibration data for resin+film combinations.
///
/// P_ADH is the single most important calibration constant in support sizing.
/// It determines how much force each support must carry, which drives tip/pillar
/// radius. Too low → supports fail during peel. Too high → oversized supports
/// that are hard to remove and leave marks.
///
/// These presets are derived from published peel force measurements and
/// ChiTuBox/Lychee default sizing back-calculation.
/// </summary>
public static class AdhesionCalibration
{
    /// <summary>A calibrated adhesion pressure for a specific resin+film combination.</summary>
    public sealed record AdhesionPreset
    {
        /// <summary>Human-readable name.</summary>
        public required string Name { get; init; }
        /// <summary>Resin category.</summary>
        public required string ResinCategory { get; init; }
        /// <summary>Film type.</summary>
        public required string FilmType { get; init; }
        /// <summary>Adhesion pressure in N/mm2.</summary>
        public required float PAdhNPerMm2 { get; init; }
        /// <summary>Source of the calibration data.</summary>
        public required string Source { get; init; }
        /// <summary>Confidence level (0-1). Lower = less empirical data.</summary>
        public required float Confidence { get; init; }
    }

    /// <summary>
    /// Built-in presets for common resin+film combinations.
    /// Values are approximate and should be refined with test prints.
    /// </summary>
    public static readonly IReadOnlyList<AdhesionPreset> Presets = new[]
    {
        new AdhesionPreset
        {
            Name = "Standard Resin + FEP",
            ResinCategory = "Standard",
            FilmType = "FEP",
            PAdhNPerMm2 = 0.015f,
            Source = "Back-calculated from ChiTuBox Medium preset",
            Confidence = 0.7f,
        },
        new AdhesionPreset
        {
            Name = "Standard Resin + nFEP",
            ResinCategory = "Standard",
            FilmType = "nFEP",
            PAdhNPerMm2 = 0.010f,
            Source = "nFEP has ~30% lower adhesion than FEP (published comparisons)",
            Confidence = 0.6f,
        },
        new AdhesionPreset
        {
            Name = "ABS-Like Resin + FEP",
            ResinCategory = "ABS-Like",
            FilmType = "FEP",
            PAdhNPerMm2 = 0.020f,
            Source = "ABS-like resins have higher adhesion due to increased cure strength",
            Confidence = 0.5f,
        },
        new AdhesionPreset
        {
            Name = "Flexible Resin + FEP",
            ResinCategory = "Flexible",
            FilmType = "FEP",
            PAdhNPerMm2 = 0.008f,
            Source = "Flexible resins peel more easily due to lower stiffness",
            Confidence = 0.5f,
        },
        new AdhesionPreset
        {
            Name = "Castable/Wax Resin + FEP",
            ResinCategory = "Castable",
            FilmType = "FEP",
            PAdhNPerMm2 = 0.012f,
            Source = "Castable resins: moderate adhesion, brittle green state",
            Confidence = 0.4f,
        },
        new AdhesionPreset
        {
            Name = "Ceramic-Filled Resin + FEP",
            ResinCategory = "Ceramic",
            FilmType = "FEP",
            PAdhNPerMm2 = 0.025f,
            Source = "Ceramic-filled resins have high adhesion and heavy cross-sections",
            Confidence = 0.4f,
        },
        new AdhesionPreset
        {
            Name = "Water-Washable Resin + FEP",
            ResinCategory = "Water-Washable",
            FilmType = "FEP",
            PAdhNPerMm2 = 0.018f,
            Source = "Water-washable resins: slightly higher adhesion than standard",
            Confidence = 0.5f,
        },
    };

    /// <summary>
    /// Look up the best P_ADH for a given resin category and film type.
    /// Falls back to the default if no match is found.
    /// </summary>
    public static float GetPAdh(string? resinCategory, string? filmType)
    {
        if (string.IsNullOrEmpty(resinCategory))
            return SupportSizer.P_ADH_DEFAULT;

        foreach (var preset in Presets)
        {
            if (string.Equals(preset.ResinCategory, resinCategory, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(filmType) &&
                    string.Equals(preset.FilmType, filmType, StringComparison.OrdinalIgnoreCase))
                    return preset.PAdhNPerMm2;
            }
        }

        // Try category-only match (ignore film type)
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.ResinCategory, resinCategory, StringComparison.OrdinalIgnoreCase))
                return preset.PAdhNPerMm2;
        }

        return SupportSizer.P_ADH_DEFAULT;
    }
}
