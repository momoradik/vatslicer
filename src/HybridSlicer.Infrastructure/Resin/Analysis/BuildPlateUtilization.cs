using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Analyzes build plate utilization — how efficiently the plate area is used
/// by model footprints. Reports waste percentage, suggests scaling or adding
/// copies to maximize plate usage.
/// </summary>
public static class BuildPlateUtilization
{
    public sealed record UtilizationReport
    {
        public required float PlateAreaMm2 { get; init; }
        public required float ModelFootprintMm2 { get; init; }
        public required float UtilizationPct { get; init; }
        public required float WastePct { get; init; }
        public required int MaxCopies { get; init; }
        public required string Suggestion { get; init; }
    }

    public static UtilizationReport Analyze(
        IReadOnlyList<(float widthMm, float depthMm)> modelFootprints,
        float plateWidthMm, float plateDepthMm,
        float gapMm = 2f, float marginMm = 3f)
    {
        float plateArea = plateWidthMm * plateDepthMm;
        float usableW = plateWidthMm - 2 * marginMm;
        float usableD = plateDepthMm - 2 * marginMm;

        float totalFootprint = 0;
        foreach (var (w, d) in modelFootprints)
            totalFootprint += (w + gapMm) * (d + gapMm);

        float utilPct = plateArea > 0 ? totalFootprint / plateArea * 100f : 0;
        float wastePct = 100f - utilPct;

        // Estimate max copies of the largest model that would fit
        int maxCopies = 0;
        if (modelFootprints.Count > 0)
        {
            var largest = modelFootprints.OrderByDescending(f => f.widthMm * f.depthMm).First();
            int cols = (int)((usableW + gapMm) / (largest.widthMm + gapMm));
            int rows = (int)((usableD + gapMm) / (largest.depthMm + gapMm));
            maxCopies = cols * rows;
        }

        string suggestion;
        if (utilPct > 70) suggestion = "Good plate utilization";
        else if (utilPct > 40) suggestion = $"Consider adding {maxCopies - modelFootprints.Count} more copies to fill the plate";
        else if (maxCopies > modelFootprints.Count * 2) suggestion = $"Plate underutilized — up to {maxCopies} copies would fit";
        else suggestion = "Add more models or scale up for better plate usage";

        return new UtilizationReport
        {
            PlateAreaMm2 = plateArea,
            ModelFootprintMm2 = totalFootprint,
            UtilizationPct = Math.Min(utilPct, 100),
            WastePct = Math.Max(wastePct, 0),
            MaxCopies = maxCopies,
            Suggestion = suggestion,
        };
    }
}
