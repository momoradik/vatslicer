namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Adjusts exposure time based on ambient temperature.
/// Cold resin cures slower → needs more exposure.
/// Warm resin cures faster → needs less exposure.
/// Based on Arrhenius rate approximation: rate doubles per ~10°C.
/// </summary>
public static class TemperatureCompensator
{
    public sealed record TempAdjustment
    {
        public required float AdjustedExposureMs { get; init; }
        public required float MultiplierPct { get; init; }
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Compute temperature-compensated exposure time.
    /// </summary>
    /// <param name="baseExposureMs">Nominal exposure at reference temp.</param>
    /// <param name="ambientTempC">Current ambient temperature (°C).</param>
    /// <param name="referenceTempC">Reference temperature (°C). Typically 25°C.</param>
    public static TempAdjustment Compensate(float baseExposureMs, float ambientTempC, float referenceTempC = 25f)
    {
        float diff = ambientTempC - referenceTempC;
        // Arrhenius approximation: reaction rate doubles every ~10°C
        float rateFactor = MathF.Pow(2f, diff / 10f);
        float adjustedMs = baseExposureMs / rateFactor;
        float multiplierPct = (adjustedMs / baseExposureMs - 1f) * 100f;

        string reason;
        if (Math.Abs(diff) < 3) reason = $"No adjustment needed ({ambientTempC:F0}°C ≈ reference {referenceTempC:F0}°C)";
        else if (diff < 0) reason = $"Cold ({ambientTempC:F0}°C < {referenceTempC:F0}°C) → +{Math.Abs(multiplierPct):F0}% exposure";
        else reason = $"Warm ({ambientTempC:F0}°C > {referenceTempC:F0}°C) → {multiplierPct:F0}% exposure (faster cure)";

        return new TempAdjustment
        {
            AdjustedExposureMs = adjustedMs,
            MultiplierPct = multiplierPct,
            Reason = reason,
        };
    }
}
