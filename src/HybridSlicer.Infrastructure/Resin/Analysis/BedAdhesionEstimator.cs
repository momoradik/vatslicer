namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates bed adhesion safety margin for support bases.
/// Compares the total peel force against the bed adhesion capacity of all
/// support bases combined. Warning if margin < 2x, critical if < 1x.
/// </summary>
public static class BedAdhesionEstimator
{
    public sealed record AdhesionResult
    {
        public required float TotalPeelForceN { get; init; }
        public required float TotalAdhesionCapacityN { get; init; }
        public required float SafetyMargin { get; init; }
        public required string Status { get; init; } // "safe", "warning", "critical"
        public required string Description { get; init; }
    }

    /// <summary>
    /// Estimate bed adhesion safety for a set of support bases.
    /// </summary>
    /// <param name="baseRadiiMm">Radii of all support bases (mm).</param>
    /// <param name="maxLayerAreaMm2">Maximum cross-section area of the model (mm2).</param>
    /// <param name="pAdhNPerMm2">FEP adhesion pressure (N/mm2).</param>
    /// <param name="bedAdhStrengthMPa">Bed adhesion strength (MPa). Typical: 0.5-2.0.</param>
    public static AdhesionResult Estimate(
        IReadOnlyList<float> baseRadiiMm,
        float maxLayerAreaMm2,
        float pAdhNPerMm2 = SupportSizer.P_ADH_DEFAULT,
        float bedAdhStrengthMPa = 1.0f)
    {
        // Total peel force at worst-case layer
        float totalPeelForce = pAdhNPerMm2 * maxLayerAreaMm2;

        // Total base adhesion capacity: sum of (pi * r^2 * bedAdhStrength)
        float totalBaseArea = 0;
        foreach (float r in baseRadiiMm)
            totalBaseArea += MathF.PI * r * r;
        float totalCapacity = totalBaseArea * bedAdhStrengthMPa;

        float margin = totalCapacity > 0 ? totalCapacity / totalPeelForce : 0;

        string status = margin >= 2f ? "safe" : margin >= 1f ? "warning" : "critical";
        string desc = status switch
        {
            "safe" => $"Bed adhesion OK: {margin:F1}x safety margin ({baseRadiiMm.Count} bases, {totalBaseArea:F0}mm2 total)",
            "warning" => $"Bed adhesion marginal: {margin:F1}x margin — consider adding supports or increasing base size",
            "critical" => $"Bed adhesion insufficient: {margin:F1}x margin — part will likely detach during peel",
            _ => "",
        };

        return new AdhesionResult
        {
            TotalPeelForceN = totalPeelForce,
            TotalAdhesionCapacityN = totalCapacity,
            SafetyMargin = margin,
            Status = status,
            Description = desc,
        };
    }
}
