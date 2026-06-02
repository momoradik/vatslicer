using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests.Validation;

public class StructuralValidatorComprehensiveTests
{
    private static PillarRouter.PillarRoute MakeRoute(float topZ, float radius)
    {
        return new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(0, 0, topZ), Radius = radius, Type = "junction" },
                new() { Position = new Vector3(0, 0, topZ / 2), Radius = radius * 1.2f, Type = "pillar" },
                new() { Position = new Vector3(0, 0, 0), Radius = radius * 2, Type = "base" },
            },
            ReachesGround = true, TotalLength = topZ,
        };
    }

    [Fact]
    public void Validate_MultipleSupports_IndependentSafetyFactors()
    {
        var supports = new List<(string, PillarRouter.PillarRoute, float)>
        {
            ("thin", MakeRoute(100, 0.15f), 100f),
            ("thick", MakeRoute(10, 2f), 10f),
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 100), "thin");
        grid.Insert(new Vector3(10, 0, 10), "thick");

        var result = StructuralValidator.Validate(supports, new(), grid, null, 2f);

        result.TotalSupports.Should().Be(2);
        result.MinSafetyFactor.Should().BeLessThan(result.AvgSafetyFactor);
    }

    [Fact]
    public void Validate_AllShortThick_AllPass()
    {
        var supports = Enumerable.Range(0, 10).Select(i =>
            ($"s{i}", MakeRoute(5, 2f), 5f)).ToList();

        var grid = new SpatialGrid<string>(5f);
        foreach (var (id, _, _) in supports)
            grid.Insert(new Vector3(0, 0, 5), id);

        var result = StructuralValidator.Validate(supports, new(), grid, null, 2f);

        result.FailedBuckling.Should().Be(0);
        result.FailedTensile.Should().Be(0);
    }

    [Fact]
    public void Validate_CoverageWithMultipleRegions_AllCovered()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(5, 5, 50), "s1");
        grid.Insert(new Vector3(55, 55, 30), "s2");

        var regions = new List<OverhangAnalyzer.OverhangRegion>
        {
            new() { Contour = new() { new(0,0), new(10,0), new(10,10), new(0,10) },
                Z = 50f, Area = 100f, Centroid = new(5, 5),
                Type = OverhangAnalyzer.OverhangType.BulkOverhang, Priority = 0.5f },
            new() { Contour = new() { new(50,50), new(60,50), new(60,60), new(50,60) },
                Z = 30f, Area = 100f, Centroid = new(55, 55),
                Type = OverhangAnalyzer.OverhangType.NewIsland, Priority = 1f },
        };

        var result = StructuralValidator.Validate(new(), regions, grid, null, 2f);
        result.OverhangRegionsCovered.Should().Be(2);
        result.OverhangRegionsUncovered.Should().Be(0);
    }

    [Fact]
    public void Validate_ElapsedMs_Tracked()
    {
        var result = StructuralValidator.Validate(new(), new(), new SpatialGrid<string>(5f), null, 2f);
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Validate_IssueDescriptions_NotEmpty()
    {
        var supports = new List<(string, PillarRouter.PillarRoute, float)>
        {
            ("thin", MakeRoute(200, 0.1f), 200f),
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 200), "thin");

        var result = StructuralValidator.Validate(supports, new(), grid, null, 2f);

        if (result.Issues.Count > 0)
        {
            foreach (var issue in result.Issues)
                issue.Description.Should().NotBeNullOrEmpty();
        }
    }
}
