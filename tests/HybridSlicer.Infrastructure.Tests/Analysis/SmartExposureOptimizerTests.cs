using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SmartExposureOptimizerTests
{
    private static CrossSectionAreaCalculator.AreaProfile MakeProfile(int layers, float area)
    {
        var areas = new float[layers];
        Array.Fill(areas, area);
        return new CrossSectionAreaCalculator.AreaProfile
        {
            AreasPerLayer = areas, LayerHeightMm = 0.05f, MeshMinZ = 0,
            LayerCount = layers, MaxAreaMm2 = area, TotalVolumeMm3 = area * layers * 0.05f,
        };
    }

    [Fact]
    public void UniformProfile_ProducesConsistentExposure()
    {
        var profile = MakeProfile(100, 200f);
        var result = SmartExposureOptimizer.Optimize(profile, 2000, 30000, 5);
        result.Layers.Should().HaveCount(100);
        result.AvgExposureMs.Should().BeGreaterThan(0);
        result.MinExposureMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BottomLayers_HaveHigherExposure()
    {
        var profile = MakeProfile(50, 100f);
        var result = SmartExposureOptimizer.Optimize(profile, 2000, 30000, 5);
        var bottomAvg = result.Layers.Take(5).Average(l => l.ExposureMs);
        var normalAvg = result.Layers.Skip(5).Average(l => l.ExposureMs);
        bottomAvg.Should().BeGreaterThan(normalAvg * 5, "bottom layers should have much higher exposure");
    }

    [Fact]
    public void ColdRoom_IncreasesExposure()
    {
        var profile = MakeProfile(50, 100f);
        var normal = SmartExposureOptimizer.Optimize(profile, 2000, 30000, 5, 25f);
        var cold = SmartExposureOptimizer.Optimize(profile, 2000, 30000, 5, 15f);
        cold.AvgExposureMs.Should().BeGreaterThan(normal.AvgExposureMs);
    }
}
