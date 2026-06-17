using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintTimeBreakdownTests
{
    [Fact]
    public void Breakdown_SumsToTotal()
    {
        var b = PrintTimeBreakdown.Compute(
            totalLayers: 100, bottomLayers: 5,
            normalExposureMs: 2000, bottomExposureMs: 30000,
            liftDistMm: 5, liftSpeedMmPerMin: 120,
            retractSpeedMmPerMin: 240,
            bottomLiftDistMm: 8, bottomLiftSpeedMmPerMin: 60,
            lightOffDelayMs: 1000);

        float sum = b.BottomExposureMinutes + b.NormalExposureMinutes +
                    b.LiftMinutes + b.RetractMinutes + b.RestMinutes + b.LightOffMinutes;
        sum.Should().BeApproximately(b.TotalMinutes, 0.01f);
    }

    [Fact]
    public void BottomExposure_DominatesForFewLayers()
    {
        var b = PrintTimeBreakdown.Compute(
            totalLayers: 10, bottomLayers: 8,
            normalExposureMs: 2000, bottomExposureMs: 60000,
            liftDistMm: 5, liftSpeedMmPerMin: 120,
            retractSpeedMmPerMin: 240,
            bottomLiftDistMm: 8, bottomLiftSpeedMmPerMin: 60,
            lightOffDelayMs: 1000);

        b.BottomExposureMinutes.Should().BeGreaterThan(b.NormalExposureMinutes,
            "8/10 bottom layers with 60s exposure should dominate");
    }

    [Fact]
    public void TotalMinutes_Positive()
    {
        var b = PrintTimeBreakdown.Compute(100, 5, 2000, 30000, 5, 120, 240, 8, 60, 1000);
        b.TotalMinutes.Should().BeGreaterThan(0);
        b.TotalLayers.Should().Be(100);
    }
}
