namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Assesses print failure risk by combining multiple risk factors:
/// - Bed detachment (insufficient base adhesion)
/// - Layer delamination (high peel force at specific layers)
/// - Suction cup failure (vacuum during peel)
/// - Support failure (undersized or missing supports)
/// - FEP wear (high cross-section area / peel force)
///
/// Returns a ranked list of risk factors with mitigation suggestions.
/// </summary>
public static class FailureRiskAssessor
{
    public sealed record RiskFactor
    {
        public required string Name { get; init; }
        public required float Probability { get; init; } // 0-1
        public required string Severity { get; init; } // "low", "medium", "high", "critical"
        public required string Mitigation { get; init; }
    }

    public sealed record Assessment
    {
        public required float OverallRiskPct { get; init; }
        public required List<RiskFactor> Factors { get; init; }
        public required string Verdict { get; init; } // "ready", "review", "risky", "do not print"
    }

    public static Assessment Assess(ComprehensiveModelAnalyzer.AnalysisReport report, int supportCount = 0)
    {
        var factors = new List<RiskFactor>();

        // Bed detachment risk
        if (!report.MeshValid)
            factors.Add(new RiskFactor { Name = "Invalid Mesh", Probability = 0.8f, Severity = "critical",
                Mitigation = "Repair mesh before printing (fix non-manifold edges, degenerate triangles)" });

        // Island/floating risk
        if (report.HighRiskIslands > 0)
            factors.Add(new RiskFactor { Name = "Floating Islands", Probability = 0.7f, Severity = "high",
                Mitigation = $"Add supports under {report.HighRiskIslands} floating regions" });

        // Suction risk
        if (report.SuctionWarnings > 0)
            factors.Add(new RiskFactor { Name = "Suction Cups", Probability = 0.5f, Severity = "high",
                Mitigation = "Add drain holes to trapped volumes to relieve vacuum pressure" });

        // Thin wall risk
        if (report.ThinWalls > 0)
            factors.Add(new RiskFactor { Name = "Thin Walls", Probability = 0.3f, Severity = "medium",
                Mitigation = $"{report.ThinWalls} walls below 0.5mm may break. Thicken or orient differently" });

        // High peel force
        if (report.HighStressLayers > 10)
            factors.Add(new RiskFactor { Name = "High Peel Force", Probability = 0.4f, Severity = "medium",
                Mitigation = "Reduce lift speed on high-stress layers or rotate model to reduce cross-section" });

        // No supports
        if (supportCount == 0 && report.HighRiskIslands > 0)
            factors.Add(new RiskFactor { Name = "No Supports", Probability = 0.9f, Severity = "critical",
                Mitigation = "Generate supports — model has unsupported overhangs" });

        float maxProb = factors.Count > 0 ? factors.Max(f => f.Probability) : 0;
        float riskPct = maxProb * 100;
        string verdict = riskPct < 10 ? "ready" : riskPct < 30 ? "review" : riskPct < 60 ? "risky" : "do not print";

        return new Assessment
        {
            OverallRiskPct = riskPct,
            Factors = factors.OrderByDescending(f => f.Probability).ToList(),
            Verdict = verdict,
        };
    }
}
