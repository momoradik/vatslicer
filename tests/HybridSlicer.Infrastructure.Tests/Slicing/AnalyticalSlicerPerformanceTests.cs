using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class AnalyticalSlicerPerformanceTests
{
    [Fact]
    public void SliceAtZ_5000Elements_Under100ms()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>();
        for (int i = 0; i < 5000; i++)
            elements.Add(new() { PointA = new(i * 0.5f, 0, 50), PointB = new(i * 0.5f, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 25f);
        sw.Stop();

        circles.Count.Should().Be(5000);
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public void SliceAll_100Elements_50Layers_Under500ms()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>();
        for (int i = 0; i < 100; i++)
            elements.Add(new() { PointA = new(i * 2f, 0, 100), PointB = new(i * 2f, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layers = AnalyticalSupportSlicer.SliceAll(elements, 2f, 0f, 100f);
        sw.Stop();

        layers.Count.Should().BeGreaterThan(30);
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public void ExtractElements_100Supports_Under100ms()
    {
        var supports = new List<(PinheadOptimizer.Pinhead, PillarRouter.PillarRoute)>();
        for (int i = 0; i < 100; i++)
        {
            supports.Add((
                new PinheadOptimizer.Pinhead
                {
                    ContactPoint = new(i, 0, 50), Direction = new(0, 0, -1),
                    PinCenter = new(i, 0, 49.8f), BackCenter = new(i, 0, 49.3f),
                    JunctionPoint = new(i, 0, 48.8f),
                    PinRadius = 0.2f, BackRadius = 0.5f, Width = 1f,
                    Clearance = 10f, IsValid = true, NeedsAnchor = false,
                },
                new PillarRouter.PillarRoute
                {
                    Path = new()
                    {
                        new() { Position = new(i, 0, 48.8f), Radius = 0.5f, Type = "junction" },
                        new() { Position = new(i, 0, 0), Radius = 1f, Type = "base" },
                    },
                    ReachesGround = true, TotalLength = 48.8f,
                }
            ));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var elements = AnalyticalSupportSlicer.ExtractElements(supports, new());
        sw.Stop();

        elements.Count.Should().BeGreaterThan(200);
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }
}
