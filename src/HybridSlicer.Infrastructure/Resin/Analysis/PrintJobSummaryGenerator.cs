namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates a human-readable print job summary combining all analysis results.
/// Intended for display in the UI or export as a report card.
/// </summary>
public static class PrintJobSummaryGenerator
{
    public sealed record JobSummary
    {
        public required string ModelName { get; init; }
        public required string PrinterName { get; init; }
        public required string ResinType { get; init; }
        public required int Layers { get; init; }
        public required string PrintTime { get; init; }
        public required string Volume { get; init; }
        public required string Weight { get; init; }
        public required string Cost { get; init; }
        public required int PrintabilityScore { get; init; }
        public required string Grade { get; init; }
        public required int IssueCount { get; init; }
        public required List<string> Issues { get; init; }
        public required string Verdict { get; init; }
    }

    public static JobSummary Generate(
        string modelName,
        string printerName,
        string resinType,
        int layers,
        float printTimeMinutes,
        float volumeMl,
        float weightG,
        float costUsd,
        int printabilityScore,
        string grade,
        List<string> issues,
        string verdict)
    {
        string timeStr = printTimeMinutes < 60 ? $"{printTimeMinutes:F0} min" :
            printTimeMinutes < 1440 ? $"{printTimeMinutes / 60:F1} hours" :
            $"{printTimeMinutes / 1440:F1} days";

        return new JobSummary
        {
            ModelName = modelName,
            PrinterName = printerName,
            ResinType = resinType,
            Layers = layers,
            PrintTime = timeStr,
            Volume = $"{volumeMl:F1} ml",
            Weight = $"{weightG:F1} g",
            Cost = $"${costUsd:F2}",
            PrintabilityScore = printabilityScore,
            Grade = grade,
            IssueCount = issues.Count,
            Issues = issues,
            Verdict = verdict,
        };
    }
}
