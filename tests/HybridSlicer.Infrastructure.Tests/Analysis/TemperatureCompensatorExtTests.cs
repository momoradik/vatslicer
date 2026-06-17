using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class TemperatureCompensatorExtTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(10f)]
    [InlineData(20f)]
    [InlineData(25f)]
    [InlineData(30f)]
    [InlineData(40f)]
    public void AllTemps_ProducePositiveExposure(float tempC)
    {
        var r = TemperatureCompensator.Compensate(2000, tempC);
        r.AdjustedExposureMs.Should().BeGreaterThan(0);
        r.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExtremeHeat_VeryShortExposure()
    {
        var r = TemperatureCompensator.Compensate(2000, 55f);
        r.AdjustedExposureMs.Should().BeLessThan(500, "very hot resin cures extremely fast");
    }

    [Fact]
    public void ExtremeCold_VeryLongExposure()
    {
        var r = TemperatureCompensator.Compensate(2000, 5f);
        r.AdjustedExposureMs.Should().BeGreaterThan(5000, "very cold resin needs much more exposure");
    }
}
