using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class AreaSupportMergerTests
{
    [Fact]
    public void ClosePillars_Merged()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(3, 0, 0), new(20, 20, 0) };
        var radii = new List<float> { 0.5f, 0.5f, 0.5f };
        var tops = new List<float> { 20f, 18f, 15f };

        var result = AreaSupportMerger.FindMerges(bases, radii, tops, mergeRadiusMm: 5f);

        result.Merges.Should().HaveCount(1, "pillars 0 and 1 are 3mm apart, pillar 2 is far");
        result.Merges[0].TrunkRadius.Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public void FarPillars_NoMerge()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(30, 30, 0) };
        var radii = new List<float> { 0.5f, 0.5f };
        var tops = new List<float> { 20f, 20f };

        var result = AreaSupportMerger.FindMerges(bases, radii, tops, mergeRadiusMm: 5f);
        result.Merges.Should().BeEmpty();
    }

    [Fact]
    public void TrunkRadius_AreaEquivalent()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(2, 0, 0) };
        var radii = new List<float> { 0.7f, 0.7f };
        var tops = new List<float> { 20f, 20f };

        var result = AreaSupportMerger.FindMerges(bases, radii, tops, mergeRadiusMm: 5f);
        // sqrt(0.7² + 0.7²) ≈ 0.99
        result.Merges[0].TrunkRadius.Should().BeApproximately(0.99f, 0.02f);
    }

    [Fact]
    public void EqualHeight_MergeAtBisector()
    {
        // Two pillars at same height, D=4mm apart, θ=45° → m=1
        // z_M = z1 - D/(2·1) = 20 - 2 = 18
        // s_M = D/2 = 2 (midpoint)
        var bases = new List<Vector3> { new(0, 0, 0), new(4, 0, 0) };
        var radii = new List<float> { 0.5f, 0.5f };
        var tops = new List<float> { 20f, 20f };

        var result = AreaSupportMerger.FindMerges(bases, radii, tops, mergeRadiusMm: 10f, maxBranchAngleDeg: 45f);

        result.Merges.Should().HaveCount(1);
        result.Merges[0].BranchPoint.Z.Should().BeApproximately(18f, 0.5f,
            "equal height merge: z_M = z1 - D/(2·tan45°) = 20 - 4/2 = 18");
        result.Merges[0].BranchPoint.X.Should().BeApproximately(2f, 0.5f,
            "equal height merge: s_M = D/2 = midpoint at x=2");
        result.Merges[0].IsContainment.Should().BeFalse();
    }

    [Fact]
    public void Containment_NoSteinerNode()
    {
        // P1 at z=30, P2 at z=10, D=5mm, θ=45° → m=1
        // D(5) ≤ m(1)·Δh(20) = 20 → containment!
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var radii = new List<float> { 0.5f, 0.5f };
        var tops = new List<float> { 30f, 10f };

        var result = AreaSupportMerger.FindMerges(bases, radii, tops, mergeRadiusMm: 10f, maxBranchAngleDeg: 45f);

        result.Merges.Should().HaveCount(1);
        result.Merges[0].IsContainment.Should().BeTrue(
            "D=5 ≤ tan(45°)·Δh=20 → P2 inside P1's cone");
    }

    [Fact]
    public void AngleGuard_ThetaClampedBelow90()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(2, 0, 0) };
        var radii = new List<float> { 0.5f, 0.5f };
        var tops = new List<float> { 20f, 20f };

        // θ=95° should be clamped to 89° (not crash with ∞)
        var result = AreaSupportMerger.FindMerges(bases, radii, tops, mergeRadiusMm: 5f, maxBranchAngleDeg: 95f);
        result.Should().NotBeNull("angle > 90° should be clamped, not crash");
    }
}
