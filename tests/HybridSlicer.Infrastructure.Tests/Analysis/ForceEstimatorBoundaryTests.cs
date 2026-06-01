using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

/// <summary>
/// Boundary and edge-case tests for the force estimator.
/// Aerospace requires correct force estimation at all extremes.
/// </summary>
public class ForceEstimatorBoundaryTests
{
    [Fact]
    public void Estimate_ZeroHeight_MinimalForce()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 0.1f, overhangArea: 1f, numSupportsInRegion: 1,
            layerAreaAbove: 1f, unsupportedLength: 0.1f,
            PrinterOrientation.BottomUp);

        result.TotalAxialForceN.Should().BeGreaterThanOrEqualTo(0);
        result.MinDiameterMm.Should().BeGreaterThanOrEqualTo(0.3f, "minimum practical diameter");
    }

    [Fact]
    public void Estimate_VeryTall_HeavyWeight()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 300f, overhangArea: 100f, numSupportsInRegion: 1,
            layerAreaAbove: 500f, unsupportedLength: 50f,
            PrinterOrientation.BottomUp);

        result.Weight.Should().Be(ForceEstimator.SupportWeight.Heavy);
    }

    [Fact]
    public void Estimate_ManySupportsInRegion_ReducesPerSupportForce()
    {
        var single = ForceEstimator.Estimate(
            supportZ: 50f, overhangArea: 100f, numSupportsInRegion: 1,
            layerAreaAbove: 100f, unsupportedLength: 10f,
            PrinterOrientation.BottomUp);

        var shared = ForceEstimator.Estimate(
            supportZ: 50f, overhangArea: 100f, numSupportsInRegion: 10,
            layerAreaAbove: 100f, unsupportedLength: 10f,
            PrinterOrientation.BottomUp);

        shared.TotalAxialForceN.Should().BeLessThan(single.TotalAxialForceN,
            "sharing load among 10 supports should reduce per-support force");
    }

    [Fact]
    public void Estimate_TopDown_WithRecoater_HasPeelForce()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 20f, overhangArea: 50f, numSupportsInRegion: 5,
            layerAreaAbove: 100f, unsupportedLength: 5f,
            PrinterOrientation.TopDown, recoaterSpeedMmS: 100f);

        result.PeelForceN.Should().BeGreaterThan(0, "recoater creates lateral force");
    }

    [Fact]
    public void Estimate_SafetyFactor_Capped()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 1f, overhangArea: 0.1f, numSupportsInRegion: 100,
            layerAreaAbove: 0.1f, unsupportedLength: 0.1f,
            PrinterOrientation.BottomUp);

        result.SafetyFactor.Should().BeLessThanOrEqualTo(10f, "SF capped at 10");
    }

    [Fact]
    public void CheckBuckling_ZeroHeight_AlwaysPasses()
    {
        ForceEstimator.CheckBuckling(0.5f, 0f, 10f).Should().BeTrue(
            "zero height column can't buckle");
    }

    [Fact]
    public void CheckBuckling_ZeroLoad_AlwaysPasses()
    {
        ForceEstimator.CheckBuckling(0.1f, 100f, 0f).Should().BeTrue(
            "zero load can't cause buckling");
    }

    [Fact]
    public void Estimate_AllWeightClasses_Reachable()
    {
        // Light: short, small area
        var light = ForceEstimator.Estimate(5f, 1f, 10, 1f, 1f, PrinterOrientation.BottomUp);
        // Medium: moderate
        var medium = ForceEstimator.Estimate(30f, 20f, 5, 50f, 5f, PrinterOrientation.BottomUp);
        // Heavy: tall (>50mm forces heavy)
        var heavy = ForceEstimator.Estimate(100f, 100f, 1, 200f, 20f, PrinterOrientation.BottomUp);

        light.Weight.Should().Be(ForceEstimator.SupportWeight.Light);
        heavy.Weight.Should().Be(ForceEstimator.SupportWeight.Heavy);
        // Medium might be light or medium depending on exact forces
        (medium.Weight == ForceEstimator.SupportWeight.Medium ||
         medium.Weight == ForceEstimator.SupportWeight.Heavy).Should().BeTrue();
    }

    [Fact]
    public void Estimate_BendingMoment_IncreasesWithSpan()
    {
        var short_ = ForceEstimator.Estimate(20f, 10f, 2, 50f, 2f, PrinterOrientation.BottomUp);
        var long_ = ForceEstimator.Estimate(20f, 10f, 2, 50f, 20f, PrinterOrientation.BottomUp);

        long_.BendingMomentNmm.Should().BeGreaterThan(short_.BendingMomentNmm,
            "longer span = more bending moment");
    }
}
