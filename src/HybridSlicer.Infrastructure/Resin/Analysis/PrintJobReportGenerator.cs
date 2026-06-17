namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates a complete print job report combining ALL analysis engines.
/// Single entry point for the /api/support-v2/full-report endpoint.
/// </summary>
public static class PrintJobReportGenerator
{
    public sealed record FullReport
    {
        public required ComprehensiveModelAnalyzer.AnalysisReport Analysis { get; init; }
        public required PrintabilityScorer.Score Score { get; init; }
        public required FailureRiskAssessor.Assessment Risk { get; init; }
        public required OverhangAreaCalculator.OverhangBreakdown Overhangs { get; init; }
        public required SurfaceFinishPredictor.FinishReport Finish { get; init; }
        public required CenterOfGravityCalculator.CogResult CenterOfGravity { get; init; }
        public required PostProcessingAdvisor.PostProcessPlan PostProcess { get; init; }
        public required long TotalElapsedMs { get; init; }
    }

    public static FullReport Generate(StlMesh mesh, string resinType = "standard", int supportCount = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var analysis = ComprehensiveModelAnalyzer.Analyze(mesh);
        var score = PrintabilityScorer.Compute(analysis);
        var risk = FailureRiskAssessor.Assess(analysis, supportCount);
        var overhangs = OverhangAreaCalculator.Calculate(mesh);
        var finish = SurfaceFinishPredictor.Predict(mesh);
        var cog = CenterOfGravityCalculator.Compute(mesh);
        var postProcess = PostProcessingAdvisor.Generate(resinType, supportCount > 0);

        return new FullReport
        {
            Analysis = analysis,
            Score = score,
            Risk = risk,
            Overhangs = overhangs,
            Finish = finish,
            CenterOfGravity = cog,
            PostProcess = postProcess,
            TotalElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
