using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

/// <summary>
/// Overhang analysis tests using real STL files.
/// </summary>
public class OverhangAnalyzerRealModelTests
{
    private static StlMesh? TryLoadMesh(string filename)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", filename);
        if (!File.Exists(path)) return null;
        var (mesh, _) = MeshValidator.ValidateAndRepair(File.ReadAllBytes(path));
        return mesh;
    }

    [Fact]
    public void FloatingModel_DetectsOverhangs()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = OverhangAnalyzer.Analyze(mesh, 2f);

        result.TotalOverhangRegions.Should().BeGreaterThan(0, "floating model has overhangs");
        result.Layers.Should().NotBeEmpty();
    }

    [Fact]
    public void FloatingModel_AnalysisCompletesQuickly()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = OverhangAnalyzer.Analyze(mesh, 3f);

        result.ElapsedMs.Should().BeLessThan(3000, "analysis should be fast");
    }

    [Fact]
    public void FloatingModel_OverhangAreaIsPositive()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = OverhangAnalyzer.Analyze(mesh, 2f);

        result.TotalOverhangArea.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FloatingModel_AllRegionsHaveContours()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = OverhangAnalyzer.Analyze(mesh, 2f);

        foreach (var layer in result.Layers)
        foreach (var region in layer.Regions)
        {
            region.Contour.Should().NotBeEmpty();
            region.Area.Should().BeGreaterThan(0);
        }
    }
}
