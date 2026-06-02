using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class FastCollisionCheckEdgeCaseTests
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

    [Fact]
    public void PillarHitsMesh_OutsideCube_ReturnsFalse()
    {
        var bvh = BuildCubeBvh();
        FastCollisionCheck.PillarHitsMesh(bvh, 50, 50, 20, -5).Should().BeFalse("outside cube");
    }

    [Fact]
    public void SegmentHitsMesh_OutsideCube_ReturnsFalse()
    {
        var bvh = BuildCubeBvh();
        FastCollisionCheck.SegmentHitsMesh(bvh, new(50, 50, 20), new(50, 50, -5)).Should().BeFalse("outside cube");
    }

    [Fact]
    public void NearSurface_ZeroThreshold_OnlyExactSurface()
    {
        var bvh = BuildCubeBvh();
        // Point clearly away from surface should return false with zero threshold
        FastCollisionCheck.NearSurface(bvh, new(5, 5, 5), 0f).Should().BeFalse();
    }

    [Fact]
    public void Clearance_InsideMesh_SmallDistance()
    {
        var bvh = BuildCubeBvh();
        float cl = FastCollisionCheck.Clearance(bvh, new(5, 5, 9.5f));
        cl.Should().BeLessThan(1f, "0.5mm from top face");
    }

    [Fact]
    public void PillarHitsMesh_HighSampleCount_StillWorks()
    {
        var bvh = BuildCubeBvh();
        FastCollisionCheck.PillarHitsMesh(bvh, 5, 5, 15, -5, 20).Should().BeTrue();
    }

    [Fact]
    public void SegmentHitsMesh_DiagonalThroughCube_ReturnsTrue()
    {
        var bvh = BuildCubeBvh();
        FastCollisionCheck.SegmentHitsMesh(bvh, new(-5, -5, -5), new(15, 15, 15), 5).Should().BeTrue();
    }
}
