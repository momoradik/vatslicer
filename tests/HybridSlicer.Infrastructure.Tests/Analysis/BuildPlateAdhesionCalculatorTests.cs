using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class BuildPlateAdhesionCalculatorTests
{
    [Fact]
    public void LargeFootprint_FewerBottomLayers()
    {
        var large = BuildPlateAdhesionCalculator.Calculate(2000, 20, 10000);
        var small = BuildPlateAdhesionCalculator.Calculate(100, 20, 10000);
        large.RecommendedBottomLayers.Should().BeLessOrEqualTo(small.RecommendedBottomLayers);
    }

    [Fact]
    public void FlexPlate_LessExposure()
    {
        var alu = BuildPlateAdhesionCalculator.Calculate(500, 30, 5000, plateType: "aluminum");
        var flex = BuildPlateAdhesionCalculator.Calculate(500, 30, 5000, plateType: "flex");
        flex.RecommendedBottomExposureMs.Should().BeLessThan(alu.RecommendedBottomExposureMs);
    }

    [Fact]
    public void SmallFootprint_LowConfidence()
    {
        var plan = BuildPlateAdhesionCalculator.Calculate(50, 40, 2000);
        plan.Confidence.Should().Contain("low");
    }

    [Fact]
    public void ReasonableDefaults()
    {
        var plan = BuildPlateAdhesionCalculator.Calculate(500, 25, 5000);
        plan.RecommendedBottomLayers.Should().BeInRange(3, 12);
        plan.RecommendedBottomExposureMs.Should().BeGreaterThan(10000);
    }
}
