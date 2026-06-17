namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates FEP/nFEP film wear from accumulated peel stress across print jobs.
/// Tracks cumulative peel force-hours and predicts remaining film life.
/// </summary>
public static class FepLifeEstimator
{
    public sealed record FilmLifeEstimate
    {
        public required float CumulativePeelForceHours { get; init; }
        public required float EstimatedLifeHours { get; init; }
        public required float RemainingLifePct { get; init; }
        public required string Status { get; init; } // "new", "good", "worn", "replace"
    }

    /// <summary>
    /// Estimate remaining FEP life based on cumulative peel stress.
    /// </summary>
    /// <param name="totalPrintHours">Total hours of printing on this film.</param>
    /// <param name="avgPeelForceN">Average peel force across prints.</param>
    /// <param name="filmType">"FEP" or "nFEP".</param>
    public static FilmLifeEstimate Estimate(
        float totalPrintHours,
        float avgPeelForceN = 1f,
        string filmType = "FEP")
    {
        // Typical FEP film life: 50-100 hours at moderate force
        // nFEP: 80-150 hours (more durable)
        float baseLifeHours = filmType.ToLowerInvariant() == "nfep" ? 120f : 75f;

        // Force multiplier: higher average force wears faster
        float forceMultiplier = Math.Max(0.5f, avgPeelForceN / 1.5f);
        float effectiveLife = baseLifeHours / forceMultiplier;

        float cumulativeStress = totalPrintHours * forceMultiplier;
        float remainingPct = Math.Max(0, 100f * (1f - totalPrintHours / effectiveLife));

        string status = remainingPct > 70 ? "new" : remainingPct > 40 ? "good" : remainingPct > 15 ? "worn" : "replace";

        return new FilmLifeEstimate
        {
            CumulativePeelForceHours = cumulativeStress,
            EstimatedLifeHours = effectiveLife,
            RemainingLifePct = remainingPct,
            Status = status,
        };
    }
}
