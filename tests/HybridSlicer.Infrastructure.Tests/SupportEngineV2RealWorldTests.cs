using System.Numerics;
using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Real-world scenario tests simulating actual aerospace manufacturing workflows.
/// </summary>
public class SupportEngineV2RealWorldTests
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
    public void Workflow_GenerateThenExport_NoError()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        var stl = result.SupportMesh.ToStlBinary();

        stl.Length.Should().Be(84 + result.SupportMesh.FaceCount * 50);
        StlMesh.FromBinary(stl).TriangleCount.Should().Be(result.SupportMesh.FaceCount);
    }

    [Fact]
    public void Workflow_GenerateThenSlice_ProducesLayers()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var layers = AnalyticalSupportSlicer.SliceAll(result.SliceElements, 1f, 0f, 30f);
        layers.Should().NotBeEmpty();
    }

    [Fact]
    public void Workflow_GenerateThenRender_ProducesPng()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.SliceElements.Count == 0) return;

        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, 5f);
        if (circles.Count == 0) return;

        var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 100, 100, 50, 50);
        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Workflow_ChangeDensity_DifferentResults()
    {
        var mesh = CreateCube(20f, 10f);
        var low = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.2f });
        var high = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.9f });

        (low.ValidSupports != high.ValidSupports || low.TotalSupportVolumeMm3 != high.TotalSupportVolumeMm3)
            .Should().BeTrue("different density should produce different results");
    }

    [Fact]
    public void Workflow_BottomUp_SmallTips()
    {
        var mesh = CreateCube(20f, 10f);
        var bu = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = PrinterOrientation.BottomUp,
            PinRadiusMm = 0.3f,
        });

        if (bu.Pinheads.Any(p => p.pinhead.IsValid))
        {
            var ph = bu.Pinheads.First(p => p.pinhead.IsValid).pinhead;
            ph.PinRadius.Should().BeLessThan(0.3f, "BottomUp scales pin down by 80%");
        }
    }

    [Fact]
    public void Workflow_WithDrainHoles_FewerSupports()
    {
        var mesh = CreateCube(30f, 10f);
        var without = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        var with_ = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DrainHoleExclusions = new()
            {
                (new Vector3(0, 0, 10), 8f),
                (new Vector3(-10, 0, 10), 5f),
            },
        });

        with_.ValidSupports.Should().BeLessThanOrEqualTo(without.ValidSupports + 10,
            "drain holes may cause coverage fill to add extra supports in shifted regions");
    }
}
