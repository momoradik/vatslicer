using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ModelWeightEstimatorTests
{
    [Fact]
    public void Estimate_CorrectVolume()
    {
        var e = ModelWeightEstimator.Estimate(1000f, 200f);
        e.TotalVolumeMm3.Should().Be(1200f);
        e.TotalVolumeMl.Should().BeApproximately(1.2f, 0.01f);
    }

    [Fact]
    public void Estimate_CorrectWeight()
    {
        var e = ModelWeightEstimator.Estimate(10000f, 0f, 1.1f);
        e.TotalWeightG.Should().BeApproximately(11f, 0.1f); // 10ml * 1.1g/ml
    }

    [Fact]
    public void Estimate_CorrectCost()
    {
        var e = ModelWeightEstimator.Estimate(5000f, 1000f, costPerMl: 0.05f);
        e.EstimatedCostUsd.Should().BeApproximately(0.3f, 0.01f); // 6ml * $0.05
    }
}
