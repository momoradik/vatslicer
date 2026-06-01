using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class AabbBvhBeamCastTests
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
    public void BeamCast_NarrowBeam_HitsLikeSingleRay()
    {
        var bvh = BuildCubeBvh();
        float singleDist = bvh.RayCast(new Vector3(5, 5, -5), Vector3.UnitZ)?.Distance ?? float.MaxValue;
        float beamDist = bvh.BeamCast(new Vector3(5, 5, -5), Vector3.UnitZ, 0.001f, 1);

        beamDist.Should().BeApproximately(singleDist, 0.1f);
    }

    [Fact]
    public void BeamCast_WideBeam_DetectsCloserHit()
    {
        var bvh = BuildCubeBvh();
        // Beam aimed at edge of cube — ring rays may hit closer than center
        float narrowDist = bvh.BeamCast(new Vector3(0, 5, -5), Vector3.UnitZ, 0.01f, 1);
        float wideDist = bvh.BeamCast(new Vector3(0, 5, -5), Vector3.UnitZ, 3f, 8);

        wideDist.Should().BeLessThanOrEqualTo(narrowDist + 0.1f,
            "wider beam should find same or closer hit");
    }

    [Fact]
    public void BeamCast_MissesEntirely_ReturnsMaxDistance()
    {
        var bvh = BuildCubeBvh();
        float dist = bvh.BeamCast(new Vector3(50, 50, -5), Vector3.UnitZ, 1f, 8);
        dist.Should().Be(float.MaxValue);
    }

    [Fact]
    public void BeamCast_ZeroRadius_MatchesSingleRay()
    {
        var bvh = BuildCubeBvh();
        float rayDist = bvh.RayCast(new Vector3(5, 5, 15), -Vector3.UnitZ)?.Distance ?? float.MaxValue;
        float beamDist = bvh.BeamCast(new Vector3(5, 5, 15), -Vector3.UnitZ, 0f, 8);

        beamDist.Should().BeApproximately(rayDist, 0.1f);
    }

    [Fact]
    public void BeamCast_MaxDistance_Limits()
    {
        var bvh = BuildCubeBvh();
        // Beam that would hit at ~5mm but max distance is 2mm
        float dist = bvh.BeamCast(new Vector3(5, 5, -5), Vector3.UnitZ, 0.5f, 4, 2f);
        dist.Should().Be(2f, "max distance should cap the result");
    }

    [Fact]
    public void BeamCast_MoreRays_BetterCoverage()
    {
        var bvh = BuildCubeBvh();
        // Both should find the same hit — more rays just verifies volume
        float few = bvh.BeamCast(new Vector3(5, 5, -5), Vector3.UnitZ, 1f, 2);
        float many = bvh.BeamCast(new Vector3(5, 5, -5), Vector3.UnitZ, 1f, 16);

        many.Should().BeLessThanOrEqualTo(few + 0.1f,
            "more rays should find same or closer hit");
    }
}
