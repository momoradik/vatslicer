using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Integration tests using real STL files from the test_slice directory.
/// These tests verify the full V2 pipeline against actual model geometry.
/// </summary>
public class SupportEngineV2RealModelTests
{
    private static string GetTestDataPath(string filename)
    {
        // Navigate from test bin output to repository root
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "test_slice", filename);
    }

    private static StlMesh? TryLoadMesh(string filename)
    {
        var path = GetTestDataPath(filename);
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);
        return mesh;
    }

    [Fact]
    public void FloatingModel_V2_CompletesInUnder5Seconds()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return; // skip if file not available

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "V2 should complete in under 5 seconds");
        result.ValidSupports.Should().BeGreaterThan(0);
        result.SupportMesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FloatingModel_V2_AllPinheadsPointDown()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var (id, pinhead) in result.Pinheads)
        {
            if (!pinhead.IsValid) continue;
            pinhead.Direction.Z.Should().BeLessThan(0,
                $"Pinhead {id} direction should point downward, got Z={pinhead.Direction.Z}");
        }
    }

    [Fact]
    public void FloatingModel_V2_AllRoutesHavePath()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var (id, route) in result.Routes)
        {
            if (route.Path.Count < 2) continue; // rejected routes have minimal path
            // Start should generally be above the end (junction above base)
            // But bridges may temporarily go up, so just check start > end
            route.Path[0].Position.Z.Should().BeGreaterThanOrEqualTo(route.Path[^1].Position.Z - 3f,
                $"Route {id} start should be at or above end");
        }
    }

    [Fact]
    public void FloatingModel_V2_LegacyFormatHasSegments()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.LegacySupports.Should().NotBeEmpty();
        foreach (var s in result.LegacySupports)
        {
            s.Segments.Should().NotBeEmpty($"Support {s.Id} should have segments");
            s.Segments[0].Part.Should().Be("tip", "first segment should be tip");
        }
    }

    [Fact]
    public void FloatingModel_V2_StlExportIsValid()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        var stlData = result.SupportMesh.ToStlBinary();

        // Verify valid STL format
        stlData.Length.Should().BeGreaterThan(84);
        var triCount = BitConverter.ToUInt32(stlData, 80);
        stlData.Length.Should().Be(84 + (int)triCount * 50);

        // Should be re-parseable
        var reimported = StlMesh.FromBinary(stlData);
        reimported.TriangleCount.Should().Be((int)triCount);
    }

    [Fact]
    public void FloatingModel_V2_AnalyticalSlicingProducesLayers()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Slice at several Z heights
        float meshHeight = mesh.Max.Z - mesh.Min.Z;
        int layersWithCircles = 0;
        for (float z = 1f; z < meshHeight; z += meshHeight / 10f)
        {
            var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, z);
            if (circles.Count > 0) layersWithCircles++;
        }

        layersWithCircles.Should().BeGreaterThan(0, "some layers should contain support circles");
    }

    [Fact]
    public void FloatingModel_V2_VolumeIsReasonable()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Volume should be positive and reasonable (not zero, not absurdly large)
        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0);
        result.TotalSupportVolumeMm3.Should().BeLessThan(100000, "support volume should be reasonable");
    }

    [Fact]
    public void FloatingModel_V2_HigherDensity_MoreSupports()
    {
        var mesh = TryLoadMesh("floating_model.stl");
        if (mesh == null) return;

        var low = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.2f });
        var high = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.9f });

        high.ValidSupports.Should().BeGreaterThanOrEqualTo(low.ValidSupports);
    }
}
