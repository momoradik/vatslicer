using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests verifying the complete V2 → analytical slice → render pipeline
/// produces valid output at every stage.
/// </summary>
public class SupportEngineV2SlicePipelineTests
{
    private static SupportEngineV2.EngineResult Generate()
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
    public void Pipeline_SliceElements_AllHaveValidType()
    {
        var r = Generate();
        var validTypes = new HashSet<string> { "pinhead", "pillar", "bridge", "base", "junction", "anchor", "interconnect", "raft", "fillet" };
        foreach (var e in r.SliceElements)
            validTypes.Should().Contain(e.Type);
    }

    [Fact]
    public void Pipeline_SliceAll_ProducesConsecutiveLayers()
    {
        var r = Generate();
        if (r.SliceElements.Count == 0) return;

        float minZ = r.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = r.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        var layers = AnalyticalSupportSlicer.SliceAll(r.SliceElements, 0.5f, minZ, maxZ);

        for (int i = 1; i < layers.Count; i++)
        {
            layers[i].Z.Should().BeGreaterThan(layers[i-1].Z);
            (layers[i].Z - layers[i-1].Z).Should().BeApproximately(0.5f, 0.01f);
        }
    }

    [Fact]
    public void Pipeline_RenderAllLayers_AllValidPng()
    {
        var r = Generate();
        if (r.SliceElements.Count == 0) return;

        float minZ = r.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = r.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        var layers = AnalyticalSupportSlicer.SliceAll(r.SliceElements, 2f, minZ, maxZ);

        foreach (var layer in layers)
        {
            var png = SupportSliceIntegrator.RenderSupportOnlyLayer(layer.Circles, 50, 50, 30, 30);
            png.Should().NotBeNull();
            png[0].Should().Be(0x89); // PNG magic
        }
    }

    [Fact]
    public void Pipeline_Stats_MatchSliceOutput()
    {
        var r = Generate();
        var (layerCount, totalArea) = SupportSliceIntegrator.ComputeSupportStats(
            r.SliceElements, 1f, 0f, 20f);

        Math.Abs(r.SupportLayerCount - layerCount).Should().BeLessThan(10,
            "engine stats should roughly match recomputed stats");
    }

    [Fact]
    public void Pipeline_CirclePolygons_HaveCorrectSideCount()
    {
        var r = Generate();
        if (r.SliceElements.Count == 0) return;

        var circles = AnalyticalSupportSlicer.SliceAtZ(r.SliceElements, 5f);
        if (circles.Count == 0) return;

        var polys = SupportSliceIntegrator.CirclesToPolygons(circles, 12);
        foreach (var poly in polys)
            poly.Count.Should().Be(12);
    }

    [Fact]
    public void Pipeline_Heatmap_FromEngineOutput()
    {
        var r = Generate();
        var positions = r.Points.Select(p => new Vector2(p.Position.X, p.Position.Y)).ToList();

        var png = SupportHeatmapRenderer.Render(positions, null, 100, 100, 30, 30);
        png.Should().NotBeNull();
        png[0].Should().Be(0x89);
    }
}
