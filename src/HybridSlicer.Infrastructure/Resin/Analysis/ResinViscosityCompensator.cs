namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Adjusts lift and rest parameters based on resin viscosity.
/// Higher viscosity resins need slower lifts and longer rest times
/// for proper layer separation and resin flow.
/// </summary>
public static class ResinViscosityCompensator
{
    public sealed record ViscosityAdjustment
    {
        public required float AdjustedLiftSpeedMmPerMin { get; init; }
        public required float AdjustedRestTimeMs { get; init; }
        public required float AdjustedLiftDistanceMm { get; init; }
        public required string Reason { get; init; }
    }

    /// <param name="viscosityCps">Resin viscosity in centipoise. Standard: 100-300. Ceramic: 500-2000.</param>
    public static ViscosityAdjustment Compensate(
        float viscosityCps,
        float baseLiftSpeedMmPerMin = 120f,
        float baseRestTimeMs = 0f,
        float baseLiftDistanceMm = 5f)
    {
        // Reference viscosity: 200 cps (standard resin)
        float viscosityRatio = viscosityCps / 200f;

        // Slower lift for viscous resins (prevents cavitation and layer distortion)
        float liftSpeed = baseLiftSpeedMmPerMin / MathF.Sqrt(Math.Max(viscosityRatio, 0.5f));
        liftSpeed = Math.Clamp(liftSpeed, 20f, baseLiftSpeedMmPerMin);

        // More rest time for viscous resins (allow resin to flow back and level)
        float restTime = baseRestTimeMs + (viscosityRatio - 1f) * 500f;
        restTime = Math.Max(restTime, baseRestTimeMs);

        // More lift for very viscous (need more clearance for flow)
        float liftDist = viscosityRatio > 2f ? baseLiftDistanceMm * 1.5f : baseLiftDistanceMm;

        string reason;
        if (viscosityCps < 150) reason = $"Low viscosity ({viscosityCps} cps) — standard parameters OK";
        else if (viscosityCps < 400) reason = $"Moderate viscosity ({viscosityCps} cps) — slightly slower lift recommended";
        else reason = $"High viscosity ({viscosityCps} cps) — slow lift + extra rest time required for proper flow";

        return new ViscosityAdjustment
        {
            AdjustedLiftSpeedMmPerMin = liftSpeed,
            AdjustedRestTimeMs = restTime,
            AdjustedLiftDistanceMm = liftDist,
            Reason = reason,
        };
    }
}
