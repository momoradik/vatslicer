using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ForceEstimatorTests
{
    [Fact]
    public void Estimate_SmallOverhang_LightWeight()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 5f, overhangArea: 2f, numSupportsInRegion: 4,
            layerAreaAbove: 10f, unsupportedLength: 3f,
            PrinterOrientation.BottomUp);

        result.Weight.Should().Be(ForceEstimator.SupportWeight.Light);
        result.TotalAxialForceN.Should().BeGreaterThan(0);
        result.SafetyFactor.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Estimate_LargeOverhang_HeavyWeight()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 200f, overhangArea: 500f, numSupportsInRegion: 1,
            layerAreaAbove: 1000f, unsupportedLength: 50f,
            PrinterOrientation.BottomUp);

        result.Weight.Should().Be(ForceEstimator.SupportWeight.Heavy);
        result.MinDiameterMm.Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public void Estimate_BottomUp_HasPeelForce()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 10f, overhangArea: 20f, numSupportsInRegion: 2,
            layerAreaAbove: 50f, unsupportedLength: 5f,
            PrinterOrientation.BottomUp);

        result.PeelForceN.Should().BeGreaterThan(0, "bottom-up has peel force");
    }

    [Fact]
    public void Estimate_TopDown_NoPeelWithoutRecoater()
    {
        var result = ForceEstimator.Estimate(
            supportZ: 10f, overhangArea: 20f, numSupportsInRegion: 2,
            layerAreaAbove: 50f, unsupportedLength: 5f,
            PrinterOrientation.TopDown, recoaterSpeedMmS: 0);

        result.PeelForceN.Should().Be(0, "top-down without recoater has no peel force");
    }

    [Fact]
    public void CheckBuckling_ThinTallPillar_Fails()
    {
        // 0.3mm diameter, 100mm tall, 1N load — should fail
        ForceEstimator.CheckBuckling(0.3f, 100f, 1f).Should().BeFalse();
    }

    [Fact]
    public void CheckBuckling_ThickShortPillar_Passes()
    {
        // 2mm diameter, 10mm tall, 0.1N load — should pass easily
        ForceEstimator.CheckBuckling(2f, 10f, 0.1f).Should().BeTrue();
    }
}
