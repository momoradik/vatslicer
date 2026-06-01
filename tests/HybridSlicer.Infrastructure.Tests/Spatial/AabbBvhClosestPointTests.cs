using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Tests for BVH ClosestPoint accuracy — critical for pinhead optimization
/// where we need exact nearest-surface queries.
/// </summary>
public class AabbBvhClosestPointTests
{
    private static AabbBvh BuildCubeBvh(float size = 10f, Vector3? offset = null)
    {
        var o = offset ?? Vector3.Zero;
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a+o; verts[vi++]=b+o; verts[vi++]=c+o;
            verts[vi++]=a+o; verts[vi++]=c+o; verts[vi++]=d+o;
        }
        float s = size;
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
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void ClosestPoint_Above_ReturnsTopFace()
    {
        var bvh = BuildCubeBvh(10f);
        var result = bvh.ClosestPoint(new Vector3(5, 15, 5));

        result.Should().NotBeNull();
        result!.Value.Point.Y.Should().BeApproximately(10f, 0.5f, "closest point should be on top face");
        result.Value.Distance.Should().BeApproximately(5f, 0.5f);
    }

    [Fact]
    public void ClosestPoint_Below_ReturnsBottomFace()
    {
        var bvh = BuildCubeBvh(10f);
        var result = bvh.ClosestPoint(new Vector3(5, -5, 5));

        result.Should().NotBeNull();
        result!.Value.Point.Y.Should().BeApproximately(0f, 0.5f, "closest point on bottom face");
        result.Value.Distance.Should().BeApproximately(5f, 0.5f);
    }

    [Fact]
    public void ClosestPoint_Inside_ReturnsNearestFace()
    {
        var bvh = BuildCubeBvh(10f);
        // Point near the right face (X=10), distance ~1mm
        var result = bvh.ClosestPoint(new Vector3(9f, 5f, 5f));

        result.Should().NotBeNull();
        result!.Value.Distance.Should().BeLessThan(2f, "close to right face");
    }

    [Fact]
    public void ClosestPoint_AtCorner_ReturnsCorner()
    {
        var bvh = BuildCubeBvh(10f);
        var result = bvh.ClosestPoint(new Vector3(15, 15, 15));

        result.Should().NotBeNull();
        // Distance to nearest corner (10,10,10) should be ~8.66
        result!.Value.Distance.Should().BeApproximately(8.66f, 1f);
    }

    [Fact]
    public void ClosestPoint_FarAway_StillFindsNearest()
    {
        var bvh = BuildCubeBvh(10f);
        var result = bvh.ClosestPoint(new Vector3(1000, 0, 0));

        result.Should().NotBeNull();
        result!.Value.Distance.Should().BeApproximately(990f, 2f);
    }

    [Fact]
    public void ClosestTriangle_ReturnsValidIndex()
    {
        var bvh = BuildCubeBvh(10f);
        int idx = bvh.ClosestTriangle(new Vector3(5, 15, 5));

        idx.Should().BeGreaterThanOrEqualTo(0);
        idx.Should().BeLessThan(12); // 12 triangles in a cube
    }

    [Fact]
    public void ClosestPoint_MultipleQueries_Consistent()
    {
        var bvh = BuildCubeBvh(10f);

        // Same query should give same result
        var r1 = bvh.ClosestPoint(new Vector3(5, 20, 5));
        var r2 = bvh.ClosestPoint(new Vector3(5, 20, 5));

        r1.Should().NotBeNull();
        r2.Should().NotBeNull();
        r1!.Value.Distance.Should().BeApproximately(r2!.Value.Distance, 0.001f);
        r1.Value.Point.Should().Be(r2.Value.Point);
    }
}
