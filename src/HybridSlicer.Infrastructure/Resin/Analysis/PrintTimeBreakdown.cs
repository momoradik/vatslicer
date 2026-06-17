namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Breaks down print time into constituent phases for UI display.
/// </summary>
public static class PrintTimeBreakdown
{
    public sealed record TimeBreakdown
    {
        public required float TotalMinutes { get; init; }
        public required float BottomExposureMinutes { get; init; }
        public required float NormalExposureMinutes { get; init; }
        public required float LiftMinutes { get; init; }
        public required float RetractMinutes { get; init; }
        public required float RestMinutes { get; init; }
        public required float LightOffMinutes { get; init; }
        public required int TotalLayers { get; init; }
        public required int BottomLayers { get; init; }
    }

    public static TimeBreakdown Compute(
        int totalLayers, int bottomLayers,
        float normalExposureMs, float bottomExposureMs,
        float liftDistMm, float liftSpeedMmPerMin,
        float retractSpeedMmPerMin,
        float bottomLiftDistMm, float bottomLiftSpeedMmPerMin,
        float lightOffDelayMs,
        float restAfterLiftMs = 0, float restAfterRetractMs = 0)
    {
        int normalLayers = totalLayers - bottomLayers;
        float retractSpeed = retractSpeedMmPerMin > 0 ? retractSpeedMmPerMin : liftSpeedMmPerMin;

        float bottomExpTotal = bottomLayers * bottomExposureMs / 60000f;
        float normalExpTotal = normalLayers * normalExposureMs / 60000f;

        float bottomLiftTotal = bottomLayers * (bottomLiftDistMm / (bottomLiftSpeedMmPerMin / 60f)) / 60f;
        float normalLiftTotal = normalLayers * (liftDistMm / (liftSpeedMmPerMin / 60f)) / 60f;

        float bottomRetractTotal = bottomLayers * (bottomLiftDistMm / (retractSpeed / 60f)) / 60f;
        float normalRetractTotal = normalLayers * (liftDistMm / (retractSpeed / 60f)) / 60f;

        float restTotal = totalLayers * (restAfterLiftMs + restAfterRetractMs) / 60000f;
        float lightOffTotal = totalLayers * lightOffDelayMs / 60000f;

        float total = bottomExpTotal + normalExpTotal +
                      bottomLiftTotal + normalLiftTotal +
                      bottomRetractTotal + normalRetractTotal +
                      restTotal + lightOffTotal;

        return new TimeBreakdown
        {
            TotalMinutes = total,
            BottomExposureMinutes = bottomExpTotal,
            NormalExposureMinutes = normalExpTotal,
            LiftMinutes = bottomLiftTotal + normalLiftTotal,
            RetractMinutes = bottomRetractTotal + normalRetractTotal,
            RestMinutes = restTotal,
            LightOffMinutes = lightOffTotal,
            TotalLayers = totalLayers,
            BottomLayers = bottomLayers,
        };
    }
}
