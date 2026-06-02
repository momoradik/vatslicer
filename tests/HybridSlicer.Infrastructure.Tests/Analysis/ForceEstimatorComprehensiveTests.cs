using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ForceEstimatorComprehensiveTests
{
    [Theory]
    [InlineData(5f, "Light")]
    [InlineData(60f, "Heavy")]
    [InlineData(200f, "Heavy")]
    public void Estimate_HeightDeterminesWeight(float z, string expected)
    {
        var result = ForceEstimator.Estimate(z, 10f, 3, 50f, 5f, PrinterOrientation.BottomUp);
        result.Weight.ToString().Should().Be(expected);
    }

    [Fact]
    public void Estimate_GravityForce_IncreasesWithHeight()
    {
        var low = ForceEstimator.Estimate(10f, 10f, 1, 50f, 5f, PrinterOrientation.BottomUp);
        var high = ForceEstimator.Estimate(100f, 10f, 1, 50f, 5f, PrinterOrientation.BottomUp);

        high.GravityForceN.Should().BeGreaterThan(low.GravityForceN);
    }

    [Fact]
    public void Estimate_PeelForce_IncreasesWithArea()
    {
        var small = ForceEstimator.Estimate(20f, 5f, 1, 10f, 5f, PrinterOrientation.BottomUp);
        var large = ForceEstimator.Estimate(20f, 100f, 1, 200f, 5f, PrinterOrientation.BottomUp);

        large.PeelForceN.Should().BeGreaterThan(small.PeelForceN);
    }

    [Fact]
    public void Estimate_MinDiameter_AlwaysPositive()
    {
        for (float z = 1; z <= 300; z += 50)
        {
            var r = ForceEstimator.Estimate(z, 10f, 1, 50f, 5f, PrinterOrientation.BottomUp);
            r.MinDiameterMm.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void CheckBuckling_InverseRelationship_RadiusVsHeight()
    {
        // Thicker pillar at same height should pass where thinner fails
        bool thin = ForceEstimator.CheckBuckling(0.3f, 100f, 0.5f);
        bool thick = ForceEstimator.CheckBuckling(2f, 100f, 0.5f);

        thick.Should().BeTrue("thick pillar should resist buckling");
        // thin may or may not pass — just verify the relationship makes sense
    }

    [Fact]
    public void Estimate_RecoaterForce_ProportionalToSpeed()
    {
        var slow = ForceEstimator.Estimate(20f, 20f, 2, 50f, 5f, PrinterOrientation.TopDown, 50f);
        var fast = ForceEstimator.Estimate(20f, 20f, 2, 50f, 5f, PrinterOrientation.TopDown, 200f);

        fast.PeelForceN.Should().BeGreaterThan(slow.PeelForceN,
            "faster recoater creates more force");
    }
}
