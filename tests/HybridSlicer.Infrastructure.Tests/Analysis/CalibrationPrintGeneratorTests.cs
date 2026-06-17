using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class CalibrationPrintGeneratorTests
{
    [Fact]
    public void XYResolution_ProducesValidMesh()
    {
        var mesh = CalibrationPrintGenerator.GenerateXYResolutionTest();
        mesh.TriangleCount.Should().BeGreaterThan(0);
        (mesh.Max.X - mesh.Min.X).Should().BeGreaterThan(1, "should span multiple test features");
    }

    [Fact]
    public void ZAccuracy_ProducesSteppedMesh()
    {
        var mesh = CalibrationPrintGenerator.GenerateZAccuracyTest(10, 1f);
        mesh.TriangleCount.Should().BeGreaterThan(0);
        (mesh.Max.Z - mesh.Min.Z).Should().BeApproximately(10f, 0.1f, "10 steps of 1mm = 10mm tall");
    }

    [Fact]
    public void XYResolution_MoreSteps_WiderMesh()
    {
        var few = CalibrationPrintGenerator.GenerateXYResolutionTest(steps: 3);
        var many = CalibrationPrintGenerator.GenerateXYResolutionTest(steps: 8);
        (many.Max.X - many.Min.X).Should().BeGreaterThan(few.Max.X - few.Min.X);
    }
}
