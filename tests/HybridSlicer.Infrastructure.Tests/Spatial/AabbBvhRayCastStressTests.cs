using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class AabbBvhRayCastStressTests
{
    private static AabbBvh BuildCubeBvh()
    {
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
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void RayCast_AllDirections_FromCenter_AllHit()
    {
        var bvh = BuildCubeBvh();
        var center = new Vector3(5, 5, 5);
        var directions = new[]
        {
            Vector3.UnitX, -Vector3.UnitX,
            Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ,
            Vector3.Normalize(new Vector3(1,1,1)),
            Vector3.Normalize(new Vector3(-1,1,-1)),
            Vector3.Normalize(new Vector3(1,-1,0)),
        };

        foreach (var dir in directions)
        {
            var hit = bvh.RayCast(center, dir);
            hit.Should().NotBeNull($"ray from center in direction ({dir.X},{dir.Y},{dir.Z}) should hit cube face");
        }
    }

    [Fact]
    public void RayCast_FromOutside_HitDistanceCorrect()
    {
        var bvh = BuildCubeBvh();

        // Ray from (5, 5, -10) toward +Z → should hit Z=0 face at distance 10
        var hit = bvh.RayCast(new Vector3(5, 5, -10), Vector3.UnitZ);
        hit.Should().NotBeNull();
        hit!.Value.Distance.Should().BeApproximately(10f, 0.5f);
    }

    [Fact]
    public void RayCast_1000RandomDirections_NoErrors()
    {
        var bvh = BuildCubeBvh();
        var rng = new Random(42);

        for (int i = 0; i < 1000; i++)
        {
            var origin = new Vector3(5, 5, 5); // inside cube
            var dir = Vector3.Normalize(new Vector3(
                rng.NextSingle() * 2 - 1,
                rng.NextSingle() * 2 - 1,
                rng.NextSingle() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;

            var hit = bvh.RayCast(origin, dir);
            hit.Should().NotBeNull("ray from inside cube should always hit a face");
            hit!.Value.Distance.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void RayCast_HitNormal_IsUnitLength()
    {
        var bvh = BuildCubeBvh();

        var hit = bvh.RayCast(new Vector3(5, 5, -5), Vector3.UnitZ);
        hit.Should().NotBeNull();
        hit!.Value.Normal.Length().Should().BeApproximately(1f, 0.1f);
    }
}
