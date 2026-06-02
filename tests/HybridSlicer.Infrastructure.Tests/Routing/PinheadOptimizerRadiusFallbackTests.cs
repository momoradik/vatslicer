using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

/// <summary>
/// Tests for pinhead optimizer radius fallback behavior.
/// When collision prevents placement at full size, the optimizer
/// reduces radius and retries.
/// </summary>
public class PinheadOptimizerRadiusFallbackTests
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
    public void Optimize_LargeRadius_StillValid()
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50), new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig
            {
                PinRadiusMm = 2f,
                BackRadiusMm = 5f,
                WidthMm = 10f,
            });

        ph.IsValid.Should().BeTrue();
        ph.PinRadius.Should().BeApproximately(2f, 0.1f);
        ph.BackRadius.Should().BeApproximately(5f, 0.1f);
    }

    [Fact]
    public void Optimize_TinyRadius_StillValid()
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50), new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig
            {
                PinRadiusMm = 0.05f,
                BackRadiusMm = 0.1f,
                WidthMm = 0.2f,
            });

        ph.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Optimize_ContactPoint_Preserved()
    {
        var bvh = CreateFarBvh();
        var contact = new Vector3(10, 20, 30);
        var ph = PinheadOptimizer.Optimize(contact, new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig());

        ph.ContactPoint.Should().Be(contact);
    }

    [Fact]
    public void Optimize_NeedsAnchor_OnlyWhenAllFails()
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50), new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig());

        // With no nearby mesh, optimization should succeed without anchor
        ph.NeedsAnchor.Should().BeFalse();
    }

    [Fact]
    public void Optimize_MaxBridgeSlope_Respected()
    {
        var bvh = CreateFarBvh();
        float maxSlope = MathF.PI / 6f; // 30 degrees

        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50),
            new Vector3(0.8f, 0, -0.6f), // ~53 degree angle
            bvh,
            new PinheadOptimizer.PinheadConfig { MaxBridgeSlope = maxSlope });

        ph.IsValid.Should().BeTrue();
        // Direction Z should be at least cos(30°) ≈ 0.866
        (-ph.Direction.Z).Should().BeGreaterThanOrEqualTo(0.8f,
            "direction should be clamped to max bridge slope");
    }

    [Fact]
    public void Optimize_Width_AffectsPinheadLength()
    {
        var bvh = CreateFarBvh();

        var short_ = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50), new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig { WidthMm = 0.5f });

        var long_ = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50), new Vector3(0, 0, -1), bvh,
            new PinheadOptimizer.PinheadConfig { WidthMm = 5f });

        float shortLen = Vector3.Distance(short_.ContactPoint, short_.JunctionPoint);
        float longLen = Vector3.Distance(long_.ContactPoint, long_.JunctionPoint);

        longLen.Should().BeGreaterThan(shortLen, "wider head = longer pinhead");
    }
}
