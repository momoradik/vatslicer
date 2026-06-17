using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ResinMixingCalculatorTests
{
    [Fact]
    public void EqualMix_AveragesProperties()
    {
        var r = ResinMixingCalculator.Calculate(50, 2000, 1.1f, 2f, 50, 3000, 1.2f, 3f);
        r.EstimatedExposureMs.Should().BeApproximately(2500, 1);
        r.EstimatedDensity.Should().BeApproximately(1.15f, 0.01f);
        r.EstimatedShrinkagePct.Should().BeApproximately(2.5f, 0.01f);
        r.TotalVolumeMl.Should().Be(100);
    }

    [Fact]
    public void LargeExposureDiff_WarnsUser()
    {
        var r = ResinMixingCalculator.Calculate(50, 1000, 1.1f, 2f, 50, 5000, 1.1f, 2f);
        r.Warning.Should().Contain("exposure difference");
    }

    [Fact]
    public void DensityDiff_WarnsShaking()
    {
        var r = ResinMixingCalculator.Calculate(50, 2000, 0.9f, 2f, 50, 2000, 1.4f, 2f);
        r.Warning.Should().Contain("density");
    }

    [Fact]
    public void SimilarResins_Compatible()
    {
        var r = ResinMixingCalculator.Calculate(80, 2000, 1.1f, 2f, 20, 2200, 1.12f, 2.2f);
        r.Warning.Should().Contain("compatible");
    }
}
