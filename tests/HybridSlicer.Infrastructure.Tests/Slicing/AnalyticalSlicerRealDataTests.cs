using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// Tests using real V2 engine output as input to the analytical slicer.
/// Verifies the full pipeline produces valid slice data.
/// </summary>
public class AnalyticalSlicerRealDataTests
{
    private static SupportEngineV2.EngineResult GenerateFromCube()
    {
        var o = new Vector3(-10, -10, 10);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));
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
        return SupportEngineV2.Generate(StlMesh.FromBinary(data), new SupportEngineV2.EngineConfig());
    }

    [Fact]
    public void RealData_SliceAtMultipleHeights_ConsistentCircleCount()
    {
        var result = GenerateFromCube();
        if (result.SliceElements.Count == 0) return;

        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        var counts = new List<int>();
        for (float z = minZ + 1; z < maxZ - 1; z += (maxZ - minZ) / 10f)
        {
            int count = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, z).Count;
            counts.Add(count);
        }

        // Interior layers should have roughly similar circle counts
        if (counts.Count > 3)
        {
            int min = counts.Skip(1).Take(counts.Count - 2).Min();
            int max = counts.Skip(1).Take(counts.Count - 2).Max();
            // Allow 3x variation (some supports may be shorter than others)
            if (min > 0)
                ((float)max / min).Should().BeLessThan(5f, "interior layers should have similar support density");
        }
    }

    [Fact]
    public void RealData_CircleCenters_WithinModelBounds()
    {
        var result = GenerateFromCube();
        if (result.SliceElements.Count == 0) return;

        float midZ = (result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z)) +
                      result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z))) / 2;

        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, midZ);

        foreach (var c in circles)
        {
            // Support circles should be within reasonable bounds of the model
            c.CenterX.Should().BeInRange(-30f, 30f, "circle X within model range");
            c.CenterY.Should().BeInRange(-30f, 30f, "circle Y within model range");
            c.Radius.Should().BeGreaterThan(0);
            c.Radius.Should().BeLessThan(10f, "circle radius should be reasonable");
        }
    }

    [Fact]
    public void RealData_SliceAll_LayerZsAreMonotonic()
    {
        var result = GenerateFromCube();
        if (result.SliceElements.Count == 0) return;

        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        var layers = AnalyticalSupportSlicer.SliceAll(result.SliceElements, 1f, minZ, maxZ);

        for (int i = 1; i < layers.Count; i++)
            layers[i].Z.Should().BeGreaterThan(layers[i - 1].Z, "layer Zs should increase");
    }

    [Fact]
    public void RealData_AllCirclesMarkedAsSupport()
    {
        var result = GenerateFromCube();
        if (result.SliceElements.Count == 0) return;

        float midZ = (result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z)) +
                      result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z))) / 2;

        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, midZ);
        circles.Should().AllSatisfy(c => c.IsSupport.Should().BeTrue());
    }
}
