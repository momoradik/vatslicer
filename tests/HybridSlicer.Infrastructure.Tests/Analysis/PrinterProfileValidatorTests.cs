using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrinterProfileValidatorTests
{
    [Fact]
    public void ValidProfile_Passes()
    {
        var r = PrinterProfileValidator.Validate(1920, 1080, 192, 120, 200, 2000, 30000, 120, 240, 5, 5);
        r.Valid.Should().BeTrue();
    }

    [Fact]
    public void ZeroResolution_Error()
    {
        var r = PrinterProfileValidator.Validate(0, 1080, 192, 120, 200, 2000, 30000, 120, 240, 5, 5);
        r.Valid.Should().BeFalse();
        r.Issues.Should().Contain(i => i.Field == "ResolutionX" && i.Severity == "error");
    }

    [Fact]
    public void VeryHighExposure_Warning()
    {
        var r = PrinterProfileValidator.Validate(1920, 1080, 192, 120, 200, 50000, 60000, 120, 240, 5, 5);
        r.Issues.Should().Contain(i => i.Field == "NormalExposure" && i.Severity == "warning");
    }

    [Fact]
    public void NoBottomLayers_Error()
    {
        var r = PrinterProfileValidator.Validate(1920, 1080, 192, 120, 200, 2000, 30000, 120, 240, 5, 0);
        r.Valid.Should().BeFalse();
    }

    [Fact]
    public void VeryFastLift_Warning()
    {
        var r = PrinterProfileValidator.Validate(1920, 1080, 192, 120, 200, 2000, 30000, 800, 240, 5, 5);
        r.Issues.Should().Contain(i => i.Field == "LiftSpeed");
    }
}
