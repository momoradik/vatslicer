using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests.Validation;

public class StructuralValidatorTests
{
    [Fact]
    public void Validate_ShortThickSupport_PassesBuckling()
    {
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(0, 0, 10), Radius = 1.0f, Type = "junction" },
                new() { Position = new Vector3(0, 0, 5), Radius = 1.0f, Type = "pillar" },
                new() { Position = new Vector3(0, 0, 0), Radius = 2.0f, Type = "base" },
            },
            ReachesGround = true,
            TotalLength = 10,
        };

        var supports = new List<(string id, PillarRouter.PillarRoute route, float contactZ)>
        {
            ("s1", route, 10f),
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 10), "s1");

        var result = StructuralValidator.Validate(supports, new(), grid, null, 2.0f);

        result.PassedBuckling.Should().Be(1);
        result.FailedBuckling.Should().Be(0);
    }

    [Fact]
    public void Validate_TallThinSupport_FailsBuckling()
    {
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(0, 0, 200), Radius = 0.1f, Type = "junction" },
                new() { Position = new Vector3(0, 0, 100), Radius = 0.1f, Type = "pillar" },
                new() { Position = new Vector3(0, 0, 0), Radius = 0.5f, Type = "base" },
            },
            ReachesGround = true,
            TotalLength = 200,
        };

        var supports = new List<(string id, PillarRouter.PillarRoute route, float contactZ)>
        {
            ("s1", route, 200f),
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 200), "s1");

        var result = StructuralValidator.Validate(supports, new(), grid, null, 2.0f);

        result.FailedBuckling.Should().BeGreaterThan(0, "very thin 200mm pillar should fail buckling");
    }

    [Fact]
    public void Validate_UncoveredOverhang_ReportsIssue()
    {
        var supports = new List<(string id, PillarRouter.PillarRoute route, float contactZ)>();
        var grid = new SpatialGrid<string>(5f);

        var overhangs = new List<OverhangAnalyzer.OverhangRegion>
        {
            new()
            {
                Contour = new() { new(0, 0), new(10, 0), new(10, 10), new(0, 10) },
                Z = 50f, Area = 100f, Centroid = new(5, 5),
                Type = OverhangAnalyzer.OverhangType.BulkOverhang, Priority = 0.5f,
            }
        };

        var result = StructuralValidator.Validate(supports, overhangs, grid, null, 2.0f);

        result.OverhangRegionsUncovered.Should().Be(1, "overhang with no nearby support is uncovered");
        result.Issues.Should().Contain(i => i.Category == "coverage");
    }

    [Fact]
    public void Validate_CoveredOverhang_NoCoverageIssue()
    {
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(5, 5, 50), Radius = 0.5f, Type = "junction" },
                new() { Position = new Vector3(5, 5, 0), Radius = 1.0f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 50,
        };

        var supports = new List<(string id, PillarRouter.PillarRoute route, float contactZ)>
        {
            ("s1", route, 50f),
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(5, 5, 50), "s1");

        var overhangs = new List<OverhangAnalyzer.OverhangRegion>
        {
            new()
            {
                Contour = new() { new(0, 0), new(10, 0), new(10, 10), new(0, 10) },
                Z = 50f, Area = 100f, Centroid = new(5, 5),
                Type = OverhangAnalyzer.OverhangType.BulkOverhang, Priority = 0.5f,
            }
        };

        var result = StructuralValidator.Validate(supports, overhangs, grid, null, 2.0f);

        result.OverhangRegionsCovered.Should().Be(1);
        result.OverhangRegionsUncovered.Should().Be(0);
    }
}
