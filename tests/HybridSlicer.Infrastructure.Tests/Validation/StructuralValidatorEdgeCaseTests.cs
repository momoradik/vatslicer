using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests.Validation;

public class StructuralValidatorEdgeCaseTests
{
    [Fact]
    public void Validate_EmptySupports_NoIssues()
    {
        var result = StructuralValidator.Validate(
            new(), new(), new SpatialGrid<string>(5f), null, 2.0f);

        result.TotalSupports.Should().Be(0);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_VeryShortSupport_PassesEverything()
    {
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(0, 0, 2), Radius = 1f, Type = "junction" },
                new() { Position = new Vector3(0, 0, 0), Radius = 2f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 2,
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 2), "s1");

        var result = StructuralValidator.Validate(
            new() { ("s1", route, 2f) }, new(), grid, null, 2.0f);

        result.FailedBuckling.Should().Be(0, "2mm support can't buckle");
        result.FailedTensile.Should().Be(0, "2mm support easily resists tension");
    }

    [Fact]
    public void Validate_HighSafetyFactor_RequiresThickerPillars()
    {
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(0, 0, 100), Radius = 0.3f, Type = "junction" },
                new() { Position = new Vector3(0, 0, 50), Radius = 0.3f, Type = "pillar" },
                new() { Position = new Vector3(0, 0, 0), Radius = 1f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 100,
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 100), "s1");

        var lowSF = StructuralValidator.Validate(
            new() { ("s1", route, 100f) }, new(), grid, null, 1.0f);
        var highSF = StructuralValidator.Validate(
            new() { ("s1", route, 100f) }, new(), grid, null, 5.0f);

        // Higher safety factor requirement should produce more failures
        highSF.FailedBuckling.Should().BeGreaterThanOrEqualTo(lowSF.FailedBuckling);
    }

    [Fact]
    public void Validate_CoverageCheck_DetectsGaps()
    {
        var grid = new SpatialGrid<string>(5f);
        // No supports anywhere

        var regions = new List<OverhangAnalyzer.OverhangRegion>
        {
            new()
            {
                Contour = new() { new(0,0), new(10,0), new(10,10), new(0,10) },
                Z = 50f, Area = 100f, Centroid = new(5, 5),
                Type = OverhangAnalyzer.OverhangType.NewIsland, Priority = 1f,
            },
            new()
            {
                Contour = new() { new(50,50), new(60,50), new(60,60), new(50,60) },
                Z = 30f, Area = 100f, Centroid = new(55, 55),
                Type = OverhangAnalyzer.OverhangType.BulkOverhang, Priority = 0.5f,
            },
        };

        var result = StructuralValidator.Validate(new(), regions, grid, null, 2.0f);
        result.OverhangRegionsUncovered.Should().Be(2, "both regions have no nearby supports");
    }

    [Fact]
    public void Validate_ManifoldCheck_DetectsNonManifold()
    {
        // Create a mesh with intentionally non-manifold edges
        var mesh = new Infrastructure.Resin.Meshing.IndexedTriangleSet();
        // Single triangle — has 3 boundary (non-manifold) edges
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        var result = StructuralValidator.Validate(new(), new(), new SpatialGrid<string>(5f), mesh, 2.0f);
        result.ManifoldErrors.Should().BeGreaterThan(0, "single triangle has boundary edges");
    }

    [Fact]
    public void Validate_MinSafetyFactor_IsLowestAcrossSupports()
    {
        var thin = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(0, 0, 100), Radius = 0.1f, Type = "junction" },
                new() { Position = new Vector3(0, 0, 0), Radius = 0.5f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 100,
        };
        var thick = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(10, 0, 10), Radius = 2f, Type = "junction" },
                new() { Position = new Vector3(10, 0, 0), Radius = 3f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 10,
        };

        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 100), "thin");
        grid.Insert(new Vector3(10, 0, 10), "thick");

        var result = StructuralValidator.Validate(
            new() { ("thin", thin, 100f), ("thick", thick, 10f) },
            new(), grid, null, 2.0f);

        result.MinSafetyFactor.Should().BeLessThan(result.AvgSafetyFactor,
            "min SF should be less than average when supports differ");
    }
}
