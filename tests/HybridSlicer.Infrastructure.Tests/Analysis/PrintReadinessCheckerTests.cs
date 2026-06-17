using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintReadinessCheckerTests
{
    [Fact]
    public void AllPassed_Ready()
    {
        var r = PrintReadinessChecker.Check(true, true, true, true, true);
        r.Ready.Should().BeTrue();
        r.PassedCount.Should().Be(r.TotalCount);
    }

    [Fact]
    public void MissingModel_NotReady()
    {
        var r = PrintReadinessChecker.Check(false, true, true, true, true);
        r.Ready.Should().BeFalse();
    }

    [Fact]
    public void NotSliced_NotReady()
    {
        var r = PrintReadinessChecker.Check(true, true, true, false, true);
        r.Ready.Should().BeFalse();
    }

    [Fact]
    public void WithOptionalChecks_AllIncluded()
    {
        var r = PrintReadinessChecker.Check(true, true, true, true, true,
            printerConnected: true, printabilityScore: 85, enoughResin: true);
        r.Ready.Should().BeTrue();
        r.TotalCount.Should().BeGreaterThan(5);
    }

    [Fact]
    public void LowScore_FailsCheck()
    {
        var r = PrintReadinessChecker.Check(true, true, true, true, true,
            printabilityScore: 30);
        r.Ready.Should().BeFalse();
    }
}
