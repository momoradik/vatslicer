using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class BedAdhesionEstimatorTests
{
    [Fact]
    public void LargeBases_Safe()
    {
        var result = BedAdhesionEstimator.Estimate(
            new[] { 3f, 3f, 3f, 3f, 3f },
            maxLayerAreaMm2: 100f);
        result.Status.Should().Be("safe");
        result.SafetyMargin.Should().BeGreaterThan(2f);
    }

    [Fact]
    public void TinyBases_Critical()
    {
        var result = BedAdhesionEstimator.Estimate(
            new[] { 0.3f },
            maxLayerAreaMm2: 500f);
        result.Status.Should().Be("critical");
        result.SafetyMargin.Should().BeLessThan(1f);
    }

    [Fact]
    public void MoreBases_HigherMargin()
    {
        var few = BedAdhesionEstimator.Estimate(new[] { 2f, 2f }, maxLayerAreaMm2: 200f);
        var many = BedAdhesionEstimator.Estimate(new[] { 2f, 2f, 2f, 2f, 2f }, maxLayerAreaMm2: 200f);
        many.SafetyMargin.Should().BeGreaterThan(few.SafetyMargin);
    }

    [Fact]
    public void Description_NotEmpty()
    {
        var result = BedAdhesionEstimator.Estimate(new[] { 2f }, maxLayerAreaMm2: 50f);
        result.Description.Should().NotBeNullOrEmpty();
        result.TotalPeelForceN.Should().BeGreaterThan(0);
    }
}
