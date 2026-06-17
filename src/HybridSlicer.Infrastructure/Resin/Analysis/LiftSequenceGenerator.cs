namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates a two-stage lift sequence (TSMC style) for advanced printers.
/// Stage 1: slow initial lift to break FEP adhesion (peel)
/// Stage 2: fast lift to clear height for recoat
/// Reduces total lift time while keeping peel force low.
/// </summary>
public static class LiftSequenceGenerator
{
    public sealed record LiftSequence
    {
        public required float Stage1DistMm { get; init; }
        public required float Stage1SpeedMmPerMin { get; init; }
        public required float Stage2DistMm { get; init; }
        public required float Stage2SpeedMmPerMin { get; init; }
        public required float RetractSpeedMmPerMin { get; init; }
        public required float TotalLiftTimeSec { get; init; }
        public required float TimeSavedVsSingleStagePct { get; init; }
    }

    public static LiftSequence Generate(
        float totalLiftDistMm = 6f,
        float peelForceN = 1.5f,
        float normalLiftSpeedMmPerMin = 120f,
        float maxLiftSpeedMmPerMin = 300f,
        float retractSpeedMmPerMin = 240f)
    {
        // Stage 1: slow peel (first 1-2mm to break adhesion)
        float peelDist = Math.Clamp(1f + peelForceN * 0.3f, 1f, 3f);
        float peelSpeed = Math.Clamp(normalLiftSpeedMmPerMin * 0.5f / Math.Max(peelForceN, 0.5f), 20f, normalLiftSpeedMmPerMin);

        // Stage 2: fast travel (remaining distance)
        float travelDist = totalLiftDistMm - peelDist;
        float travelSpeed = Math.Min(maxLiftSpeedMmPerMin, normalLiftSpeedMmPerMin * 2.5f);

        float stage1Time = peelDist / (peelSpeed / 60f);
        float stage2Time = travelDist > 0 ? travelDist / (travelSpeed / 60f) : 0;
        float totalTime = stage1Time + stage2Time;

        // Compare to single-stage
        float singleStageTime = totalLiftDistMm / (normalLiftSpeedMmPerMin / 60f);
        float savedPct = singleStageTime > 0 ? (singleStageTime - totalTime) / singleStageTime * 100f : 0;

        return new LiftSequence
        {
            Stage1DistMm = peelDist,
            Stage1SpeedMmPerMin = peelSpeed,
            Stage2DistMm = Math.Max(0, travelDist),
            Stage2SpeedMmPerMin = travelSpeed,
            RetractSpeedMmPerMin = retractSpeedMmPerMin,
            TotalLiftTimeSec = totalTime,
            TimeSavedVsSingleStagePct = savedPct,
        };
    }
}
