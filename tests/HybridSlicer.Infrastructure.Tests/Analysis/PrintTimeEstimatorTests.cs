using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintTimeEstimatorTests
{
    [Fact]
    public void ShortPrint_FormattedAsMinutes()
    {
        var e = PrintTimeEstimator.Calculate(50, 5, 2000, 30000, 5, 120, 240, 8, 60, 1000);
        e.Formatted.Should().Contain("min");
        e.TotalMinutes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void LongPrint_FormattedAsHours()
    {
        var e = PrintTimeEstimator.Calculate(2000, 10, 3000, 60000, 6, 100, 200, 10, 50, 2000);
        e.TotalMinutes.Should().BeGreaterThan(60);
        e.Formatted.Should().Contain("h");
    }

    [Fact]
    public void IncludesWarmUp()
    {
        var e = PrintTimeEstimator.Calculate(100, 5, 2000, 30000, 5, 120, 240, 8, 60, 1000, warmUpMinutes: 2f);
        e.WarmUpMinutes.Should().Be(2f);
        e.TotalMinutes.Should().BeGreaterThan(e.PrintingMinutes);
    }
}
