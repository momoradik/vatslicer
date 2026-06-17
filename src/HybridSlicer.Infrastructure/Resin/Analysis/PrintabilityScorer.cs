namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes a 0-100 printability score from the comprehensive analysis report.
/// Higher = more likely to print successfully without intervention.
/// Deductions for: mesh errors, islands, suction, thin walls, high peel force.
/// </summary>
public static class PrintabilityScorer
{
    public sealed record Score
    {
        public required int Total { get; init; }
        public required string Grade { get; init; } // A/B/C/D/F
        public required List<(string category, int deduction, string reason)> Deductions { get; init; }
    }

    public static Score Compute(ComprehensiveModelAnalyzer.AnalysisReport report)
    {
        int score = 100;
        var deductions = new List<(string category, int deduction, string reason)>();

        void Deduct(string cat, int pts, string reason)
        {
            deductions.Add((cat, pts, reason));
            score -= pts;
        }

        // Mesh quality
        if (!report.MeshValid) Deduct("Mesh", 30, "Invalid mesh — repair required");
        else if (report.MeshWarnings > 0) Deduct("Mesh", 5 * Math.Min(report.MeshWarnings, 3), $"{report.MeshWarnings} mesh warnings");

        // Islands
        if (report.HighRiskIslands > 3) Deduct("Islands", 25, $"{report.HighRiskIslands} high-risk floating regions");
        else if (report.HighRiskIslands > 0) Deduct("Islands", 10 * report.HighRiskIslands, $"{report.HighRiskIslands} high-risk island(s)");
        else if (report.IslandRisks > 5) Deduct("Islands", 5, $"{report.IslandRisks} potential island zones");

        // Suction
        if (report.SuctionWarnings > 2) Deduct("Suction", 20, $"{report.SuctionWarnings} suction cup risks — add drain holes");
        else if (report.SuctionWarnings > 0) Deduct("Suction", 10, $"{report.SuctionWarnings} suction pocket(s)");

        // Thin walls
        if (report.ThinWalls > 10) Deduct("ThinWalls", 15, $"{report.ThinWalls} thin wall regions");
        else if (report.ThinWalls > 0) Deduct("ThinWalls", 5, $"{report.ThinWalls} thin wall(s) detected");

        // Peel force
        if (report.HighStressLayers > 20) Deduct("PeelForce", 10, $"{report.HighStressLayers} high-stress layers — slow lift recommended");
        else if (report.HighStressLayers > 5) Deduct("PeelForce", 5, $"{report.HighStressLayers} high-stress layer(s)");

        score = Math.Max(0, score);
        string grade = score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 40 ? "D" : "F";

        return new Score { Total = score, Grade = grade, Deductions = deductions };
    }
}
