namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Manages multiple resin bottles — tracks which bottle is active,
/// remaining volume per bottle, and suggests which bottle to use for
/// the next job.
/// </summary>
public static class ResinBottleManager
{
    public sealed record Bottle
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string ResinType { get; init; }
        public required float CapacityMl { get; init; }
        public required float UsedMl { get; init; }
        public float RemainingMl => Math.Max(0, CapacityMl - UsedMl);
        public float RemainingPct => CapacityMl > 0 ? RemainingMl / CapacityMl * 100 : 0;
    }

    public sealed record BottleRecommendation
    {
        public required string? RecommendedBottleId { get; init; }
        public required string Reason { get; init; }
        public required bool NeedNewBottle { get; init; }
    }

    public static BottleRecommendation Recommend(
        IReadOnlyList<Bottle> bottles,
        float requiredMl,
        string? preferredResinType = null)
    {
        // Filter by resin type if specified
        var candidates = preferredResinType != null
            ? bottles.Where(b => string.Equals(b.ResinType, preferredResinType, StringComparison.OrdinalIgnoreCase)).ToList()
            : bottles.ToList();

        if (candidates.Count == 0)
            return new BottleRecommendation { RecommendedBottleId = null, Reason = "No bottles of the requested resin type", NeedNewBottle = true };

        // Find bottle with most remaining that has enough for the job
        var sufficient = candidates.Where(b => b.RemainingMl >= requiredMl).OrderByDescending(b => b.RemainingMl).ToList();
        if (sufficient.Count > 0)
        {
            var best = sufficient[0];
            return new BottleRecommendation
            {
                RecommendedBottleId = best.Id,
                Reason = $"Use {best.Name} ({best.RemainingMl:F0}ml remaining, job needs {requiredMl:F0}ml)",
                NeedNewBottle = false,
            };
        }

        // No single bottle has enough — use the one with most remaining
        var mostRemaining = candidates.OrderByDescending(b => b.RemainingMl).First();
        return new BottleRecommendation
        {
            RecommendedBottleId = mostRemaining.Id,
            Reason = $"No bottle has enough ({requiredMl:F0}ml needed). {mostRemaining.Name} has {mostRemaining.RemainingMl:F0}ml — need {requiredMl - mostRemaining.RemainingMl:F0}ml more.",
            NeedNewBottle = true,
        };
    }
}
