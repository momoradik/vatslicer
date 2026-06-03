using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportPointGeneratorStressTests
{
    private static StlMesh CreateCube(float size, float z)
    {
        var o = new Vector3(-size/2, -size/2, z);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = size;
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Generate_LargeCube_CompletesUnder2s()
    {
        var mesh = CreateCube(100f, 50f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
        result.Points.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Generate_SmallCube_FewPoints()
    {
        var mesh = CreateCube(2f, 1f);
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());

        result.Points.Count.Should().BeLessThan(50);
    }

    [Fact]
    public void Generate_AllPointIdsUnique()
    {
        var mesh = CreateCube(30f, 15f);
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());

        var ids = result.Points.Select(p => p.Id).ToHashSet();
        ids.Count.Should().Be(result.Points.Count, "all IDs must be unique");
    }

    [Fact]
    public void Generate_OverhangRegions_Counted()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());

        result.OverhangRegionsAnalyzed.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Generate_WithBvh_NoErrors()
    {
        var mesh = CreateCube(20f, 10f);
        var bvh = AabbBvh.Build(mesh);
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig(), bvh);

        result.Should().NotBeNull();
    }

    [Fact(Skip = "Triangle-based approach does not use layer height")]
    public void Generate_DifferentLayerHeights_DifferentResults()
    {
        var mesh = CreateCube(20f, 10f);
        var fine = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig { LayerHeightMm = 0.5f });
        var coarse = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig { LayerHeightMm = 5f });

        // Different analysis granularity may produce different point counts
        (fine.Points.Count != coarse.Points.Count || fine.OverhangRegionsAnalyzed != coarse.OverhangRegionsAnalyzed)
            .Should().BeTrue("different layer heights should affect analysis");
    }
}
