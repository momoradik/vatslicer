using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SliceMemoryEstimatorTests
{
    [Fact]
    public void SmallJob_DoesNotExceed()
    {
        var e = SliceMemoryEstimator.Estimate(1920, 1080, 500);
        e.MayExceedMemory.Should().BeFalse();
        e.PeakMemoryMb.Should().BeLessThan(100);
    }

    [Fact]
    public void LargeResolution_MoreMemory()
    {
        var small = SliceMemoryEstimator.Estimate(1920, 1080, 500);
        var large = SliceMemoryEstimator.Estimate(7680, 4320, 500);
        large.PeakMemoryMb.Should().BeGreaterThan(small.PeakMemoryMb);
    }

    [Fact]
    public void MoreLayers_MoreDisk()
    {
        var few = SliceMemoryEstimator.Estimate(1920, 1080, 100);
        var many = SliceMemoryEstimator.Estimate(1920, 1080, 5000);
        many.TotalDiskMb.Should().BeGreaterThan(few.TotalDiskMb);
    }

    [Fact]
    public void Warning_AlwaysProvided()
    {
        var e = SliceMemoryEstimator.Estimate(1920, 1080, 1000);
        e.Warning.Should().NotBeNullOrEmpty();
    }
}
