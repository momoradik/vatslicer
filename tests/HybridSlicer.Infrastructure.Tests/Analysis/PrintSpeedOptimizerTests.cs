using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintSpeedOptimizerTests
{
    [Fact]
    public void SuboptimalSettings_SavesTime()
    {
        var plan = PrintSpeedOptimizer.Optimize(
            500, 5, 2000, 30000, 8, 100, 200, 3000, 300);
        plan.TimeSavedMinutes.Should().BeGreaterThan(0);
        plan.Optimizations.Should().NotBeEmpty();
    }

    [Fact]
    public void AlreadyOptimal_NoSavings()
    {
        var plan = PrintSpeedOptimizer.Optimize(
            500, 5, 2000, 30000, 4, 180, 240, 1000, 300);
        plan.TimeSavedPct.Should().BeLessThan(5);
    }

    [Fact]
    public void HotRoom_ReducesExposure()
    {
        var plan = PrintSpeedOptimizer.Optimize(
            500, 5, 2000, 30000, 5, 120, 240, 1000, 200, ambientTempC: 35f);
        plan.Optimizations.Should().Contain(o => o.Contains("Temperature") || o.Contains("optimal"));
    }
}
