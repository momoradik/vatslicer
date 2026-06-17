using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintabilityScorerTests
{
    [Fact]
    public void PerfectModel_ScoreA()
    {
        var report = new ComprehensiveModelAnalyzer.AnalysisReport
        {
            TriangleCount = 1000, VolumeMm3 = 500, SurfaceAreaMm2 = 300,
            MeshValid = true, MeshWarnings = 0,
            IslandRisks = 0, HighRiskIslands = 0,
            SuctionWarnings = 0, ThinWalls = 0, MinWallMm = 2f,
            MaxPeelForceN = 1f, HighStressLayers = 0,
            OverallStatus = "good", Issues = new(), ElapsedMs = 10,
        };
        var score = PrintabilityScorer.Compute(report);
        score.Total.Should().Be(100);
        score.Grade.Should().Be("A");
        score.Deductions.Should().BeEmpty();
    }

    [Fact]
    public void InvalidMesh_LowScore()
    {
        var report = new ComprehensiveModelAnalyzer.AnalysisReport
        {
            TriangleCount = 100, VolumeMm3 = 50, SurfaceAreaMm2 = 30,
            MeshValid = false, MeshWarnings = 3,
            IslandRisks = 5, HighRiskIslands = 2,
            SuctionWarnings = 1, ThinWalls = 5, MinWallMm = 0.3f,
            MaxPeelForceN = 5f, HighStressLayers = 10,
            OverallStatus = "critical", Issues = new() { "bad mesh" }, ElapsedMs = 5,
        };
        var score = PrintabilityScorer.Compute(report);
        score.Total.Should().BeLessThan(50);
        score.Grade.Should().NotBe("A");
        score.Deductions.Should().NotBeEmpty();
    }
}
