using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class UVPowerDensityTests
{
    [Fact]
    public void StandardExposure_PositiveDose()
    {
        var r = UVPowerDensityCalculator.Calculate(2000f, 50000f, 230f);
        r.EnergyDoseMjPerCm2.Should().BeGreaterThan(0);
        r.PowerDensityMwPerCm2.Should().BeGreaterThan(0);
        r.DoseCategory.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void LongerExposure_HigherDose()
    {
        var short_ = UVPowerDensityCalculator.Calculate(500f, 50000f, 230f);
        var long_ = UVPowerDensityCalculator.Calculate(5000f, 50000f, 230f);
        long_.EnergyDoseMjPerCm2.Should().BeGreaterThan(short_.EnergyDoseMjPerCm2);
    }

    [Fact]
    public void RecommendedExposure_Positive()
    {
        var r = UVPowerDensityCalculator.Calculate(2000f);
        r.RecommendedExposureMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HigherPower_LessExposureNeeded()
    {
        var lowPower = UVPowerDensityCalculator.Calculate(2000f, 30000f, 230f);
        var highPower = UVPowerDensityCalculator.Calculate(2000f, 80000f, 230f);
        highPower.RecommendedExposureMs.Should().BeLessThan(lowPower.RecommendedExposureMs);
    }
}
