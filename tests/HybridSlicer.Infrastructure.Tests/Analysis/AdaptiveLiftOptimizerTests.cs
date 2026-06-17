using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class AdaptiveLiftOptimizerTests
{
    [Fact]
    public void UniformProfile_NoSlowedLayers()
    {
        var profile = new PeelForceProfiler.ForceProfile
        {
            Layers = Enumerable.Range(0, 10).Select(i => new PeelForceProfiler.LayerForce
            {
                ZMm = i * 0.05f, CrossSectionAreaMm2 = 100f, PeelForceN = 1.5f, IsHighStress = false,
            }).ToList(),
            MaxPeelForceN = 1.5f, MaxPeelForceZ = 0.25f, AvgPeelForceN = 1.5f, HighStressLayers = 0,
        };
        var result = AdaptiveLiftOptimizer.Optimize(profile);
        result.Layers.Should().HaveCount(10);
        result.SlowedLayers.Should().Be(0);
    }

    [Fact]
    public void HighStressLayer_GetsSlowerLift()
    {
        var layers = new List<PeelForceProfiler.LayerForce>();
        for (int i = 0; i < 10; i++)
            layers.Add(new PeelForceProfiler.LayerForce
            {
                ZMm = i * 0.05f, CrossSectionAreaMm2 = i == 5 ? 500f : 100f,
                PeelForceN = i == 5 ? 7.5f : 1.5f, IsHighStress = i == 5,
            });
        var profile = new PeelForceProfiler.ForceProfile
        {
            Layers = layers, MaxPeelForceN = 7.5f, MaxPeelForceZ = 0.25f,
            AvgPeelForceN = 2.1f, HighStressLayers = 1,
        };
        var result = AdaptiveLiftOptimizer.Optimize(profile);
        result.SlowedLayers.Should().BeGreaterThan(0);
        result.Layers[5].LiftSpeedMmPerMin.Should().BeLessThan(120f);
    }
}
