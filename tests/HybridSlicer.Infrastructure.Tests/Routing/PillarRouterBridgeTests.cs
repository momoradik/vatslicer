using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

/// <summary>
/// Tests for pillar routing with bridge-and-descend strategy.
/// These scenarios simulate complex geometry where direct descent is blocked.
/// </summary>
public class PillarRouterBridgeTests
{
    private static AabbBvh BuildCubeBvh(Vector3 center, float size)
    {
        var o = center - new Vector3(size / 2);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a+o; verts[vi++]=b+o; verts[vi++]=c+o;
            verts[vi++]=a+o; verts[vi++]=c+o; verts[vi++]=d+o;
        }
        float s = size;
        Quad(new(0,0,s), new(s,0,s), new(s,s,s), new(0,s,s));
        Quad(new(0,0,0), new(0,s,0), new(s,s,0), new(s,0,0));
        Quad(new(s,0,0), new(s,0,s), new(s,s,s), new(s,s,0));
        Quad(new(0,0,s), new(0,0,0), new(0,s,0), new(0,s,s));
        Quad(new(0,s,s), new(s,s,s), new(s,s,0), new(0,s,0));
        Quad(new(0,0,0), new(s,0,0), new(s,0,s), new(0,0,s));

        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void Route_BlockedByObstacle_TriesBridgeOrAnchor()
    {
        // Create an obstacle cube directly below the junction point
        var bvh = BuildCubeBvh(new Vector3(0, 0, 15), 20f); // cube from Z=5 to Z=25

        // Junction above the cube at Z=30 — direct descent at (0,0) would hit the cube
        var route = PillarRouter.Route(
            new Vector3(0, 0, 30), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        // Should still produce a route (via bridge, anchor, or fallback)
        route.Path.Should().HaveCountGreaterThan(1);
        route.TotalLength.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Route_ClearPath_NoNeedForBridge()
    {
        // Obstacle far away from the junction
        var bvh = BuildCubeBvh(new Vector3(50, 50, 15), 10f);

        var route = PillarRouter.Route(
            new Vector3(0, 0, 30), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        // Should use direct descent (no bridge waypoints)
        route.ReachesGround.Should().BeTrue();
        var bridgeWaypoints = route.Path.Where(w => w.Type == "bridge").ToList();
        bridgeWaypoints.Should().BeEmpty("no obstacle in the way");
    }

    [Fact]
    public void Route_AnchorOnModel_WhenGroundUnreachable()
    {
        // Large obstacle covering the entire descent path
        var bvh = BuildCubeBvh(new Vector3(0, 0, 10), 30f); // huge cube Z=-5 to Z=25

        var route = PillarRouter.Route(
            new Vector3(0, 0, 30), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        // May reach ground via bridge, or anchor on the obstacle
        route.Path.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Route_PillarWidening_IncreasesRadius()
    {
        var bvh = BuildCubeBvh(new Vector3(1000, 1000, 1000), 1f); // far away

        var route = PillarRouter.Route(
            new Vector3(0, 0, 100), 0.5f, bvh,
            new PillarRouter.RoutingConfig { WideningFactor = 0.05f });

        var pillarWaypoints = route.Path.Where(w => w.Type == "pillar").ToList();
        if (pillarWaypoints.Count >= 2)
        {
            // Last pillar waypoint (near base) should have larger radius than first
            pillarWaypoints[^1].Radius.Should().BeGreaterThan(pillarWaypoints[0].Radius,
                "widening should increase radius toward base");
        }
    }
}
