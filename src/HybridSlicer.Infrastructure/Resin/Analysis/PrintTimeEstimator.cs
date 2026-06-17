namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates total print time with per-phase breakdown.
/// Accounts for warm-up, bottom layers, normal layers, and cool-down.
/// Returns human-readable formatted time.
/// </summary>
public static class PrintTimeEstimator
{
    public sealed record Estimate
    {
        public required float TotalMinutes { get; init; }
        public required string Formatted { get; init; }
        public required float WarmUpMinutes { get; init; }
        public required float PrintingMinutes { get; init; }
        public required float CoolDownMinutes { get; init; }
    }

    public static Estimate Calculate(
        int totalLayers, int bottomLayers,
        float normalExposureMs, float bottomExposureMs,
        float liftDistMm, float liftSpeedMmPerMin,
        float retractSpeedMmPerMin,
        float bottomLiftDistMm, float bottomLiftSpeedMmPerMin,
        float lightOffDelayMs,
        float warmUpMinutes = 1f, float coolDownMinutes = 0.5f)
    {
        var breakdown = PrintTimeBreakdown.Compute(
            totalLayers, bottomLayers,
            normalExposureMs, bottomExposureMs,
            liftDistMm, liftSpeedMmPerMin,
            retractSpeedMmPerMin,
            bottomLiftDistMm, bottomLiftSpeedMmPerMin,
            lightOffDelayMs);

        float total = warmUpMinutes + breakdown.TotalMinutes + coolDownMinutes;

        string formatted;
        if (total < 60) formatted = $"{total:F0}min";
        else if (total < 1440) formatted = $"{(int)(total/60)}h {(int)(total%60)}min";
        else formatted = $"{(int)(total/1440)}d {(int)(total%1440/60)}h";

        return new Estimate
        {
            TotalMinutes = total,
            Formatted = formatted,
            WarmUpMinutes = warmUpMinutes,
            PrintingMinutes = breakdown.TotalMinutes,
            CoolDownMinutes = coolDownMinutes,
        };
    }
}
