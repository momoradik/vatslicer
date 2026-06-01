using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class SupportSliceIntegratorTests
{
    [Fact]
    public void CirclesToPolygons_ProducesCorrectPolygons()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 10, CenterY = 20, Radius = 5, IsSupport = true },
            new() { CenterX = 50, CenterY = 30, Radius = 3, IsSupport = true },
        };

        var polygons = SupportSliceIntegrator.CirclesToPolygons(circles, 12);

        polygons.Should().HaveCount(2);
        polygons[0].Should().HaveCount(12, "12-sided polygon");
        polygons[1].Should().HaveCount(12);

        // First polygon should be centered around (10, 20) with radius 5
        var center = new Vector2(
            polygons[0].Average(p => p.X),
            polygons[0].Average(p => p.Y));
        center.X.Should().BeApproximately(10, 0.5f);
        center.Y.Should().BeApproximately(20, 0.5f);
    }

    [Fact]
    public void CirclesToPolygons_SkipsTiny()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 0.005f, IsSupport = true }, // too small
            new() { CenterX = 10, CenterY = 10, Radius = 1f, IsSupport = true },
        };

        var polygons = SupportSliceIntegrator.CirclesToPolygons(circles);
        polygons.Should().HaveCount(1, "tiny circle should be skipped");
    }

    [Fact]
    public void RenderSupportOnlyLayer_ProducesPng()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 5, IsSupport = true },
        };

        var pngBytes = SupportSliceIntegrator.RenderSupportOnlyLayer(
            circles, 100, 100, 50, 50);

        pngBytes.Should().NotBeNull();
        pngBytes.Length.Should().BeGreaterThan(0, "should produce a PNG image");
        // Check PNG magic bytes
        pngBytes[0].Should().Be(0x89, "PNG signature");
        pngBytes[1].Should().Be(0x50); // 'P'
        pngBytes[2].Should().Be(0x4E); // 'N'
        pngBytes[3].Should().Be(0x47); // 'G'
    }

    [Fact]
    public void ComputeSupportStats_ReturnsCorrectCounts()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" },
        };

        var (layers, area) = SupportSliceIntegrator.ComputeSupportStats(elements, 1f, 0f, 10f);

        layers.Should().BeGreaterThan(0, "support spans multiple layers");
        area.Should().BeGreaterThan(0, "support has cross-section area");
    }

    [Fact]
    public void RenderSupportOnlyLayer_EmptyCircles_ProducesBlackImage()
    {
        var pngBytes = SupportSliceIntegrator.RenderSupportOnlyLayer(
            new(), 50, 50, 25, 25);

        pngBytes.Should().NotBeNull();
        pngBytes.Length.Should().BeGreaterThan(0);
    }
}
