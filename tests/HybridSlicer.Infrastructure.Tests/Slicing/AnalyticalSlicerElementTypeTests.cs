using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// Tests verifying every support element type is correctly sliced.
/// </summary>
public class AnalyticalSlicerElementTypeTests
{
    [Theory]
    [InlineData("pillar")]
    [InlineData("pinhead")]
    [InlineData("bridge")]
    [InlineData("base")]
    [InlineData("junction")]
    [InlineData("anchor")]
    [InlineData("interconnect")]
    public void SliceAtZ_AllTypes_ProduceCircles(string type)
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 10),
                PointB = new Vector3(0, 0, 0),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = type,
            }
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);
        circles.Should().NotBeEmpty($"type '{type}' should produce circles at mid-height");
        circles[0].IsSupport.Should().BeTrue();
    }

    [Fact]
    public void SliceAtZ_MixedTypes_AllProduceCircles()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" },
            new() { PointA = new(5, 0, 10), PointB = new(5, 0, 0), RadiusA = 0.3f, RadiusB = 0.3f, Type = "pinhead" },
            new() { PointA = new(10, 0, 5), PointB = new(15, 0, 0), RadiusA = 0.4f, RadiusB = 0.4f, Type = "bridge" },
            new() { PointA = new(20, 0, 2), PointB = new(20, 0, 0), RadiusA = 1f, RadiusB = 2f, Type = "base" },
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 1f);
        circles.Count.Should().BeGreaterThanOrEqualTo(2, "multiple elements should produce multiple circles");
    }

    [Fact]
    public void ExtractElements_FromPinheadAndRoute_AllTypesPresent()
    {
        // Create minimal pinhead and route data
        var pinhead = new Infrastructure.Resin.Routing.PinheadOptimizer.Pinhead
        {
            ContactPoint = new(0, 0, 10),
            Direction = new(0, 0, -1),
            PinCenter = new(0, 0, 9.8f),
            BackCenter = new(0, 0, 9.3f),
            JunctionPoint = new(0, 0, 8.8f),
            PinRadius = 0.2f, BackRadius = 0.5f, Width = 1f,
            Clearance = 10f, IsValid = true, NeedsAnchor = false,
        };

        var route = new Infrastructure.Resin.Routing.PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new(0, 0, 8.8f), Radius = 0.5f, Type = "junction" },
                new() { Position = new(0, 0, 5f), Radius = 0.6f, Type = "pillar" },
                new() { Position = new(0, 0, 0), Radius = 2f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 8.8f,
        };

        var interconnections = new List<Infrastructure.Resin.Routing.InterconnectBuilder.Interconnection>
        {
            new() { PillarA = 0, PillarB = 1, PointA = new(0, 0, 5), PointB = new(3, 0, 5), Radius = 0.3f, Type = "horizontal" },
        };

        var elements = AnalyticalSupportSlicer.ExtractElements(
            new() { (pinhead, route) }, interconnections);

        elements.Should().NotBeEmpty();
        var types = elements.Select(e => e.Type).Distinct().ToList();
        types.Should().Contain("pinhead");
        types.Should().Contain("interconnect");
    }

    [Fact]
    public void SliceAll_ProducesLayersForEntireHeight()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 20), PointB = new(0, 0, 0), RadiusA = 0.5f, RadiusB = 1f, Type = "pillar" },
        };

        var layers = AnalyticalSupportSlicer.SliceAll(elements, 2f, 0f, 20f);
        layers.Should().HaveCountGreaterThan(5);

        // Each layer should have at least one circle
        foreach (var layer in layers)
        {
            layer.Circles.Should().NotBeEmpty($"layer at Z={layer.Z} should have circles");
            layer.Z.Should().BeInRange(0f, 21f);
        }
    }
}
