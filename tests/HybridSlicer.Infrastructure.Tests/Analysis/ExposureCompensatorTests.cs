using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ExposureCompensatorTests
{
    [Fact]
    public void DefaultCompensation_ScaleGreaterThanOne()
    {
        var c = ExposureCompensator.Compute();
        c.ScaleFactor.Should().BeGreaterThan(1f, "shrinkage compensation scales up");
    }

    [Fact]
    public void XYCompensation_Negative()
    {
        var c = ExposureCompensator.Compute(xyBleedMm: 0.05f);
        c.XYCompensationMm.Should().BeLessThan(0, "XY bleed compensation insets contours");
    }

    [Fact]
    public void HigherExposure_MoreBleed()
    {
        var low = ExposureCompensator.Compute(exposureTimeMs: 1000f);
        var high = ExposureCompensator.Compute(exposureTimeMs: 4000f);
        Math.Abs(high.XYCompensationMm).Should().BeGreaterThan(Math.Abs(low.XYCompensationMm));
    }

    [Fact]
    public void Description_NotEmpty()
    {
        ExposureCompensator.Compute().Description.Should().NotBeNullOrEmpty();
    }
}
