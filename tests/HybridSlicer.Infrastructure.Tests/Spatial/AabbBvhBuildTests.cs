using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Tests for BVH construction correctness and edge cases.
/// </summary>
public class AabbBvhBuildTests
{
    private static StlMesh MeshFromVerts(Vector3[] verts)
    {
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Build_2Triangles_CorrectNodeCount()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(1,0,0), new(0,1,0),
            new(1,0,0), new(1,1,0), new(0,1,0),
        };
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(2);
        bvh.NodeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_100Triangles_ReasonableNodeCount()
    {
        var verts = new Vector3[300];
        var rng = new Random(42);
        for (int i = 0; i < 300; i++)
            verts[i] = new(rng.NextSingle() * 100, rng.NextSingle() * 100, rng.NextSingle() * 100);

        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(100);
        bvh.NodeCount.Should().BeLessThan(300, "node count should be bounded");
    }

    [Fact]
    public void Build_AllIdenticalTriangles_DoesNotCrash()
    {
        var verts = new Vector3[30]; // 10 identical triangles
        for (int i = 0; i < 30; i += 3)
        {
            verts[i] = new(0, 0, 0);
            verts[i + 1] = new(1, 0, 0);
            verts[i + 2] = new(0, 1, 0);
        }
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(10);
    }

    [Fact]
    public void Build_LinearTriangles_SAHHandlesIt()
    {
        // Triangles spread along X axis only — tests SAH with 1D data
        var verts = new Vector3[150]; // 50 triangles
        for (int i = 0; i < 50; i++)
        {
            float x = i * 10f;
            verts[i * 3] = new(x, 0, 0);
            verts[i * 3 + 1] = new(x + 10, 0, 0);
            verts[i * 3 + 2] = new(x, 1, 0);
        }
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        bvh.TriangleCount.Should().Be(50);
    }

    [Fact]
    public void Build_PreservesTriangleCount()
    {
        var rng = new Random(123);
        for (int n = 1; n <= 50; n += 10)
        {
            var verts = new Vector3[n * 3];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = new(rng.NextSingle() * 50, rng.NextSingle() * 50, rng.NextSingle() * 50);

            var bvh = AabbBvh.Build(MeshFromVerts(verts));
            bvh.TriangleCount.Should().Be(n);
        }
    }

    [Fact]
    public void Build_LargeMesh_CompletesInTime()
    {
        // 5000 triangles
        var verts = new Vector3[15000];
        var rng = new Random(42);
        for (int i = 0; i < verts.Length; i++)
            verts[i] = new(rng.NextSingle() * 200, rng.NextSingle() * 200, rng.NextSingle() * 200);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bvh = AabbBvh.Build(MeshFromVerts(verts));
        sw.Stop();

        bvh.TriangleCount.Should().Be(5000);
        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "5k triangle BVH build should be fast");
    }
}
