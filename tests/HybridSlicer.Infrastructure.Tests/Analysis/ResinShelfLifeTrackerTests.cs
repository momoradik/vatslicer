using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ResinShelfLifeTrackerTests
{
    [Fact]
    public void FreshResin_StatusFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var e = ResinShelfLifeTracker.Estimate(now.AddDays(-10), now);
        e.Status.Should().Be("fresh");
        e.DaysRemaining.Should().BeGreaterThan(300);
    }

    [Fact]
    public void ExpiredResin_StatusExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var e = ResinShelfLifeTracker.Estimate(now.AddDays(-400), now);
        e.Status.Should().Be("expired");
        e.DaysRemaining.Should().Be(0);
    }

    [Fact]
    public void LightExposure_ShorterLife()
    {
        var now = DateTimeOffset.UtcNow;
        var dark = ResinShelfLifeTracker.Estimate(now.AddDays(-100), now, storedInDark: true);
        var light = ResinShelfLifeTracker.Estimate(now.AddDays(-100), now, storedInDark: false);
        light.DaysRemaining.Should().BeLessThan(dark.DaysRemaining);
    }

    [Fact]
    public void CeramicResin_ShorterShelfLife()
    {
        var now = DateTimeOffset.UtcNow;
        var standard = ResinShelfLifeTracker.Estimate(now.AddDays(-1), now, "standard");
        var ceramic = ResinShelfLifeTracker.Estimate(now.AddDays(-1), now, "ceramic");
        ceramic.EstimatedShelfLifeDays.Should().BeLessThan(standard.EstimatedShelfLifeDays);
    }

    [Fact]
    public void Warning_MatchesStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var e = ResinShelfLifeTracker.Estimate(now, now);
        e.Warning.Should().Contain("fresh");
    }
}
