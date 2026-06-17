using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Analyzes support efficiency: material used per unit of supported area.
/// Identifies over-supported and under-supported regions.
/// Lower ratio = more efficient supports.
/// </summary>
public static class SupportEfficiencyAnalyzer
{
    public sealed record EfficiencyReport
    {
        public required float SupportVolumeMm3 { get; init; }
        public required float SupportedAreaMm2 { get; init; }
        public required float VolumePerAreaRatio { get; init; }
        public required string Efficiency { get; init; }
        public required float EstimatedWasteVolumeMm3 { get; init; }
        public required string Suggestion { get; init; }
    }

    public static EfficiencyReport Analyze(
        float supportVolumeMm3,
        float overhangAreaMm2,
        int supportCount,
        float modelVolumeMm3)
    {
        float ratio = overhangAreaMm2 > 0 ? supportVolumeMm3 / overhangAreaMm2 : 0;
        float supportPct = modelVolumeMm3 > 0 ? supportVolumeMm3 / modelVolumeMm3 * 100 : 0;

        // Ideal ratio is ~0.5-2 mm3 per mm2 of overhang
        string efficiency;
        float wasteVol;
        string suggestion;

        if (ratio < 0.3f)
        {
            efficiency = "under-supported";
            wasteVol = 0;
            suggestion = "Support volume very low — increase density or tip diameter to prevent failures";
        }
        else if (ratio < 1.5f)
        {
            efficiency = "optimal";
            wasteVol = 0;
            suggestion = "Good support efficiency — minimal material waste";
        }
        else if (ratio < 4f)
        {
            efficiency = "moderate";
            wasteVol = (ratio - 1.5f) * overhangAreaMm2;
            suggestion = "Consider reducing support density or pillar diameter to save resin";
        }
        else
        {
            efficiency = "over-supported";
            wasteVol = (ratio - 1.5f) * overhangAreaMm2;
            suggestion = $"Excessive supports — {supportPct:F0}% of model volume. Reduce density significantly";
        }

        return new EfficiencyReport
        {
            SupportVolumeMm3 = supportVolumeMm3,
            SupportedAreaMm2 = overhangAreaMm2,
            VolumePerAreaRatio = ratio,
            Efficiency = efficiency,
            EstimatedWasteVolumeMm3 = wasteVol,
            Suggestion = suggestion,
        };
    }
}
