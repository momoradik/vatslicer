using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class FastPathRoutingTests
{
    [Fact]
    public void FastVerticalRoute_ProducesGroundedPath()
    {
        var junction = new Vector3(0, 0, 30);
        var config = new PillarRouter.RoutingConfig
        {
            BaseZ = 0, PillarRadiusMm = 0.5f, BaseRadiusMm = 2f, BaseHeightMm = 0.5f,
        };

        var route = PillarRouter.FastVerticalRoute(junction, 0.5f, config);

        route.ReachesGround.Should().BeTrue();
        route.Path.Should().NotBeEmpty();
        route.Path[0].Type.Should().Be("junction");
        route.Path[^1].Type.Should().Be("base");
        route.Path[^1].Position.Z.Should().BeApproximately(0f, 0.01f, "base should be at Z=0");
    }

    [Fact]
    public void FastVerticalRoute_PathDescendsMonotonically()
    {
        var route = PillarRouter.FastVerticalRoute(
            new Vector3(5, 5, 40), 0.5f,
            new PillarRouter.RoutingConfig { BaseZ = 0 });

        for (int i = 1; i < route.Path.Count; i++)
        {
            route.Path[i].Position.Z.Should().BeLessOrEqualTo(
                route.Path[i - 1].Position.Z + 0.01f,
                "path should descend monotonically");
        }
    }

    [Fact]
    public void FastVerticalRoute_RadiusWidens()
    {
        var config = new PillarRouter.RoutingConfig
        {
            BaseZ = 0, PillarRadiusMm = 0.5f, BaseRadiusMm = 2f,
            WideningFactor = 0.02f, BaseHeightMm = 0.5f,
        };

        var route = PillarRouter.FastVerticalRoute(new Vector3(0, 0, 50), 0.5f, config);

        // The last pillar waypoint before the base should have a wider radius
        var pillarWaypoints = route.Path.Where(wp => wp.Type == "pillar").ToList();
        if (pillarWaypoints.Count >= 2)
        {
            pillarWaypoints[^1].Radius.Should().BeGreaterThan(pillarWaypoints[0].Radius,
                "pillar should widen toward the base");
        }
    }

    [Fact]
    public void FastVerticalRoute_XYStaysConstant()
    {
        float startX = 7.5f, startY = -3.2f;
        var route = PillarRouter.FastVerticalRoute(
            new Vector3(startX, startY, 25), 0.5f,
            new PillarRouter.RoutingConfig { BaseZ = 0 });

        foreach (var wp in route.Path)
        {
            wp.Position.X.Should().BeApproximately(startX, 0.01f, "fast path is purely vertical");
            wp.Position.Y.Should().BeApproximately(startY, 0.01f, "fast path is purely vertical");
        }
    }
}
