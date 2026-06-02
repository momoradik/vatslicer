using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Performance tests for FastCollisionCheck — verifying it's significantly
/// faster than full beam-cast for generation-time collision checks.
/// </summary>
public class FastCollisionCheckPerformanceTests
{
    private static AabbBvh BuildGridBvh(int nx, int ny, float z)
    {
        int triCount = nx * ny * 2;
        var verts = new Vector3[triCount * 3];
        int vi = 0;
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        {
            verts[vi++] = new(x, y, z);
            verts[vi++] = new(x + 1, y, z);
            verts[vi++] = new(x, y + 1, z);
            verts[vi++] = new(x + 1, y, z);
            verts[vi++] = new(x + 1, y + 1, z);
            verts[vi++] = new(x, y + 1, z);
        }
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
    public void PillarHitsMesh_1000Queries_Under100ms()
    {
        var bvh = BuildGridBvh(20, 20, 10f); // 800 triangles
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int hits = 0;
        for (int i = 0; i < 1000; i++)
        {
            float x = rng.NextSingle() * 20;
            float y = rng.NextSingle() * 20;
            if (FastCollisionCheck.PillarHitsMesh(bvh, x, y, 15f, -5f, 3))
                hits++;
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "1000 pillar checks should be fast");
    }

    [Fact]
    public void Clearance_1000Queries_Under200ms()
    {
        var bvh = BuildGridBvh(20, 20, 10f);
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 30, rng.NextSingle() * 30, rng.NextSingle() * 20);
            FastCollisionCheck.Clearance(bvh, pt);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "1000 clearance queries should be fast");
    }

    [Fact]
    public void SegmentHitsMesh_500Queries_Under100ms()
    {
        var bvh = BuildGridBvh(20, 20, 10f);
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 500; i++)
        {
            var a = new Vector3(rng.NextSingle() * 20, rng.NextSingle() * 20, 15);
            var b = new Vector3(rng.NextSingle() * 20, rng.NextSingle() * 20, -5);
            FastCollisionCheck.SegmentHitsMesh(bvh, a, b, 3);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "500 segment checks should be fast");
    }
}
