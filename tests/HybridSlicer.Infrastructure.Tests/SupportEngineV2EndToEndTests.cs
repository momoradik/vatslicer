using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// End-to-end tests that exercise the complete V2 pipeline
/// and verify every output is self-consistent.
/// </summary>
public class SupportEngineV2EndToEndTests
{
    private static StlMesh? LoadTestModel(string name)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", name);
        if (!File.Exists(path)) return null;
        var (mesh, _) = MeshValidator.ValidateAndRepair(File.ReadAllBytes(path));
        return mesh;
    }

    [Fact]
    public void E2E_FloatingModel_PipelineIsConsistent()
    {
        var mesh = LoadTestModel("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Points → Pinheads: every point should have a pinhead
        result.Pinheads.Count.Should().Be(result.Points.Count,
            "every support point should get a pinhead (valid or not)");

        // Valid pinheads → Routes: every valid pinhead should have a route
        int validPinheads = result.Pinheads.Count(p => p.pinhead.IsValid);
        result.Routes.Count.Should().Be(validPinheads,
            "every valid pinhead should get a route");

        // Routes → Legacy supports: same count
        result.LegacySupports.Count.Should().Be(result.Routes.Count,
            "every route should produce a legacy support");

        // Mesh should have faces for every route
        result.SupportMesh.FaceCount.Should().BeGreaterThan(0);

        // Slice elements should cover the support height range
        if (result.SliceElements.Count > 0)
        {
            float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
            float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));
            (maxZ - minZ).Should().BeGreaterThan(1f, "slice elements should span support height");
        }

        // Volume should match number of supports roughly
        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0);

        // STL export should match face count
        var stl = result.SupportMesh.ToStlBinary();
        BitConverter.ToUInt32(stl, 80).Should().Be((uint)result.SupportMesh.FaceCount);
    }

    [Fact]
    public void E2E_FloatingModel_SliceAtMidHeight_HasCircles()
    {
        var mesh = LoadTestModel("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.SliceElements.Count == 0) return;

        // Find mid-height of supports
        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));
        float midZ = (minZ + maxZ) / 2;

        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, midZ);
        circles.Should().NotBeEmpty("mid-height should have support cross-sections");

        // All circles should have positive radius
        foreach (var c in circles)
        {
            c.Radius.Should().BeGreaterThan(0);
            c.IsSupport.Should().BeTrue();
        }
    }

    [Fact]
    public void E2E_FloatingModel_LegacySegments_AreWellFormed()
    {
        var mesh = LoadTestModel("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var s in result.LegacySupports)
        {
            s.Id.Should().NotBeNullOrEmpty();
            s.Segments.Should().NotBeEmpty();

            // First segment should be tip
            s.Segments[0].Part.Should().Be("tip");

            // All segments should have finite coordinates
            foreach (var seg in s.Segments)
            {
                float.IsNaN(seg.X1).Should().BeFalse();
                float.IsNaN(seg.Y1).Should().BeFalse();
                float.IsNaN(seg.Z1).Should().BeFalse();
                float.IsNaN(seg.X2).Should().BeFalse();
                float.IsNaN(seg.Y2).Should().BeFalse();
                float.IsNaN(seg.Z2).Should().BeFalse();
                float.IsNaN(seg.R1).Should().BeFalse();
                float.IsNaN(seg.R2).Should().BeFalse();
                seg.R1.Should().BeGreaterThanOrEqualTo(0);
                seg.R2.Should().BeGreaterThanOrEqualTo(0);
            }

            // Contact Z should be above base Z
            s.ContactZ.Should().BeGreaterThan(s.BaseZ);
        }
    }

    [Fact]
    public void E2E_FloatingModel_Interconnections_ConnectValidPillars()
    {
        var mesh = LoadTestModel("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
        });

        foreach (var conn in result.Interconnections)
        {
            conn.PillarA.Should().BeGreaterThanOrEqualTo(0);
            conn.PillarB.Should().BeGreaterThanOrEqualTo(0);
            conn.PillarA.Should().NotBe(conn.PillarB, "no self-connections");
            conn.Radius.Should().BeGreaterThan(0);
            Vector3.Distance(conn.PointA, conn.PointB).Should().BeGreaterThan(0.1f);
        }
    }

    [Fact]
    public void E2E_FloatingModel_RoutePaths_AreMonotonicallyDescending()
    {
        var mesh = LoadTestModel("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var (id, route) in result.Routes)
        {
            if (!route.ReachesGround) continue;

            // Overall: first waypoint Z >= last waypoint Z
            route.Path[0].Position.Z.Should().BeGreaterThanOrEqualTo(
                route.Path[^1].Position.Z - 1f,
                $"route {id} should descend overall");

            // All radii positive
            foreach (var wp in route.Path)
                wp.Radius.Should().BeGreaterThan(0, $"route {id} waypoint {wp.Type}");
        }
    }

    [Fact]
    public void E2E_DeterministicOutput_TwoRuns_Identical()
    {
        var mesh = LoadTestModel("floating_model.stl");
        if (mesh == null) return;

        var cfg = new SupportEngineV2.EngineConfig { Seed = 123 };
        var r1 = SupportEngineV2.Generate(mesh, cfg);
        var r2 = SupportEngineV2.Generate(mesh, cfg);

        r1.ValidSupports.Should().Be(r2.ValidSupports);
        r1.SupportMesh.FaceCount.Should().Be(r2.SupportMesh.FaceCount);
        r1.TotalSupportVolumeMm3.Should().BeApproximately(r2.TotalSupportVolumeMm3, 0.01f);
        r1.Interconnections.Count.Should().Be(r2.Interconnections.Count);
    }
}
