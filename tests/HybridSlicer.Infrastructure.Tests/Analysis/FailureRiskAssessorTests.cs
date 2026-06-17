using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class FailureRiskAssessorTests
{
    [Fact]
    public void CleanModel_Ready()
    {
        var report = new ComprehensiveModelAnalyzer.AnalysisReport
        {
            TriangleCount = 1000, VolumeMm3 = 500, SurfaceAreaMm2 = 300,
            MeshValid = true, MeshWarnings = 0, IslandRisks = 0, HighRiskIslands = 0,
            SuctionWarnings = 0, ThinWalls = 0, MinWallMm = 2f,
            MaxPeelForceN = 1f, HighStressLayers = 0,
            OverallStatus = "good", Issues = new(), ElapsedMs = 10,
        };
        var a = FailureRiskAssessor.Assess(report, supportCount: 20);
        a.Verdict.Should().Be("ready");
        a.Factors.Should().BeEmpty();
    }

    [Fact]
    public void InvalidMesh_DoNotPrint()
    {
        var report = new ComprehensiveModelAnalyzer.AnalysisReport
        {
            TriangleCount = 100, VolumeMm3 = 50, SurfaceAreaMm2 = 30,
            MeshValid = false, MeshWarnings = 5, IslandRisks = 3, HighRiskIslands = 2,
            SuctionWarnings = 1, ThinWalls = 5, MinWallMm = 0.3f,
            MaxPeelForceN = 5f, HighStressLayers = 15,
            OverallStatus = "critical", Issues = new() { "bad" }, ElapsedMs = 5,
        };
        var a = FailureRiskAssessor.Assess(report, supportCount: 0);
        a.Verdict.Should().Be("do not print");
        a.Factors.Should().NotBeEmpty();
    }

    [Fact]
    public void Factors_HaveMitigations()
    {
        var report = new ComprehensiveModelAnalyzer.AnalysisReport
        {
            TriangleCount = 500, VolumeMm3 = 200, SurfaceAreaMm2 = 150,
            MeshValid = true, MeshWarnings = 0, IslandRisks = 2, HighRiskIslands = 1,
            SuctionWarnings = 1, ThinWalls = 3, MinWallMm = 0.4f,
            MaxPeelForceN = 3f, HighStressLayers = 5,
            OverallStatus = "warnings", Issues = new(), ElapsedMs = 8,
        };
        var a = FailureRiskAssessor.Assess(report);
        foreach (var f in a.Factors)
            f.Mitigation.Should().NotBeNullOrEmpty();
    }
}
