using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class AnalyticalSlicerTests
{
    [Fact]
    public void SliceAtZ_VerticalPillar_ProducesCircle()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(10, 20, 50),
                PointB = new Vector3(10, 20, 0),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar",
            }
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 25f);

        circles.Should().HaveCount(1);
        circles[0].CenterX.Should().BeApproximately(10f, 0.1f);
        circles[0].CenterY.Should().BeApproximately(20f, 0.1f);
        circles[0].Radius.Should().BeApproximately(0.5f, 0.1f);
    }

    [Fact]
    public void SliceAtZ_OutsideRange_ReturnsEmpty()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 10),
                PointB = new Vector3(0, 0, 20),
                RadiusA = 1f, RadiusB = 1f,
                Type = "pillar",
            }
        };

        AnalyticalSupportSlicer.SliceAtZ(elements, 5f).Should().BeEmpty();
        AnalyticalSupportSlicer.SliceAtZ(elements, 25f).Should().BeEmpty();
    }

    [Fact]
    public void SliceAtZ_TaperedPillar_InterpolatesRadius()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 20),
                PointB = new Vector3(0, 0, 0),
                RadiusA = 0.5f, RadiusB = 2.0f, // widens toward base
                Type = "pillar",
            }
        };

        var mid = AnalyticalSupportSlicer.SliceAtZ(elements, 10f);
        mid.Should().HaveCount(1);
        // At midpoint (t=0.5), radius should be 0.5 + 0.5*(2.0-0.5) = 1.25
        mid[0].Radius.Should().BeApproximately(1.25f, 0.2f);
    }

    [Fact]
    public void SliceAll_ProducesMultipleLayers()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 10),
                PointB = new Vector3(0, 0, 0),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar",
            }
        };

        var layers = AnalyticalSupportSlicer.SliceAll(elements, 1f, 0f, 10f);

        layers.Should().HaveCountGreaterThan(5);
        layers.All(l => l.Circles.Count > 0).Should().BeTrue();
    }

    [Fact]
    public void SliceAtZ_MultipleElements_ReturnsAll()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" },
            new() { PointA = new(20, 0, 10), PointB = new(20, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" },
            new() { PointA = new(40, 0, 10), PointB = new(40, 0, 0), RadiusA = 0.5f, RadiusB = 0.5f, Type = "pillar" },
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);
        circles.Should().HaveCount(3, "all three pillars span Z=5");
    }

    [Fact]
    public void SliceAtZ_AllCirclesMarkedAsSupport()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new() { PointA = new(0, 0, 10), PointB = new(0, 0, 0), RadiusA = 1f, RadiusB = 1f, Type = "pillar" },
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);
        circles.All(c => c.IsSupport).Should().BeTrue("support circles must be marked");
    }
}
