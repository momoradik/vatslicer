using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class PolygonCrossSectionTests
{
    [Fact]
    public void GeneratePolygonVertices_Square_Returns4Vertices()
    {
        var verts = AnalyticalSupportSlicer.GeneratePolygonVertices(0, 0, 1.0f, 4);
        verts.Length.Should().Be(4);

        // All vertices should be at radius 1.0 from center
        foreach (var v in verts)
        {
            float dist = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            dist.Should().BeApproximately(1.0f, 0.001f);
        }
    }

    [Fact]
    public void GeneratePolygonVertices_Octagon_Returns8Vertices()
    {
        var verts = AnalyticalSupportSlicer.GeneratePolygonVertices(5, 3, 2.0f, 8);
        verts.Length.Should().Be(8);

        foreach (var v in verts)
        {
            float dx = v.X - 5f;
            float dy = v.Y - 3f;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            dist.Should().BeApproximately(2.0f, 0.001f);
        }
    }

    [Fact]
    public void GeneratePolygonVertices_WithRotation_ShiftsVertices()
    {
        var noRotation = AnalyticalSupportSlicer.GeneratePolygonVertices(0, 0, 1.0f, 4, 0);
        var withRotation = AnalyticalSupportSlicer.GeneratePolygonVertices(0, 0, 1.0f, 4, MathF.PI / 4);

        // Rotated square should have different vertex positions
        noRotation[0].X.Should().NotBeApproximately(withRotation[0].X, 0.01f);
    }

    [Fact]
    public void SliceAtZFull_CircularElement_ProducesCircle()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 10),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar", Sides = 0,
            },
        };

        var (circles, polygons) = AnalyticalSupportSlicer.SliceAtZFull(elements, 5f);
        circles.Should().HaveCount(1);
        polygons.Should().BeEmpty();
        circles[0].Radius.Should().BeApproximately(0.5f, 0.01f);
    }

    [Fact]
    public void SliceAtZFull_CubeElement_ProducesPolygon()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 10),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar", Sides = 4,
            },
        };

        var (circles, polygons) = AnalyticalSupportSlicer.SliceAtZFull(elements, 5f);
        circles.Should().BeEmpty();
        polygons.Should().HaveCount(1);
        polygons[0].Vertices.Length.Should().Be(4);
    }

    [Fact]
    public void SliceAtZFull_CrossElement_ProducesOctagon()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 10),
                RadiusA = 1.0f, RadiusB = 1.0f,
                Type = "pillar", Sides = 8,
            },
        };

        var (circles, polygons) = AnalyticalSupportSlicer.SliceAtZFull(elements, 5f);
        circles.Should().BeEmpty();
        polygons.Should().HaveCount(1);
        polygons[0].Vertices.Length.Should().Be(8);
    }

    [Fact]
    public void SliceAtZFull_TaperedSquare_InterpolatesRadius()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 10),
                RadiusA = 1.0f, RadiusB = 2.0f,
                Type = "pillar", Sides = 4,
            },
        };

        var (_, polygons) = AnalyticalSupportSlicer.SliceAtZFull(elements, 5f);
        polygons.Should().HaveCount(1);

        // At z=5 (midpoint), radius should be 1.5
        float dist = MathF.Sqrt(polygons[0].Vertices[0].X * polygons[0].Vertices[0].X +
                                polygons[0].Vertices[0].Y * polygons[0].Vertices[0].Y);
        dist.Should().BeApproximately(1.5f, 0.01f);
    }

    [Fact]
    public void SliceAtZ_StillWorksForCircles()
    {
        // Existing SliceAtZ should still work unchanged for circular elements
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 10),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar", Sides = 0,
            },
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);
        circles.Should().HaveCount(1);
    }

    [Fact]
    public void SliceAtZFull_OutOfRange_ReturnsEmpty()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 10),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar", Sides = 4,
            },
        };

        var (circles, polygons) = AnalyticalSupportSlicer.SliceAtZFull(elements, 15f);
        circles.Should().BeEmpty();
        polygons.Should().BeEmpty();
    }

    [Fact]
    public void SliceAtZFull_RaftElement_ProducesCircle()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 0),
                PointB = new Vector3(0, 0, 0.3f),
                RadiusA = 3.0f, RadiusB = 3.0f,
                Type = "raft", Sides = 0,
            },
        };

        var (circles, polygons) = AnalyticalSupportSlicer.SliceAtZFull(elements, 0.15f);
        circles.Should().HaveCount(1);
        circles[0].Radius.Should().BeApproximately(3.0f, 0.1f);
    }
}
