using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ResinViscosityCompensatorTests
{
    [Fact]
    public void LowViscosity_StandardSpeed()
    {
        var a = ResinViscosityCompensator.Compensate(100, 120);
        a.AdjustedLiftSpeedMmPerMin.Should().BeGreaterThan(100);
    }

    [Fact]
    public void HighViscosity_SlowerLift()
    {
        var low = ResinViscosityCompensator.Compensate(100, 120);
        var high = ResinViscosityCompensator.Compensate(1000, 120);
        high.AdjustedLiftSpeedMmPerMin.Should().BeLessThan(low.AdjustedLiftSpeedMmPerMin);
    }

    [Fact]
    public void VeryViscous_MoreLiftDistance()
    {
        var a = ResinViscosityCompensator.Compensate(800, baseLiftDistanceMm: 5);
        a.AdjustedLiftDistanceMm.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Reason_MatchesViscosity()
    {
        ResinViscosityCompensator.Compensate(100).Reason.Should().Contain("Low");
        ResinViscosityCompensator.Compensate(600).Reason.Should().Contain("High");
    }
}
