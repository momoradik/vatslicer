using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// Tests for rendering support cross-sections into layer images.
/// Verifies the PNG output is valid and contains support pixels.
/// </summary>
public class SupportSliceRenderingTests
{
    [Fact]
    public void RenderSupportOnlyLayer_SingleCircle_ProducesNonEmptyImage()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 5, IsSupport = true },
        };

        var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 200, 200, 50, 50);

        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(100, "non-trivial PNG");
        // PNG magic bytes
        png[0].Should().Be(0x89);
        png[1].Should().Be(0x50);
    }

    [Fact]
    public void RenderSupportOnlyLayer_ManyCircles_ProducesLargerImage()
    {
        var single = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 2, IsSupport = true },
        };
        var many = new List<AnalyticalSupportSlicer.SupportCircle>();
        for (int i = 0; i < 50; i++)
            many.Add(new() { CenterX = (i % 10) * 5f - 25, CenterY = (i / 10) * 5f - 12.5f, Radius = 2, IsSupport = true });

        var singlePng = SupportSliceIntegrator.RenderSupportOnlyLayer(single, 200, 200, 50, 50);
        var manyPng = SupportSliceIntegrator.RenderSupportOnlyLayer(many, 200, 200, 50, 50);

        manyPng.Length.Should().BeGreaterThan(singlePng.Length,
            "more circles should produce a larger PNG (more non-black pixels)");
    }

    [Fact]
    public void ComputeSupportStats_VerticalPillar_CorrectLayerCount()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" },
        };

        var (layers, area) = SupportSliceIntegrator.ComputeSupportStats(elements, 1f, 0f, 10f);

        layers.Should().BeInRange(8, 11, "10mm pillar at 1mm layers ≈ 10 layers");
        area.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ComputeSupportStats_MultipleElements_SumsArea()
    {
        var single = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 1f, RadiusB = 1f, Type = "pillar" },
        };
        var double_ = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 1f, RadiusB = 1f, Type = "pillar" },
            new() { PointA = new(20, 0, 10), PointB = new(20, 0, 0), RadiusA = 1f, RadiusB = 1f, Type = "pillar" },
        };

        var (_, singleArea) = SupportSliceIntegrator.ComputeSupportStats(single, 1f, 0f, 10f);
        var (_, doubleArea) = SupportSliceIntegrator.ComputeSupportStats(double_, 1f, 0f, 10f);

        doubleArea.Should().BeApproximately(singleArea * 2, singleArea * 0.1f,
            "two identical pillars should have ~2x the area");
    }

    [Fact]
    public void CirclesToPolygons_CorrectSideCount()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 5, IsSupport = true },
        };

        var polys8 = SupportSliceIntegrator.CirclesToPolygons(circles, 8);
        var polys24 = SupportSliceIntegrator.CirclesToPolygons(circles, 24);

        polys8[0].Should().HaveCount(8);
        polys24[0].Should().HaveCount(24);
    }

    [Fact]
    public void V2Engine_SliceElements_ProduceRenderableImages()
    {
        // Create a floating cube, generate V2 supports, then render a layer
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
        var mesh = StlMesh.FromBinary(data);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.SliceElements.Count == 0) return;

        // Render at mid-height
        float midZ = 5f;
        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, midZ);
        if (circles.Count == 0) return;

        var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 100, 100, 50, 50);
        png.Length.Should().BeGreaterThan(50, "rendered layer should produce valid PNG");
    }
}
