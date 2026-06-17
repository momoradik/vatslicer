using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class AntiAliasingQualityTests
{
    [Fact]
    public void FinePixels_NoAANeeded()
    {
        var e = AntiAliasingQualityEstimator.Estimate(0.03f, 0.05f);
        e.RecommendedLevel.Should().Be(1);
    }

    [Fact]
    public void CoarsePixels_HeavyAA()
    {
        var e = AntiAliasingQualityEstimator.Estimate(0.08f, 0.05f);
        e.RecommendedLevel.Should().Be(8);
    }

    [Fact]
    public void MidRange_ModerateAA()
    {
        var e = AntiAliasingQualityEstimator.Estimate(0.05f, 0.05f);
        e.RecommendedLevel.Should().BeOneOf(2, 4);
    }

    [Fact]
    public void Recommendation_NotEmpty()
    {
        var e = AntiAliasingQualityEstimator.Estimate(0.05f, 0.05f);
        e.Recommendation.Should().NotBeNullOrEmpty();
        e.StairstepHeightMm.Should().BeGreaterThan(0);
    }
}
