using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class PillarRouterTests
{
    private static AabbBvh CreateEmptyBvh()
    {
        // Minimal mesh (single tiny triangle far away — BVH needs at least 1 triangle)
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12; // skip normal
        // Triangle at (-1000, -1000, -1000)
        for (int v = 0; v < 3; v++)
        {
            BitConverter.GetBytes(-1000f + v).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(-1000f).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(-1000f).CopyTo(data, off); off += 4;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void Route_ClearPath_DirectDescent()
    {
        var bvh = CreateEmptyBvh();
        var route = PillarRouter.Route(
            new Vector3(0, 0, 50), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.ReachesGround.Should().BeTrue();
        route.Path.Should().HaveCountGreaterThan(1);
        route.Path[0].Type.Should().Be("junction");
        route.Path[^1].Type.Should().Be("base");
        route.Path[^1].Position.Z.Should().BeApproximately(0, 0.5f);
    }

    [Fact]
    public void Route_PillarWidens()
    {
        var bvh = CreateEmptyBvh();
        var route = PillarRouter.Route(
            new Vector3(0, 0, 100), 0.5f, bvh,
            new PillarRouter.RoutingConfig { WideningFactor = 0.02f });

        var pillarWaypoints = route.Path.Where(w => w.Type == "pillar").ToList();
        if (pillarWaypoints.Count >= 2)
        {
            // Later (lower) waypoints should have larger radius due to widening
            pillarWaypoints[^1].Radius.Should().BeGreaterThan(pillarWaypoints[0].Radius,
                "pillar should widen toward base");
        }
    }

    [Fact]
    public void Route_ShortPillar_StillProducesPath()
    {
        var bvh = CreateEmptyBvh();
        var route = PillarRouter.Route(
            new Vector3(0, 0, 2), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.Path.Should().HaveCountGreaterThan(0);
        route.ReachesGround.Should().BeTrue();
    }

    [Fact]
    public void Route_BaseRadiusLargerThanPillar()
    {
        var bvh = CreateEmptyBvh();
        var route = PillarRouter.Route(
            new Vector3(0, 0, 30), 0.5f, bvh,
            new PillarRouter.RoutingConfig { BaseRadiusMm = 3.0f });

        var basePt = route.Path.FirstOrDefault(w => w.Type == "base");
        basePt.Should().NotBeNull();
        basePt!.Radius.Should().BeGreaterThan(0.5f, "base should be wider than pillar");
    }

    [Fact]
    public void Route_TotalLengthMatchesPath()
    {
        var bvh = CreateEmptyBvh();
        var route = PillarRouter.Route(
            new Vector3(0, 0, 50), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        float computedLength = 0;
        for (int i = 1; i < route.Path.Count; i++)
            computedLength += Vector3.Distance(route.Path[i - 1].Position, route.Path[i].Position);

        route.TotalLength.Should().BeApproximately(computedLength, 0.1f);
    }

    [Fact]
    public void Route_AllWaypointsHavePositiveRadius()
    {
        var bvh = CreateEmptyBvh();
        var route = PillarRouter.Route(
            new Vector3(10, 20, 80), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        foreach (var wp in route.Path)
            wp.Radius.Should().BeGreaterThan(0, $"waypoint {wp.Type} at Z={wp.Position.Z} needs positive radius");
    }
}
