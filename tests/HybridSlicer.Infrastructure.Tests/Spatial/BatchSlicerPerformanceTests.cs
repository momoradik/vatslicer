using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class BatchSlicerPerformanceTests
{
    private static StlMesh CreateCube(float size)
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void SliceAll_100Layers_Under500ms()
    {
        var mesh = CreateCube(100f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layers = BatchSlicer.SliceAll(mesh, 1f);
        sw.Stop();

        layers.Count.Should().BeGreaterThan(50);
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public void SliceAll_FineLayerHeight_ManyLayers()
    {
        var mesh = CreateCube(10f);
        var layers = BatchSlicer.SliceAll(mesh, 0.1f);
        layers.Count.Should().BeGreaterThan(50);
    }

    [Fact]
    public void SliceAll_CoarseLayerHeight_FewLayers()
    {
        var mesh = CreateCube(10f);
        var layers = BatchSlicer.SliceAll(mesh, 5f);
        layers.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void ComputeOverhangs_EmptyPrevious_AllIslands()
    {
        var current = new List<List<Vector2>>
        {
            new() { new(0,0), new(10,0), new(10,10), new(0,10) },
            new() { new(20,20), new(30,20), new(30,30), new(20,30) },
        };

        var overhangs = BatchSlicer.ComputeOverhangs(current, new(), 5f);
        overhangs.Should().HaveCount(2);
        overhangs.All(o => o.Type == BatchSlicer.OverhangType.NewIsland).Should().BeTrue();
    }

    [Fact]
    public void PolygonArea_LargePolygon_Correct()
    {
        // 100x100 square
        var poly = new List<Vector2>
        {
            new(0, 0), new(100, 0), new(100, 100), new(0, 100)
        };
        Math.Abs(BatchSlicer.PolygonArea(poly)).Should().BeApproximately(10000f, 10f);
    }

    [Fact]
    public void SliceAll_LayerZsMonotonic()
    {
        var mesh = CreateCube(20f);
        var layers = BatchSlicer.SliceAll(mesh, 2f);

        for (int i = 1; i < layers.Count; i++)
            layers[i].Z.Should().BeGreaterThan(layers[i - 1].Z);
    }

    [Fact]
    public void SliceAll_IslandCountNonNegative()
    {
        var mesh = CreateCube(10f);
        var layers = BatchSlicer.SliceAll(mesh, 2f);

        foreach (var l in layers)
            l.IslandCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void SliceAll_TotalAreaPositive()
    {
        var mesh = CreateCube(10f);
        var layers = BatchSlicer.SliceAll(mesh, 2f);

        var interiorLayers = layers.Where(l => l.Z > 1 && l.Z < 9).ToList();
        foreach (var l in interiorLayers)
            l.TotalArea.Should().BeGreaterThan(0);
    }
}
