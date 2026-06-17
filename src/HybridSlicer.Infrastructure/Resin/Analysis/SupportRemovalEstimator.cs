namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates difficulty of support removal based on contact depth,
/// tip diameter, number of supports, and surface area of contact.
/// Lower contact depth + thinner tips = easier removal.
/// </summary>
public static class SupportRemovalEstimator
{
    public sealed record RemovalEstimate
    {
        public required float Difficulty { get; init; } // 0-10 scale
        public required string Rating { get; init; } // "easy", "moderate", "hard", "very hard"
        public required float TotalContactAreaMm2 { get; init; }
        public required float AvgTipDiameterMm { get; init; }
        public required int SupportCount { get; init; }
        public required string Advice { get; init; }
    }

    public static RemovalEstimate Estimate(
        int supportCount,
        float avgTipRadiusMm,
        float avgContactDepthMm,
        float totalContactAreaMm2)
    {
        // Difficulty factors (each 0-1, weighted)
        float tipFactor = Math.Clamp(avgTipRadiusMm / 0.5f, 0, 1); // larger tip = harder
        float depthFactor = Math.Clamp(avgContactDepthMm / 0.5f, 0, 1); // deeper = harder
        float countFactor = Math.Clamp(supportCount / 100f, 0, 1); // more supports = harder
        float areaFactor = Math.Clamp(totalContactAreaMm2 / 50f, 0, 1); // more contact area = harder

        float difficulty = (tipFactor * 3 + depthFactor * 3 + countFactor * 2 + areaFactor * 2);
        difficulty = Math.Clamp(difficulty, 0, 10);

        string rating = difficulty < 2.5f ? "easy" : difficulty < 5f ? "moderate" : difficulty < 7.5f ? "hard" : "very hard";
        string advice = rating switch
        {
            "easy" => "Supports should snap off cleanly with minimal sanding",
            "moderate" => "Use flush cutters. Light sanding recommended for contact marks",
            "hard" => "Careful removal needed. Consider reducing contact depth or using thinner tips",
            "very hard" => "High risk of surface damage. Reduce tip size, use point contact shape, or lower contact depth",
            _ => "",
        };

        return new RemovalEstimate
        {
            Difficulty = difficulty,
            Rating = rating,
            TotalContactAreaMm2 = totalContactAreaMm2,
            AvgTipDiameterMm = avgTipRadiusMm * 2,
            SupportCount = supportCount,
            Advice = advice,
        };
    }
}
