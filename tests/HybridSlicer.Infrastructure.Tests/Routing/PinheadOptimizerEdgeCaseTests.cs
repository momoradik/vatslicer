using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class PinheadOptimizerEdgeCaseTests
{
    private static AabbBvh CreateFarBvh()
    {
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12;
        for (int v = 0; v < 3; v++) {
            BitConverter.GetBytes(1000f + v).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void Optimize_VerticalNormal_ProducesVerticalDirection()
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 10), new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig());

        ph.IsValid.Should().BeTrue();
        ph.Direction.Z.Should().BeApproximately(-1f, 0.05f);
    }

    [Fact]
    public void Optimize_HorizontalNormal_ClampedTo45Degrees()
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 10), new Vector3(1, 0, 0), // horizontal
            bvh, new PinheadOptimizer.PinheadConfig());

        ph.IsValid.Should().BeTrue();
        // Should be clamped — Z component >= cos(45°) ≈ 0.707
        (-ph.Direction.Z).Should().BeGreaterThanOrEqualTo(0.65f);
    }
}
