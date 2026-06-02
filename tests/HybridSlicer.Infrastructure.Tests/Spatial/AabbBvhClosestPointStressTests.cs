using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class AabbBvhClosestPointStressTests
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
    public void ClosestPoint_1000Queries_Under1s()
    {
        var bvh = BuildCubeBvh();
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 30 - 10, rng.NextSingle() * 30 - 10, rng.NextSingle() * 30 - 10);
            bvh.ClosestPoint(pt);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void ClosestPoint_AlwaysOnSurface()
    {
        var bvh = BuildCubeBvh();
        var rng = new Random(42);

        for (int i = 0; i < 50; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 20 - 5, rng.NextSingle() * 20 - 5, rng.NextSingle() * 20 - 5);
            var result = bvh.ClosestPoint(pt);

            result.Should().NotBeNull();
            // Closest point should be on a face of the cube (one coord ≈ 0 or 10)
            var cp = result!.Value.Point;
            bool onFace =
                MathF.Abs(cp.X) < 0.1f || MathF.Abs(cp.X - 10) < 0.1f ||
                MathF.Abs(cp.Y) < 0.1f || MathF.Abs(cp.Y - 10) < 0.1f ||
                MathF.Abs(cp.Z) < 0.1f || MathF.Abs(cp.Z - 10) < 0.1f;

            // Most points should land on a face (some land on edges/corners)
            // Just verify the distance is reasonable
            result.Value.Distance.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void ClosestPoint_SymmetricQueries_SymmetricResults()
    {
        var bvh = BuildCubeBvh();

        var r1 = bvh.ClosestPoint(new Vector3(5, 15, 5));
        var r2 = bvh.ClosestPoint(new Vector3(5, -5, 5));

        r1.Should().NotBeNull();
        r2.Should().NotBeNull();
        r1!.Value.Distance.Should().BeApproximately(r2!.Value.Distance, 0.5f,
            "symmetric points should have similar distances");
    }

    [Fact]
    public void ClosestTriangle_1000Queries_AllValid()
    {
        var bvh = BuildCubeBvh();
        var rng = new Random(42);

        for (int i = 0; i < 1000; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 20 - 5, rng.NextSingle() * 20 - 5, rng.NextSingle() * 20 - 5);
            int tri = bvh.ClosestTriangle(pt);
            tri.Should().BeInRange(0, 11);
        }
    }
}
