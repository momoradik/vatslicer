namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Optimizes print queue ordering for batch printing.
/// Groups models by resin type, sorts by height (tallest first to
/// maximize plate utilization), and estimates total batch time.
/// </summary>
public static class PrintQueueOptimizer
{
    public sealed record QueueItem
    {
        public required string ModelName { get; init; }
        public required string ResinType { get; init; }
        public required float HeightMm { get; init; }
        public required float VolumeMl { get; init; }
        public required float EstimatedTimeMinutes { get; init; }
    }

    public sealed record OptimizedQueue
    {
        public required List<List<QueueItem>> Batches { get; init; }
        public required float TotalTimeMinutes { get; init; }
        public required int BatchCount { get; init; }
        public required float TotalVolumeMl { get; init; }
    }

    public static OptimizedQueue Optimize(IReadOnlyList<QueueItem> items)
    {
        // Group by resin type (can't mix resins in one batch)
        var groups = items.GroupBy(i => i.ResinType).ToList();
        var batches = new List<List<QueueItem>>();
        float totalTime = 0, totalVol = 0;

        foreach (var group in groups)
        {
            // Sort by height descending (tallest first = longest, but print concurrently)
            var sorted = group.OrderByDescending(i => i.HeightMm).ToList();
            batches.Add(sorted);

            // Batch time = tallest model's time (all print concurrently)
            float batchTime = sorted.Max(i => i.EstimatedTimeMinutes);
            totalTime += batchTime;
            totalVol += sorted.Sum(i => i.VolumeMl);
        }

        return new OptimizedQueue
        {
            Batches = batches,
            TotalTimeMinutes = totalTime,
            BatchCount = batches.Count,
            TotalVolumeMl = totalVol,
        };
    }
}
