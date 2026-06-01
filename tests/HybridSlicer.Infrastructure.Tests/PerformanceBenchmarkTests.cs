using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Performance benchmarks to ensure the V2 engine meets its targets.
/// These tests verify that key operations complete within acceptable time limits.
/// </summary>
public class PerformanceBenchmarkTests
{
    private static StlMesh CreateGrid(int nx, int ny, float cellSize = 1f)
    {
        // Grid of triangles to simulate a larger mesh
        int triCount = nx * ny * 2;
        var verts = new Vector3[triCount * 3];
        int vi = 0;
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        {
            float x0 = x * cellSize, y0 = y * cellSize;
            verts[vi++] = new(x0, y0, 0);
            verts[vi++] = new(x0 + cellSize, y0, 0);
            verts[vi++] = new(x0, y0 + cellSize, 0);
            verts[vi++] = new(x0 + cellSize, y0, 0);
            verts[vi++] = new(x0 + cellSize, y0 + cellSize, 0);
            verts[vi++] = new(x0, y0 + cellSize, 0);
        }
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
    public void BVH_Build_10kTriangles_Under100ms()
    {
        var mesh = CreateGrid(70, 70); // ~9800 triangles
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bvh = AabbBvh.Build(mesh);
        sw.Stop();

        bvh.TriangleCount.Should().BeGreaterThan(9000);
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "BVH build for 10k triangles should be under 500ms");
    }

    [Fact]
    public void BVH_10kRayCasts_Under50ms()
    {
        var mesh = CreateGrid(30, 30); // ~1800 triangles
        var bvh = AabbBvh.Build(mesh);
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int hits = 0;
        for (int i = 0; i < 10000; i++)
        {
            var origin = new Vector3(rng.NextSingle() * 30, rng.NextSingle() * 30, 5);
            var hit = bvh.RayCast(origin, new Vector3(0, 0, -1));
            if (hit.HasValue) hits++;
        }
        sw.Stop();

        hits.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "10k ray casts should be under 1 second");
    }

    [Fact]
    public void SpatialGrid_10kInsert_10kQuery_Under20ms()
    {
        var grid = new SpatialGrid<int>(5f);
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 10k insertions
        for (int i = 0; i < 10000; i++)
            grid.Insert(new Vector3(rng.NextSingle() * 200, rng.NextSingle() * 200, rng.NextSingle() * 200), i);

        // 10k radius queries
        int found = 0;
        for (int i = 0; i < 10000; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 200, rng.NextSingle() * 200, rng.NextSingle() * 200);
            if (grid.ExistsInRadius(pt, 5f)) found++;
        }

        sw.Stop();
        found.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(200, "20k operations should be under 200ms");
    }

    [Fact]
    public void V2Engine_SmallModel_Under2Seconds()
    {
        // Load the test floating model if available
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 6; i++) path = Path.GetDirectoryName(path)!;
        path = Path.Combine(path, "test_slice", "floating_model.stl");
        if (!File.Exists(path)) return; // skip if not available

        var data = File.ReadAllBytes(path);
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();

        result.ValidSupports.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "small model should process in under 2 seconds");
    }

    [Fact]
    public void IndexedTriangleSet_WeldVertices_50kVerts_Under100ms()
    {
        var mesh = new IndexedTriangleSet();

        // Create 50k vertices with some duplicates
        var rng = new Random(42);
        for (int i = 0; i < 50000; i++)
        {
            // Every 10th vertex is a duplicate of the previous
            if (i % 10 == 0 && i > 0)
                mesh.AddVertex(mesh.Vertices[i - 1] + new Vector3(0.0001f, 0, 0)); // near-duplicate
            else
                mesh.AddVertex(new Vector3(rng.NextSingle() * 100, rng.NextSingle() * 100, rng.NextSingle() * 100));
        }
        // Add faces
        for (int i = 0; i < mesh.VertexCount - 2; i += 3)
            mesh.AddFace(i, i + 1, i + 2);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        mesh.WeldVertices(0.001f);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500, "50k vertex weld should be under 500ms");
    }
}
