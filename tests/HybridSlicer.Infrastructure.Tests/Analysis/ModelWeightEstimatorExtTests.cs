using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ModelWeightEstimatorExtTests
{
    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.0f)]
    [InlineData(1.1f)]
    [InlineData(1.3f)]
    [InlineData(1.5f)]
    public void AllDensities_ProducePositiveWeight(float density)
    {
        var e = ModelWeightEstimator.Estimate(1000f, 0f, density);
        e.TotalWeightG.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(0.03f)]
    [InlineData(0.05f)]
    [InlineData(0.08f)]
    [InlineData(0.12f)]
    public void AllCosts_ProducePositiveCost(float costPerMl)
    {
        var e = ModelWeightEstimator.Estimate(5000f, 1000f, costPerMl: costPerMl);
        e.EstimatedCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SupportVolume_AddedToTotal()
    {
        var e = ModelWeightEstimator.Estimate(1000f, 500f);
        e.TotalVolumeMm3.Should().Be(1500f);
        e.ModelVolumeMm3.Should().Be(1000f);
        e.SupportVolumeMm3.Should().Be(500f);
    }
}
