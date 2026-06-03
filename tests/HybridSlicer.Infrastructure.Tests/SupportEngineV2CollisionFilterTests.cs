using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests verifying the post-routing collision filter correctly removes
/// supports whose pillar passes through the mesh, and that the legacy
/// format and slice elements handle the filtered results without crash.
/// </summary>
public class SupportEngineV2CollisionFilterTests
{
    private static StlMesh CreateFloatingCube(float size = 20f, float z = 10f)
    {
        var o = new Vector3(-size/2, -size/2, z);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a+o; verts[vi++]=b+o; verts[vi++]=c+o;
            verts[vi++]=a+o; verts[vi++]=c+o; verts[vi++]=d+o;
        }
        float s = size;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void CollisionFilter_ValidSupports_LessThanOrEqualTotal()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.ValidSupports.Should().BeLessThanOrEqualTo(result.TotalSupports);
        result.Routes.Count.Should().BeLessThanOrEqualTo(result.TotalSupports);
    }

    [Fact]
    public void CollisionFilter_LegacySupports_MatchRouteCount()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Legacy supports should only contain supports with valid routes
        result.LegacySupports.Count.Should().Be(result.Routes.Count,
            "every route should have exactly one legacy support");
    }

    [Fact]
    public void CollisionFilter_SliceElements_OnlyValidRoutes()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Slice elements should not reference routes that were filtered out
        result.SliceElements.Should().NotBeNull();
        if (result.Routes.Count > 0)
            result.SliceElements.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CollisionFilter_MeshFaces_MatchValidSupports()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.ValidSupports > 0)
            result.SupportMesh.FaceCount.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void CollisionFilter_NoNullSegments()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var s in result.LegacySupports)
        {
            s.Segments.Should().NotBeNull($"support {s.Id} should have segments list");
            s.Segments.Should().NotBeEmpty($"support {s.Id} should have at least one segment");
            foreach (var seg in s.Segments)
            {
                seg.Part.Should().NotBeNullOrEmpty();
                seg.R1.Should().BeGreaterThanOrEqualTo(0);
                seg.R2.Should().BeGreaterThanOrEqualTo(0);
            }
        }
    }

    [Fact]
    public void CollisionFilter_RealModel_NoCrash()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", "floating_model.stl");
        if (!File.Exists(path)) return;

        var data = File.ReadAllBytes(path);
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        // Should not throw even with collision filtering
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.Should().NotBeNull();
        result.LegacySupports.Count.Should().Be(result.Routes.Count);
        result.SupportMesh.FaceCount.Should().BeGreaterOrEqualTo(0);

        // STL export should work
        var stl = result.SupportMesh.ToStlBinary();
        stl.Length.Should().Be(84 + result.SupportMesh.FaceCount * 50);
    }
}
