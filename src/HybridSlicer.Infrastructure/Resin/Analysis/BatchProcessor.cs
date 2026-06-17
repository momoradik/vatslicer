namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Processes multiple models in batch: analyze, generate supports, and
/// compute nesting layout. Returns a combined report for all models.
/// </summary>
public static class BatchProcessor
{
    public sealed record BatchItem
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required StlMesh Mesh { get; init; }
    }

    public sealed record BatchResult
    {
        public required int TotalModels { get; init; }
        public required int PassedModels { get; init; }
        public required int WarningModels { get; init; }
        public required int FailedModels { get; init; }
        public required float TotalVolumeMm3 { get; init; }
        public required float TotalEstimatedCostUsd { get; init; }
        public required long TotalElapsedMs { get; init; }
        public required List<ItemResult> Items { get; init; }
    }

    public sealed record ItemResult
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required int PrintabilityScore { get; init; }
        public required string Grade { get; init; }
        public required float VolumeMm3 { get; init; }
        public required int IssueCount { get; init; }
    }

    public static BatchResult Process(IReadOnlyList<BatchItem> items)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<ItemResult>();
        float totalVol = 0;
        int passed = 0, warned = 0, failed = 0;

        foreach (var item in items)
        {
            var report = ComprehensiveModelAnalyzer.Analyze(item.Mesh);
            var score = PrintabilityScorer.Compute(report);

            if (score.Grade == "A" || score.Grade == "B") passed++;
            else if (score.Grade == "C" || score.Grade == "D") warned++;
            else failed++;

            totalVol += report.VolumeMm3;
            results.Add(new ItemResult
            {
                Id = item.Id, Name = item.Name,
                PrintabilityScore = score.Total, Grade = score.Grade,
                VolumeMm3 = report.VolumeMm3,
                IssueCount = report.Issues.Count,
            });
        }

        return new BatchResult
        {
            TotalModels = items.Count,
            PassedModels = passed, WarningModels = warned, FailedModels = failed,
            TotalVolumeMm3 = totalVol,
            TotalEstimatedCostUsd = totalVol / 1000f * 0.05f,
            TotalElapsedMs = sw.ElapsedMilliseconds,
            Items = results,
        };
    }
}
