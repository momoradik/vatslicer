using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// The 600th test — verifying the complete V2 engine works end-to-end
/// with a real STL file from the test suite.
/// </summary>
public class SupportEngineV2MilestoneTest
{
    [Fact]
    public void Milestone600_RealModel_FullPipeline_AllGreen()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = System.IO.Path.GetDirectoryName(dir)!;
        var path = System.IO.Path.Combine(dir, "test_slice", "floating_model.stl");
        if (!System.IO.File.Exists(path)) return;

        var data = System.IO.File.ReadAllBytes(path);
        var (mesh, report) = MeshValidator.ValidateAndRepair(data);

        report.IsValid.Should().BeTrue();

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
            Seed = 600,
        });

        // All core assertions
        result.ValidSupports.Should().BeGreaterThan(5);
        result.SupportMesh.FaceCount.Should().BeGreaterThan(10000);
        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0);
        result.SupportLayerCount.Should().BeGreaterOrEqualTo(0);
        result.LegacySupports.Count.Should().Be(result.Routes.Count);
        result.SliceElements.Count.Should().BeGreaterThan(0);
        result.TotalElapsedMs.Should().BeLessThan(5000);

        // STL export roundtrip
        var stl = result.SupportMesh.ToStlBinary();
        StlMesh.FromBinary(stl).TriangleCount.Should().Be(result.SupportMesh.FaceCount);
    }
}
