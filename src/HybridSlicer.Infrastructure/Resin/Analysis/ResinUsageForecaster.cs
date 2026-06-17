namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Predicts when the current resin bottle will run out based on
/// usage history and the planned print job.
/// </summary>
public static class ResinUsageForecaster
{
    public sealed record Forecast
    {
        public required float BottleRemainingMl { get; init; }
        public required float JobRequiredMl { get; init; }
        public required bool EnoughForJob { get; init; }
        public required float RemainingAfterJobMl { get; init; }
        public required int EstimatedPrintsRemaining { get; init; }
        public required string Message { get; init; }
    }

    public static Forecast Predict(
        float bottleCapacityMl,
        float usedMl,
        float jobVolumeMl,
        float avgJobVolumeMl = 0)
    {
        float remaining = Math.Max(0, bottleCapacityMl - usedMl);
        bool enough = remaining >= jobVolumeMl;
        float afterJob = remaining - jobVolumeMl;
        float avgJob = avgJobVolumeMl > 0 ? avgJobVolumeMl : jobVolumeMl;
        int printsLeft = avgJob > 0 ? (int)(remaining / avgJob) : 0;

        string msg;
        if (!enough)
            msg = $"Not enough resin! Need {jobVolumeMl:F1}ml but only {remaining:F1}ml remaining. Refill needed.";
        else if (afterJob < avgJob)
            msg = $"This is likely the last print from this bottle ({afterJob:F1}ml will remain).";
        else
            msg = $"Enough for ~{printsLeft} more prints ({remaining:F1}ml remaining, this job uses {jobVolumeMl:F1}ml).";

        return new Forecast
        {
            BottleRemainingMl = remaining,
            JobRequiredMl = jobVolumeMl,
            EnoughForJob = enough,
            RemainingAfterJobMl = Math.Max(0, afterJob),
            EstimatedPrintsRemaining = printsLeft,
            Message = msg,
        };
    }
}
