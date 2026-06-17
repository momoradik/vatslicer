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
        result.Merges[0].trunkRadius.Should().BeGreaterThan(0.5f, "merged trunk is thicker");
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
        result.Merges[0].trunkRadius.Should().BeApproximately(0.99f, 0.02f);
    }
}
