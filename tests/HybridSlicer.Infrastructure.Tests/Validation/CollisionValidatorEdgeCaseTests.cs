using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests.Validation;

public class CollisionValidatorEdgeCaseTests
{
    [Fact]
    public void ValidatePinheads_ValidPinhead_NoIssues()
    {
        // Minimal BVH far from pinhead
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12;
        for (int v = 0; v < 3; v++) {
            BitConverter.GetBytes(1000f + v).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
        }
        var bvh = AabbBvh.Build(StlMesh.FromBinary(data));

        var pinhead = new PinheadOptimizer.Pinhead
        {
            ContactPoint = new(0, 0, 10),
            Direction = new(0, 0, -1),
            PinCenter = new(0, 0, 9.8f),
            BackCenter = new(0, 0, 9.3f),
            JunctionPoint = new(0, 0, 8.8f),
            PinRadius = 0.2f, BackRadius = 0.5f, Width = 1f,
            Clearance = 100f, IsValid = true, NeedsAnchor = false,
        };

        var issues = CollisionValidator.ValidatePinheads(
            new() { ("s1", pinhead) }, bvh, 4);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePinheads_InvalidPinhead_Skipped()
    {
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12;
        for (int v = 0; v < 3; v++) {
            BitConverter.GetBytes(0f).CopyTo(data, off); off += 12;
        }
        var bvh = AabbBvh.Build(StlMesh.FromBinary(data));

        var pinhead = new PinheadOptimizer.Pinhead
        {
            ContactPoint = Vector3.Zero, Direction = -Vector3.UnitZ,
            PinCenter = Vector3.Zero, BackCenter = Vector3.Zero,
            JunctionPoint = Vector3.Zero,
            PinRadius = 0.2f, BackRadius = 0.5f, Width = 1f,
            Clearance = 0f, IsValid = false, NeedsAnchor = true,
        };

        var issues = CollisionValidator.ValidatePinheads(
            new() { ("s1", pinhead) }, bvh, 4);

        issues.Should().BeEmpty("invalid pinheads should be skipped");
    }

    [Fact]
    public void ValidateAll_Empty_ZeroIssues()
    {
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12;
        for (int v = 0; v < 3; v++) {
            BitConverter.GetBytes(0f).CopyTo(data, off); off += 12;
        }
        var bvh = AabbBvh.Build(StlMesh.FromBinary(data));

        var result = CollisionValidator.ValidateAll(new(), new(), new(), bvh);

        result.TotalSupportsChecked.Should().Be(0);
        result.CollidingSupports.Should().Be(0);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePillarRoutes_InsideMesh_DetectsCollision()
    {
        // Build a cube BVH
        var o = Vector3.Zero;
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 10f;
        Quad(new(0,0,s), new(s,0,s), new(s,s,s), new(0,s,s));
        Quad(new(0,0,0), new(0,s,0), new(s,s,0), new(s,0,0));
        Quad(new(s,0,0), new(s,0,s), new(s,s,s), new(s,s,0));
        Quad(new(0,0,s), new(0,0,0), new(0,s,0), new(0,s,s));
        Quad(new(0,s,s), new(s,s,s), new(s,s,0), new(0,s,0));
        Quad(new(0,0,0), new(s,0,0), new(s,0,s), new(0,0,s));

        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++) {
            off += 12;
            for (int v = 0; v < 3; v++) {
                BitConverter.GetBytes(verts[t*3+v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t*3+v].Y).CopyTo(data, off+4);
                BitConverter.GetBytes(verts[t*3+v].Z).CopyTo(data, off+8);
                off += 12;
            }
            off += 2;
        }
        var bvh = AabbBvh.Build(StlMesh.FromBinary(data));

        // Route with waypoint inside cube
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new(5, 5, 15), Radius = 0.5f, Type = "junction" },
                new() { Position = new(5, 5, 5), Radius = 0.5f, Type = "pillar" }, // INSIDE
                new() { Position = new(5, 5, -5), Radius = 0.5f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 20,
        };

        var issues = CollisionValidator.ValidatePillarRoutes(new() { ("s1", route) }, bvh, 4);
        issues.Should().NotBeEmpty("pillar through cube interior should collide");
    }
}
