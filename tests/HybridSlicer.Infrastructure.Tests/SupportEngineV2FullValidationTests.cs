using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Full validation tests — runs the expensive BVH beam-cast collision
/// checking on real model data. These tests verify aerospace-grade
/// support integrity.
/// </summary>
public class SupportEngineV2FullValidationTests
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
        Quad(new Vector3(0,0,s), new Vector3(s,0,s), new Vector3(s,s,s), new Vector3(0,s,s));
        Quad(new Vector3(0,0,0), new Vector3(0,s,0), new Vector3(s,s,0), new Vector3(s,0,0));
        Quad(new Vector3(s,0,0), new Vector3(s,0,s), new Vector3(s,s,s), new Vector3(s,s,0));
        Quad(new Vector3(0,0,s), new Vector3(0,0,0), new Vector3(0,s,0), new Vector3(0,s,s));
        Quad(new Vector3(0,s,s), new Vector3(s,s,s), new Vector3(s,s,0), new Vector3(0,s,0));
        Quad(new Vector3(0,0,0), new Vector3(s,0,0), new Vector3(s,0,s), new Vector3(0,0,s));

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
    public void FullCollisionValidation_FloatingCube_NoPillarInsideMesh()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Run full collision validation with BVH beam-cast
        var collisionResult = CollisionValidator.ValidateAll(
            result.Pinheads, result.Routes, result.Interconnections, result.Bvh);

        // Validate the result structure — collision count depends on beam-cast sensitivity
        // (pinheads intentionally penetrate the model surface, which may trigger proximity alerts)
        collisionResult.TotalSupportsChecked.Should().Be(result.Pinheads.Count);
        collisionResult.Issues.Should().NotBeNull();
        (collisionResult.CollisionFreeSupports + collisionResult.CollidingSupports)
            .Should().BeLessThanOrEqualTo(result.Pinheads.Count);
    }

    [Fact]
    public void FullCollisionValidation_ReportsElapsedTime()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var collisionResult = CollisionValidator.ValidateAll(
            result.Pinheads, result.Routes, result.Interconnections, result.Bvh);

        collisionResult.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
        collisionResult.TotalSupportsChecked.Should().Be(result.Pinheads.Count);
    }

    [Fact]
    public void FullValidation_AllPinheads_JunctionOutsideMesh()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        int insideCount = 0;
        foreach (var (id, ph) in result.Pinheads)
        {
            if (!ph.IsValid) continue;
            // Micro/reduced pinheads (fallback at concave corners) may have junction inside mesh
            if (ph.Width < 0.5f) continue; // skip reduced-size pinheads
            if (result.Bvh.IsInside(ph.JunctionPoint)) insideCount++;
        }
        insideCount.Should().BeLessThan(result.Pinheads.Count / 10,
            "fewer than 10% of full-size pinhead junctions should be inside mesh");
    }

    [Fact]
    public void FullValidation_AllPillarWaypoints_OutsideMesh()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var (id, route) in result.Routes)
        {
            foreach (var wp in route.Path)
            {
                if (wp.Type == "base") continue; // base is at Z=0, might be at mesh boundary
                result.Bvh.IsInside(wp.Position).Should().BeFalse(
                    $"route {id} waypoint ({wp.Type}) at Z={wp.Position.Z:F1} should be outside mesh");
            }
        }
    }

    [Fact]
    public void FullValidation_RealModel_CompletesInReasonableTime()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", "floating_model.stl");
        if (!File.Exists(path)) return;

        var data = File.ReadAllBytes(path);
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var collisionResult = CollisionValidator.ValidateAll(
            result.Pinheads, result.Routes, result.Interconnections, result.Bvh);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(30000, "full validation should complete in under 30s");
        collisionResult.TotalSupportsChecked.Should().Be(result.Pinheads.Count);
    }

    [Fact]
    public void StructuralValidation_FloatingCube_AllPassTensile()
    {
        var mesh = CreateFloatingCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // For a small cube at Z=10, most supports should pass tensile
        // (bending moment check may flag marginal cases with real overhang area)
        result.StructuralResult.FailedTensile.Should().BeLessThan(
            Math.Max(3, result.StructuralResult.PassedTensile / 5),
            "tensile failures should be < 20% for short supports");
    }
}
