using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Comprehensive ray-cast tests covering all axis-aligned and diagonal directions.
/// </summary>
public class AabbBvhRayCastTests
{
    private static AabbBvh BuildCubeBvh(float size = 10f)
    {
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
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

    [Theory]
    [InlineData(5, 5, -5, 0, 0, 1)]   // +Z into front face
    [InlineData(5, 5, 15, 0, 0, -1)]  // -Z into back face
    [InlineData(-5, 5, 5, 1, 0, 0)]   // +X into left face
    [InlineData(15, 5, 5, -1, 0, 0)]  // -X into right face
    [InlineData(5, -5, 5, 0, 1, 0)]   // +Y into bottom face
    [InlineData(5, 15, 5, 0, -1, 0)]  // -Y into top face
    public void RayCast_AllAxes_HitsAtCorrectDistance(
        float ox, float oy, float oz, float dx, float dy, float dz)
    {
        var bvh = BuildCubeBvh(10f);
        var hit = bvh.RayCast(new Vector3(ox, oy, oz), new Vector3(dx, dy, dz));

        hit.Should().NotBeNull($"ray from ({ox},{oy},{oz}) dir ({dx},{dy},{dz}) should hit cube");
        hit!.Value.Distance.Should().BeApproximately(5f, 0.5f, "should hit at ~5mm distance");
    }

    [Fact]
    public void RayCast_DiagonalRay_HitsCorrectly()
    {
        var bvh = BuildCubeBvh(10f);
        var hit = bvh.RayCast(new Vector3(-5, -5, -5), Vector3.Normalize(new Vector3(1, 1, 1)));

        hit.Should().NotBeNull("diagonal ray toward cube should hit");
    }

    [Fact]
    public void RayCast_RayFromInside_HitsExit()
    {
        var bvh = BuildCubeBvh(10f);
        // Ray from inside cube pointing outward
        var hit = bvh.RayCast(new Vector3(5, 5, 5), new Vector3(1, 0, 0));

        hit.Should().NotBeNull("ray from inside should hit exit face");
        hit!.Value.Point.X.Should().BeApproximately(10f, 0.5f);
    }

    [Fact]
    public void RayCast_MaxDistance_Respected()
    {
        var bvh = BuildCubeBvh(10f);
        // Ray that would hit at ~5mm but max distance is 3mm
        var hit = bvh.RayCast(new Vector3(5, 5, -5), Vector3.UnitZ, 3f);
        hit.Should().BeNull("max distance should prevent hit");
    }

    [Fact]
    public void RayCast_ReturnsNormal()
    {
        var bvh = BuildCubeBvh(10f);
        var hit = bvh.RayCast(new Vector3(5, 15, 5), -Vector3.UnitY);

        hit.Should().NotBeNull();
        // Normal should point upward (+Y) for top face hit
        hit!.Value.Normal.Y.Should().BeGreaterThan(0.5f, "top face normal should point up");
    }

    [Fact]
    public void RayCast_ReturnsTriangleIndex()
    {
        var bvh = BuildCubeBvh(10f);
        var hit = bvh.RayCast(new Vector3(5, 5, -5), Vector3.UnitZ);

        hit.Should().NotBeNull();
        hit!.Value.TriangleIndex.Should().BeGreaterThanOrEqualTo(0);
        hit.Value.TriangleIndex.Should().BeLessThan(12);
    }
}
