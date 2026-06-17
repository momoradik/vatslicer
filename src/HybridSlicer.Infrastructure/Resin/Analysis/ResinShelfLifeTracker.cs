namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Tracks resin bottle opened date and estimates remaining shelf life.
/// Most resins expire 6-12 months after opening due to UV sensitivity
/// and moisture absorption.
/// </summary>
public static class ResinShelfLifeTracker
{
    public sealed record ShelfLifeEstimate
    {
        public required int DaysSinceOpened { get; init; }
        public required int EstimatedShelfLifeDays { get; init; }
        public required int DaysRemaining { get; init; }
        public required float RemainingPct { get; init; }
        public required string Status { get; init; }
        public required string Warning { get; init; }
    }

    public static ShelfLifeEstimate Estimate(
        DateTimeOffset openedDate,
        DateTimeOffset currentDate,
        string resinType = "standard",
        bool storedInDark = true)
    {
        int daysSinceOpened = (int)(currentDate - openedDate).TotalDays;

        int baseShelfLifeDays = resinType.ToLowerInvariant() switch
        {
            "standard" => 365,
            "abs-like" or "tough" => 270,
            "flexible" => 240,
            "castable" or "wax" => 180,
            "ceramic" => 150,
            "water-washable" => 200,
            _ => 300,
        };

        if (!storedInDark) baseShelfLifeDays = (int)(baseShelfLifeDays * 0.6f);

        int remaining = Math.Max(0, baseShelfLifeDays - daysSinceOpened);
        float remainingPct = baseShelfLifeDays > 0 ? remaining * 100f / baseShelfLifeDays : 0;

        string status = remainingPct > 60 ? "fresh" : remainingPct > 30 ? "aging" : remainingPct > 0 ? "expiring" : "expired";
        string warning = status switch
        {
            "fresh" => "Resin is fresh — no concerns",
            "aging" => "Resin aging — use within a few months. Shake well before use.",
            "expiring" => "Resin nearing expiry — may need longer exposure times. Test before critical prints.",
            "expired" => "Resin likely expired — cure test recommended. Increase exposure 20-50%.",
            _ => "",
        };

        return new ShelfLifeEstimate
        {
            DaysSinceOpened = daysSinceOpened,
            EstimatedShelfLifeDays = baseShelfLifeDays,
            DaysRemaining = remaining,
            RemainingPct = remainingPct,
            Status = status,
            Warning = warning,
        };
    }
}
