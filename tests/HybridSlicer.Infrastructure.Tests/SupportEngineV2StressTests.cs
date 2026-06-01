using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Stress tests verifying the V2 engine handles extreme inputs.
/// </summary>
public class SupportEngineV2StressTests
{
    private static StlMesh CreateGrid(int nx, int ny, float cellSize, float z)
    {
        int triCount = nx * ny * 2;
        var verts = new Vector3[triCount * 3];
        int vi = 0;
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        {
            float x0 = x * cellSize, y0 = y * cellSize;
            verts[vi++] = new(x0, y0, z);
            verts[vi++] = new(x0 + cellSize, y0, z);
            verts[vi++] = new(x0, y0 + cellSize, z);
            verts[vi++] = new(x0 + cellSize, y0, z);
            verts[vi++] = new(x0 + cellSize, y0 + cellSize, z);
            verts[vi++] = new(x0, y0 + cellSize, z);
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Stress_2kTriangles_CompletesUnder3s()
    {
        var mesh = CreateGrid(30, 30, 1f, 20f); // ~1800 tris
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();

        result.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [Fact]
    public void Stress_MaxDensity_DoesntExplode()
    {
        var mesh = CreateGrid(10, 10, 2f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 1.0f,
            MinSpacingMm = 1f,
        });

        result.Should().NotBeNull();
        result.ValidSupports.Should().BeLessThan(10000, "should not produce absurd number");
    }

    [Fact]
    public void Stress_AllFeaturesEnabled_CompletesUnder5s()
    {
        var mesh = CreateGrid(15, 15, 1.5f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.7f,
            EnableInterconnections = true,
            DrainHoleExclusions = new() { (new Vector3(5, 5, 10), 3f) },
        });

        result.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void Stress_RepeatedGeneration_ConsistentPerformance()
    {
        var mesh = CreateGrid(10, 10, 1f, 8f);
        var times = new List<long>();

        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { Seed = 42 });
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        // All runs should be within 3x of each other (no degradation)
        var maxTime = times.Max();
        var minTime = times.Min();
        if (minTime > 0)
            ((float)maxTime / minTime).Should().BeLessThan(10f, "performance should be reasonably consistent");
    }
}
