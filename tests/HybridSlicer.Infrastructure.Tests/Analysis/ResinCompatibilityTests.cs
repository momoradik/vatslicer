using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ResinCompatibilityTests
{
    [Fact]
    public void MatchingSettings_Compatible()
    {
        var r = ResinCompatibilityChecker.Check(405, 405, 2000, 2000, 30000, 30000, 120);
        r.Compatible.Should().BeTrue();
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void WavelengthMismatch_Error()
    {
        var r = ResinCompatibilityChecker.Check(365, 405, 2000, 2000, 30000, 30000, 120);
        r.Compatible.Should().BeFalse();
        r.Errors.Should().ContainMatch("*Wavelength*");
    }

    [Fact]
    public void LowExposure_Error()
    {
        var r = ResinCompatibilityChecker.Check(405, 405, 500, 2000, 30000, 30000, 120);
        r.Compatible.Should().BeFalse();
        r.Errors.Should().ContainMatch("*too low*");
    }

    [Fact]
    public void HighLiftSpeed_Warning()
    {
        var r = ResinCompatibilityChecker.Check(405, 405, 2000, 2000, 30000, 30000, 300);
        r.Warnings.Should().ContainMatch("*Lift speed*");
    }
}
