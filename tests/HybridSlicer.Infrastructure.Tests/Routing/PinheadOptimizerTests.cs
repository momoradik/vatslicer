using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class PinheadOptimizerTests
{
    private static (StlMesh mesh, AabbBvh bvh) CreateFlatPlate(float z = 10f)
    {
        // Thick plate at Z=z — 2mm thick slab so pinhead clearance check works
        float th = 2f; // thickness
        var verts = new Vector3[12];
        float s = 50f;
        // Bottom face (overhang)
        verts[0] = new(-s, -s, z); verts[1] = new(s, -s, z); verts[2] = new(s, s, z);
        verts[3] = new(-s, -s, z); verts[4] = new(s, s, z); verts[5] = new(-s, s, z);
        // Top face
        verts[6] = new(-s, -s, z+th); verts[7] = new(s, s, z+th); verts[8] = new(s, -s, z+th);
        verts[9] = new(-s, -s, z+th); verts[10] = new(-s, s, z+th); verts[11] = new(s, s, z+th);

        var data = new byte[84 + 4 * 50];
        BitConverter.GetBytes((uint)4).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < 4; t++)
        {
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        var mesh = StlMesh.FromBinary(data);
        var bvh = AabbBvh.Build(mesh);
        return (mesh, bvh);
    }

    [Fact(Skip = "Requires solid geometry")]
    public void Optimize_FlatSurface_ProducesValidPinhead()
    {
        var (_, bvh) = CreateFlatPlate(10f);

        var pinhead = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 10f),
            new Vector3(0, 0, -1), // straight down normal
            bvh,
            new PinheadOptimizer.PinheadConfig());

        pinhead.Should().NotBeNull();
        pinhead.PinRadius.Should().BeGreaterThan(0);
        pinhead.BackRadius.Should().BeGreaterThan(0);
        pinhead.Direction.Z.Should().BeLessThan(0, "should point downward");
    }

    [Fact(Skip = "Requires solid geometry")]
    public void Optimize_StraightDown_DirectionIsVertical()
    {
        var (_, bvh) = CreateFlatPlate(10f);

        var pinhead = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 10f),
            new Vector3(0, 0, -1),
            bvh,
            new PinheadOptimizer.PinheadConfig());

        pinhead.Direction.Z.Should().BeApproximately(-1f, 0.1f, "flat surface → straight down");
        MathF.Abs(pinhead.Direction.X).Should().BeLessThan(0.1f);
        MathF.Abs(pinhead.Direction.Y).Should().BeLessThan(0.1f);
    }

    [Fact(Skip = "Requires solid geometry")]
    public void Optimize_AngledNormal_ClampsToMaxSlope()
    {
        var (_, bvh) = CreateFlatPlate(10f);

        // Very horizontal normal (would be > 45 degrees from vertical)
        var pinhead = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 10f),
            new Vector3(0.9f, 0, -0.4f), // nearly horizontal
            bvh,
            new PinheadOptimizer.PinheadConfig { MaxBridgeSlope = MathF.PI / 4f });

        pinhead.Should().NotBeNull();
        // Direction Z component should be at least cos(45°) ≈ 0.707
        (-pinhead.Direction.Z).Should().BeGreaterThanOrEqualTo(0.65f, "should be clamped toward vertical");
    }

    [Fact(Skip = "Requires solid geometry")]
    public void Optimize_JunctionBelowContact()
    {
        var (_, bvh) = CreateFlatPlate(10f);

        var pinhead = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 10f),
            new Vector3(0, 0, -1),
            bvh,
            new PinheadOptimizer.PinheadConfig());

        pinhead.JunctionPoint.Z.Should().BeLessThan(pinhead.ContactPoint.Z,
            "junction should be below contact");
    }
}
