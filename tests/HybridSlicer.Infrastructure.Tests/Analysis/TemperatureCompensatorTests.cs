using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class TemperatureCompensatorTests
{
    [Fact]
    public void ReferenceTemp_NoChange()
    {
        var r = TemperatureCompensator.Compensate(2000, 25f, 25f);
        r.AdjustedExposureMs.Should().BeApproximately(2000, 1);
    }

    [Fact]
    public void Cold_IncreasesExposure()
    {
        var r = TemperatureCompensator.Compensate(2000, 15f, 25f);
        r.AdjustedExposureMs.Should().BeGreaterThan(2000, "cold resin needs more exposure");
    }

    [Fact]
    public void Warm_DecreasesExposure()
    {
        var r = TemperatureCompensator.Compensate(2000, 35f, 25f);
        r.AdjustedExposureMs.Should().BeLessThan(2000, "warm resin cures faster");
    }
}
