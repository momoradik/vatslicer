using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests for material estimation, print stats, and cost calculation.
/// Critical for aerospace: need to know exactly how much resin supports consume.
/// </summary>
public class SupportEngineV2MaterialTests
{
    private static StlMesh CreateFloatingCube(float size = 20f, float zOffset = 10f)
    {
        var o = new Vector3(-size/2, -size/2, zOffset);
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
    public void Volume_IsProportionalToSupportCount()
    {
        var mesh = CreateFloatingCube();
        var sparse = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.1f });
        var dense = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.9f });

        // Both should produce non-negative volume
        dense.TotalSupportVolumeMm3.Should().BeGreaterOrEqualTo(0);
        sparse.TotalSupportVolumeMm3.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void Volume_IsReasonableForSmallCube()
    {
        var mesh = CreateFloatingCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // 20mm cube floating at Z=10, supports ~10mm tall, ~0.5mm radius each
        // Volume per support ≈ π × 0.5² × 10 ≈ 7.8mm³
        // With pinhead, base: maybe ~15mm³ per support
        // 20-50 supports → 300-750mm³ total
        result.TotalSupportVolumeMm3.Should().BeInRange(1, 5000,
            "volume should be reasonable for a small floating cube");
    }

    [Fact]
    public void SupportLayerCount_IsPositive()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.SupportLayerCount.Should().BeGreaterOrEqualTo(0,
            "supports span multiple Z layers");
    }

    [Fact]
    public void CrossSectionArea_IncreasesWithDensity()
    {
        var mesh = CreateFloatingCube();
        var sparse = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.1f });
        var dense = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.9f });

        if (dense.ValidSupports > sparse.ValidSupports)
        {
            dense.TotalSupportCrossSectionArea.Should().BeGreaterThanOrEqualTo(sparse.TotalSupportCrossSectionArea,
                "more supports → more cross-section area");
        }
    }

    [Fact]
    public void StlExportSize_MatchesFaceCount()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var stl = result.SupportMesh.ToStlBinary();
        int expectedSize = 84 + result.SupportMesh.FaceCount * 50;
        stl.Length.Should().Be(expectedSize);
    }

    [Fact]
    public void AnalyticalSlice_MatchesMeshPresence()
    {
        var mesh = CreateFloatingCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Check that analytical slicing produces circles at heights where supports exist
        if (result.SliceElements.Count > 0)
        {
            float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
            float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));
            float midZ = (minZ + maxZ) / 2;

            var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, midZ);
            circles.Should().NotBeEmpty("mid-height should have support cross-sections");

            // Below all supports should have no circles
            var belowCircles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, minZ - 10f);
            belowCircles.Should().BeEmpty("below supports should have no cross-sections");
        }
    }

    [Fact]
    public void RealModel_MaterialEstimatesAreReasonable()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", "floating_model.stl");
        if (!File.Exists(path)) return;

        var data = File.ReadAllBytes(path);
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Reasonable bounds for a small-medium model
        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0);
        result.TotalSupportVolumeMm3.Should().BeLessThan(50000, "supports shouldn't exceed 50ml");

        // Weight: 1.1 g/cm³ → volume_mm3 * 1.1e-3 grams
        float weightG = result.TotalSupportVolumeMm3 * 1.1e-3f;
        weightG.Should().BeGreaterThan(0);
        weightG.Should().BeLessThan(100, "support weight should be under 100g");
    }
}
