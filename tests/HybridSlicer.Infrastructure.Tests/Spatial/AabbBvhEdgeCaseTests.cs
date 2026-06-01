using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Edge-case tests for BVH robustness.
/// Aerospace parts have thin walls, internal channels, and complex geometry
/// that stress the BVH in ways simple cubes don't.
/// </summary>
public class AabbBvhEdgeCaseTests
{
    private static StlMesh MeshFromVerts(Vector3[] verts)
    {
        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Build_SingleTriangle_Works()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(1);
        bvh.NodeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RayCast_SingleTriangle_HitsCorrectly()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 0), new(10, 0, 0), new(5, 10, 0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));

        // Ray from above center pointing down
        var hit = bvh.RayCast(new Vector3(5, 3, 5), new Vector3(0, 0, -1));
        hit.Should().NotBeNull();
        hit!.Value.Point.Z.Should().BeApproximately(0, 0.01f);
    }

    [Fact]
    public void RayCast_ParallelToTriangle_NoHit()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 0), new(10, 0, 0), new(5, 10, 0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));

        // Ray parallel to the Z=0 plane
        var hit = bvh.RayCast(new Vector3(5, 3, 1), new Vector3(1, 0, 0));
        hit.Should().BeNull();
    }

    [Fact]
    public void ClosestPoint_FarAwayPoint_FindsNearest()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 0), new(10, 0, 0), new(5, 10, 0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));

        var result = bvh.ClosestPoint(new Vector3(100, 100, 100));
        result.Should().NotBeNull();
        // Closest point should be on the triangle
        result!.Value.Point.Z.Should().BeApproximately(0, 0.01f);
    }

    [Fact]
    public void Build_CoplanarTriangles_NoError()
    {
        // Many triangles all in the Z=0 plane — SAH split should handle this
        var verts = new Vector3[300]; // 100 triangles
        for (int i = 0; i < 100; i++)
        {
            float x = (i % 10) * 10f;
            float y = (i / 10) * 10f;
            verts[i * 3] = new(x, y, 0);
            verts[i * 3 + 1] = new(x + 10, y, 0);
            verts[i * 3 + 2] = new(x, y + 10, 0);
        }
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(100);
    }

    [Fact]
    public void Build_DegenerateTriangle_HandledGracefully()
    {
        // Zero-area triangle (all vertices the same)
        var verts = new Vector3[]
        {
            new(5, 5, 5), new(5, 5, 5), new(5, 5, 5),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(1);
        // Ray should miss degenerate triangle
        var hit = bvh.RayCast(new Vector3(5, 5, 10), new Vector3(0, 0, -1));
        // May or may not hit — just shouldn't crash
    }

    [Fact]
    public void BeamCast_NarrowBeam_MatchesRayCast()
    {
        var verts = new Vector3[]
        {
            new(-10, -10, 0), new(10, -10, 0), new(0, 10, 0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));

        float rayDist = bvh.RayCast(new Vector3(0, 0, 5), new Vector3(0, 0, -1))?.Distance ?? float.MaxValue;
        float beamDist = bvh.BeamCast(new Vector3(0, 0, 5), new Vector3(0, 0, -1), 0.001f, 1);

        beamDist.Should().BeApproximately(rayDist, 0.1f, "narrow beam should match single ray");
    }

    [Fact]
    public void IsInside_LargeOffset_StillCorrect()
    {
        // Cube at large offset — tests hash/coordinate precision
        var o = new Vector3(10000, 10000, 10000);
        var s = 10f;
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++] = a; verts[vi++] = b; verts[vi++] = c;
            verts[vi++] = a; verts[vi++] = c; verts[vi++] = d;
        }
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.IsInside(o + new Vector3(5, 5, 5)).Should().BeTrue();
        bvh.IsInside(o + new Vector3(15, 15, 15)).Should().BeFalse();
    }

    [Fact]
    public void RayCast_NegativeDirection_Works()
    {
        var verts = new Vector3[]
        {
            new(-10, -10, 10), new(10, -10, 10), new(0, 10, 10),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));

        // Ray from below pointing up
        var hit = bvh.RayCast(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        hit.Should().NotBeNull();
        hit!.Value.Point.Z.Should().BeApproximately(10, 0.01f);
    }

    [Fact]
    public void ClosestPoint_PointOnEdge_ReturnsNearEdge()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 0), new(10, 0, 0), new(5, 10, 0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));

        // Point directly above the midpoint of the (0,0)-(10,0) edge
        var result = bvh.ClosestPoint(new Vector3(5, 0, 3));
        result.Should().NotBeNull();
        result!.Value.Point.Z.Should().BeApproximately(0, 0.01f);
        result.Value.Point.X.Should().BeApproximately(5, 0.1f);
        result.Value.Point.Y.Should().BeApproximately(0, 0.1f);
        result.Value.Distance.Should().BeApproximately(3, 0.01f);
    }
}
