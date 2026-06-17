namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Final pre-print readiness check that verifies ALL requirements are met:
/// - Model loaded and validated
/// - Supports generated
/// - Sliced
/// - Export format selected
/// - Printer connected (optional)
/// Returns a go/no-go verdict with checklist.
/// </summary>
public static class PrintReadinessChecker
{
    public sealed record CheckItem
    {
        public required string Name { get; init; }
        public required bool Passed { get; init; }
        public required string Detail { get; init; }
    }

    public sealed record ReadinessResult
    {
        public required bool Ready { get; init; }
        public required List<CheckItem> Checks { get; init; }
        public required int PassedCount { get; init; }
        public required int TotalCount { get; init; }
    }

    public static ReadinessResult Check(
        bool modelLoaded,
        bool meshValid,
        bool supportsGenerated,
        bool sliced,
        bool formatSelected,
        bool printerConnected = false,
        float? printabilityScore = null,
        bool? enoughResin = null)
    {
        var checks = new List<CheckItem>
        {
            new() { Name = "Model loaded", Passed = modelLoaded, Detail = modelLoaded ? "Model file loaded" : "No model — import STL/OBJ/3MF" },
            new() { Name = "Mesh valid", Passed = meshValid, Detail = meshValid ? "Mesh passes validation" : "Mesh has errors — run repair" },
            new() { Name = "Supports generated", Passed = supportsGenerated, Detail = supportsGenerated ? "Supports ready" : "Generate supports first" },
            new() { Name = "Sliced", Passed = sliced, Detail = sliced ? "Slice complete" : "Click Slice to generate layers" },
            new() { Name = "Export format", Passed = formatSelected, Detail = formatSelected ? "Format selected" : "Select export format in Printer Setup" },
        };

        if (printerConnected)
            checks.Add(new() { Name = "Printer connected", Passed = true, Detail = "Printer online" });

        if (printabilityScore.HasValue)
        {
            bool ok = printabilityScore.Value >= 60;
            checks.Add(new() { Name = "Printability score", Passed = ok,
                Detail = ok ? $"Score: {printabilityScore.Value}/100" : $"Low score: {printabilityScore.Value}/100 — review issues" });
        }

        if (enoughResin.HasValue)
            checks.Add(new() { Name = "Resin level", Passed = enoughResin.Value,
                Detail = enoughResin.Value ? "Enough resin" : "Resin level low — refill needed" });

        int passed = checks.Count(c => c.Passed);
        bool ready = checks.All(c => c.Passed);

        return new ReadinessResult
        {
            Ready = ready,
            Checks = checks,
            PassedCount = passed,
            TotalCount = checks.Count,
        };
    }
}
