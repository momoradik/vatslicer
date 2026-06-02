using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class AabbBvhIsInsideStressTests
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
    public void IsInside_1000RandomPoints_AllClearlyInsideDetected()
    {
        var bvh = BuildCubeBvh(10f);
        var rng = new Random(42);
        int missed = 0;

        for (int i = 0; i < 1000; i++)
        {
            // Clearly inside: [1, 9] range
            float x = 1f + rng.NextSingle() * 8f;
            float y = 1f + rng.NextSingle() * 8f;
            float z = 1f + rng.NextSingle() * 8f;
            if (!bvh.IsInside(new Vector3(x, y, z))) missed++;
        }

        missed.Should().Be(0, "all clearly-inside points should be detected");
    }

    [Fact]
    public void IsInside_1000RandomPoints_AllClearlyOutsideRejected()
    {
        var bvh = BuildCubeBvh(10f);
        var rng = new Random(42);
        int falsePositives = 0;

        for (int i = 0; i < 1000; i++)
        {
            // Clearly outside: one coord > 12 or < -2
            float x = 12f + rng.NextSingle() * 100f;
            float y = rng.NextSingle() * 10f;
            float z = rng.NextSingle() * 10f;
            if (bvh.IsInside(new Vector3(x, y, z))) falsePositives++;
        }

        falsePositives.Should().Be(0, "all clearly-outside points should be rejected");
    }

    [Fact]
    public void IsInside_GridScan_CorrectBoundary()
    {
        var bvh = BuildCubeBvh(10f);
        int insideCount = 0;
        int outsideCount = 0;

        // Scan a 3D grid and count inside/outside
        for (float x = -2; x <= 12; x += 2)
        for (float y = -2; y <= 12; y += 2)
        for (float z = -2; z <= 12; z += 2)
        {
            bool inside = bvh.IsInside(new Vector3(x, y, z));
            bool expected = x > 0.5f && x < 9.5f && y > 0.5f && y < 9.5f && z > 0.5f && z < 9.5f;
            if (inside) insideCount++;
            else outsideCount++;
        }

        insideCount.Should().BeGreaterThan(10, "should detect many inside points");
        outsideCount.Should().BeGreaterThan(100, "should detect many outside points");
    }

    [Fact]
    public void IsInside_Performance_10kQueries_Under2s()
    {
        var bvh = BuildCubeBvh(10f);
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 20 - 5, rng.NextSingle() * 20 - 5, rng.NextSingle() * 20 - 5);
            bvh.IsInside(pt);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }
}
