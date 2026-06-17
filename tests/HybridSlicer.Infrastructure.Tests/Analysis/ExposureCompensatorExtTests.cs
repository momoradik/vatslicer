using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ExposureCompensatorExtTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(3f)]
    [InlineData(5f)]
    public void AllShrinkages_ScaleAboveOne(float shrinkage)
    {
        var c = ExposureCompensator.Compute(shrinkagePct: shrinkage);
        c.ScaleFactor.Should().BeGreaterOrEqualTo(1f);
    }

    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.05f)]
    [InlineData(0.1f)]
    public void AllBleedValues_NegativeCompensation(float bleed)
    {
        var c = ExposureCompensator.Compute(xyBleedMm: bleed);
        c.XYCompensationMm.Should().BeLessOrEqualTo(0);
    }
}
