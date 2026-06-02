using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

/// <summary>
/// Tests for pillar routing anchor-on-model behavior.
/// </summary>
public class PillarRouterAnchorTests
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
        for (int t = 0; t < triCount; t++) {
            off += 12;
            for (int v = 0; v < 3; v++) {
                BitConverter.GetBytes(verts[t*3+v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t*3+v].Y).CopyTo(data, off+4);
                BitConverter.GetBytes(verts[t*3+v].Z).CopyTo(data, off+8);
                off += 12;
            }
            off += 2;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void Route_AboveObstacle_MayAnchor()
    {
        // Obstacle cube blocks direct descent
        var bvh = BuildCubeBvh(new Vector3(0, 0, 10), 15f);

        var route = PillarRouter.Route(
            new Vector3(0, 0, 25), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.Should().NotBeNull();
        route.Path.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Route_AllPathsHaveType()
    {
        var bvh = BuildCubeBvh(new Vector3(100, 100, 100), 5f);
        var route = PillarRouter.Route(new Vector3(0, 0, 50), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        foreach (var wp in route.Path)
            wp.Type.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Route_BaseTypeAtEnd()
    {
        var bvh = BuildCubeBvh(new Vector3(100, 100, 100), 5f);
        var route = PillarRouter.Route(new Vector3(0, 0, 50), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        if (route.ReachesGround)
            route.Path[^1].Type.Should().Be("base");
    }

    [Fact]
    public void Route_JunctionTypeAtStart()
    {
        var bvh = BuildCubeBvh(new Vector3(100, 100, 100), 5f);
        var route = PillarRouter.Route(new Vector3(0, 0, 50), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.Path[0].Type.Should().Be("junction");
    }
}
