using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests.Validation;

public class CollisionValidatorTests
{
    private static AabbBvh CreateCubeBvh(float size = 10f)
    {
        var o = Vector3.Zero;
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts[vi++] = a + o; verts[vi++] = b + o; verts[vi++] = c + o;
            verts[vi++] = a + o; verts[vi++] = c + o; verts[vi++] = d + o;
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
    public void ValidatePillarRoutes_ClearPath_NoIssues()
    {
        var bvh = CreateCubeBvh(10f);

        // Route that goes alongside the cube (not through it)
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(15, 5, 20), Radius = 0.5f, Type = "junction" },
                new() { Position = new Vector3(15, 5, 0), Radius = 0.5f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 20,
        };

        var issues = CollisionValidator.ValidatePillarRoutes(
            new() { ("s1", route) }, bvh, 4);

        issues.Should().BeEmpty("route beside the cube should be clear");
    }

    [Fact]
    public void ValidatePillarRoutes_ThroughMesh_DetectsCollision()
    {
        var bvh = CreateCubeBvh(10f);

        // Route that goes through the cube interior
        var route = new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new Vector3(5, 5, 15), Radius = 0.5f, Type = "junction" },
                new() { Position = new Vector3(5, 5, 5), Radius = 0.5f, Type = "pillar" }, // inside cube
                new() { Position = new Vector3(5, 5, -5), Radius = 0.5f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 20,
        };

        var issues = CollisionValidator.ValidatePillarRoutes(
            new() { ("s1", route) }, bvh, 4);

        issues.Should().NotBeEmpty("route through cube should detect collision");
    }

    [Fact]
    public void ValidateInterconnections_ClearBrace_NoIssue()
    {
        var bvh = CreateCubeBvh(10f);

        var connections = new List<InterconnectBuilder.Interconnection>
        {
            new()
            {
                PillarA = 0, PillarB = 1,
                PointA = new Vector3(-5, 5, 5), PointB = new Vector3(-2, 5, 5),
                Radius = 0.3f, Type = "horizontal",
            }
        };

        var issues = CollisionValidator.ValidateInterconnections(connections, bvh, 4);
        issues.Should().BeEmpty("brace outside the cube is clear");
    }

    [Fact]
    public void ValidateAll_EmptyInputs_NoIssues()
    {
        var bvh = CreateCubeBvh(10f);

        var result = CollisionValidator.ValidateAll(
            new(), new(), new(), bvh);

        result.TotalSupportsChecked.Should().Be(0);
        result.CollidingSupports.Should().Be(0);
        result.Issues.Should().BeEmpty();
    }
}
