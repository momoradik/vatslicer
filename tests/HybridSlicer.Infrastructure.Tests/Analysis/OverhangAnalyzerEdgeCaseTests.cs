using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class OverhangAnalyzerEdgeCaseTests
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
    public void Analyze_SingleTriangle_DoesNotCrash()
    {
        var verts = new Vector3[] { new(0,0,5), new(10,0,5), new(5,10,5) };
        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 2f);
        result.Should().NotBeNull();
    }

    [Fact]
    public void Analyze_VeryCoarseLayerHeight_FewLayers()
    {
        var o = new Vector3(-5, -5, 0);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 10f;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 100f); // very coarse
        result.Layers.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Analyze_ResultHasElapsedTime()
    {
        var verts = new Vector3[] { new(0,0,5), new(10,0,5), new(5,10,5) };
        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 2f);
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Analyze_TotalOverhangArea_NonNegative()
    {
        var verts = new Vector3[] { new(0,0,5), new(10,0,5), new(5,10,5) };
        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 2f);
        result.TotalOverhangArea.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Analyze_TotalIslands_NonNegative()
    {
        var verts = new Vector3[] { new(0,0,5), new(10,0,5), new(5,10,5) };
        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 2f);
        result.TotalIslands.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Analyze_AllLayerZsPositive()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(10,0,0), new(5,10,0),
            new(0,0,10), new(10,0,10), new(5,10,10),
        };
        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 2f);

        foreach (var layer in result.Layers)
            layer.Z.Should().BeGreaterThan(-1f);
    }

    [Fact]
    public void Analyze_AllRegionPrioritiesInRange()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(10,0,0), new(5,10,0),
            new(0,0,10), new(10,0,10), new(5,10,10),
        };
        var mesh = MeshFromVerts(verts);
        var result = OverhangAnalyzer.Analyze(mesh, 2f);

        foreach (var layer in result.Layers)
        foreach (var region in layer.Regions)
        {
            region.Priority.Should().BeInRange(0f, 1.1f);
            region.Area.Should().BeGreaterThan(0);
        }
    }
}
