using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportRemovalEstimatorTests
{
    [Fact]
    public void LightSupports_Easy()
    {
        var e = SupportRemovalEstimator.Estimate(10, 0.1f, 0.05f, 2f);
        e.Rating.Should().Be("easy");
        e.Difficulty.Should().BeLessThan(3f);
    }

    [Fact]
    public void HeavySupports_Hard()
    {
        var e = SupportRemovalEstimator.Estimate(200, 0.5f, 0.5f, 100f);
        e.Rating.Should().BeOneOf("hard", "very hard");
        e.Difficulty.Should().BeGreaterThan(5f);
    }

    [Fact]
    public void Advice_NotEmpty()
    {
        var e = SupportRemovalEstimator.Estimate(50, 0.25f, 0.2f, 20f);
        e.Advice.Should().NotBeNullOrEmpty();
        e.SupportCount.Should().Be(50);
    }
}
