using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class SupportSliceIntegratorEdgeCaseTests
{
    [Fact]
    public void RenderSupportOnlyLayer_HighResolution_ValidPng()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 10, IsSupport = true },
        };
        var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 1920, 1080, 192, 120);
        png[0].Should().Be(0x89); // PNG magic
        png.Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void RenderSupportOnlyLayer_LowResolution_ValidPng()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 5, IsSupport = true },
        };
        var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 10, 10, 20, 20);
        png[0].Should().Be(0x89);
    }

    [Fact]
    public void CirclesToPolygons_DefaultSides_Is16()
    {
        var circles = new List<AnalyticalSupportSlicer.SupportCircle>
        {
            new() { CenterX = 0, CenterY = 0, Radius = 5, IsSupport = true },
        };
        var polys = SupportSliceIntegrator.CirclesToPolygons(circles);
        polys[0].Should().HaveCount(16);
    }

    [Fact]
    public void ComputeSupportStats_EmptyElements_ZeroCounts()
    {
        var (layers, area) = SupportSliceIntegrator.ComputeSupportStats(new(), 1f, 0f, 10f);
        layers.Should().Be(0);
        area.Should().Be(0);
    }

    [Fact]
    public void ComputeSupportStats_LargerLayerHeight_FewerLayers()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 1f, RadiusB = 1f, Type = "pillar" },
        };

        var (fine, _) = SupportSliceIntegrator.ComputeSupportStats(elements, 0.5f, 0f, 10f);
        var (coarse, _) = SupportSliceIntegrator.ComputeSupportStats(elements, 2f, 0f, 10f);

        fine.Should().BeGreaterThan(coarse, "finer layers = more count");
    }
}
