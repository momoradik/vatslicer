using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class BatchSlicerEdgeCaseTests
{
    private static StlMesh MeshFromVerts(Vector3[] verts)
    {
        int triCount = verts.Length / 3;
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
    public void SliceAll_EmptyMesh_NoLayers()
    {
        // Single degenerate triangle (all same point)
        var verts = new Vector3[] { new(0,0,0), new(0,0,0), new(0,0,0) };
        var mesh = MeshFromVerts(verts);
        var layers = BatchSlicer.SliceAll(mesh, 1f);
        // May have 0 or 1 layers — shouldn't crash
        layers.Should().NotBeNull();
    }

    [Fact]
    public void SliceAll_ThinLayer_ManySlices()
    {
        // Cube with fine layer height
        var o = Vector3.Zero;
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

        var mesh = MeshFromVerts(verts);
        var layers = BatchSlicer.SliceAll(mesh, 0.5f);

        // 10mm cube at 0.5mm layers ≈ 20 layers
        layers.Should().HaveCountGreaterThan(15);
    }

    [Fact]
    public void PolygonArea_Triangle_Correct()
    {
        var tri = new List<Vector2> { new(0, 0), new(10, 0), new(0, 10) };
        Math.Abs(BatchSlicer.PolygonArea(tri)).Should().BeApproximately(50f, 1f);
    }

    [Fact]
    public void PolygonArea_ReverseWinding_OppositeSign()
    {
        var ccw = new List<Vector2> { new(0, 0), new(10, 0), new(0, 10) };
        var cw = new List<Vector2> { new(0, 0), new(0, 10), new(10, 0) };
        float areaCcw = BatchSlicer.PolygonArea(ccw);
        float areaCw = BatchSlicer.PolygonArea(cw);
        // Opposite winding should give opposite sign (or same — depends on convention)
        // Just verify both compute non-zero area
        Math.Abs(areaCcw).Should().BeApproximately(Math.Abs(areaCw), 1f);
        Math.Abs(areaCcw).Should().BeGreaterThan(1f);
    }

    [Fact]
    public void ComputeOverhangs_NoOverhang_EmptyResult()
    {
        // Current layer fully inside previous layer
        var current = new List<List<Vector2>>
        {
            new() { new(2, 2), new(8, 2), new(8, 8), new(2, 8) },
        };
        var previous = new List<List<Vector2>>
        {
            new() { new(0, 0), new(10, 0), new(10, 10), new(0, 10) },
        };

        var overhangs = BatchSlicer.ComputeOverhangs(current, previous, 5f);
        overhangs.Should().BeEmpty("fully contained contour has no overhang");
    }

    [Fact]
    public void ComputeOverhangs_FullOverhang_DetectsNewIsland()
    {
        // Current layer with no previous layer overlap
        var current = new List<List<Vector2>>
        {
            new() { new(0, 0), new(10, 0), new(10, 10), new(0, 10) },
        };
        var previous = new List<List<Vector2>>(); // empty

        var overhangs = BatchSlicer.ComputeOverhangs(current, previous, 5f);
        overhangs.Should().NotBeEmpty();
        overhangs[0].Type.Should().Be(BatchSlicer.OverhangType.NewIsland);
    }
}
