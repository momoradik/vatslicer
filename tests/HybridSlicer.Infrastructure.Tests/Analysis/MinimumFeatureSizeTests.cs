using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class MinimumFeatureSizeTests
{
    [Fact]
    public void LargeFeatures_AllResolvable()
    {
        var r = MinimumFeatureSizeChecker.Check(2f, 1.5f, 0.05f);
        r.AllFeaturesResolvable.Should().BeTrue();
        r.SubResolutionFeatures.Should().Be(0);
    }

    [Fact]
    public void TinyFeatures_NotResolvable()
    {
        var r = MinimumFeatureSizeChecker.Check(0.05f, 0.03f, 0.05f);
        r.AllFeaturesResolvable.Should().BeFalse();
        r.SubResolutionFeatures.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FinerPixelPitch_BetterResolution()
    {
        var coarse = MinimumFeatureSizeChecker.Check(0.2f, 0.2f, 0.08f);
        var fine = MinimumFeatureSizeChecker.Check(0.2f, 0.2f, 0.03f);
        fine.PrinterMinFeatureMm.Should().BeLessThan(coarse.PrinterMinFeatureMm);
    }
}
