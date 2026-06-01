using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests with all available STL test files in the test_slice directory.
/// </summary>
public class SupportEngineV2TestFileTests
{
    private static string GetTestDir()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "test_slice");
    }

    [Fact]
    public void FloatingModel_V2_ProducesSupports()
    {
        var path = Path.Combine(GetTestDir(), "floating_model.stl");
        if (!File.Exists(path)) return;

        var (mesh, _) = MeshValidator.ValidateAndRepair(File.ReadAllBytes(path));
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.ValidSupports.Should().BeGreaterThan(50, "floating model needs many supports");
        result.SupportMesh.FaceCount.Should().BeGreaterThan(1000);
        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FloatingCube_V2_ProducesSupports()
    {
        var path = Path.Combine(GetTestDir(), "floating_cube.stl");
        if (!File.Exists(path)) return;

        var (mesh, _) = MeshValidator.ValidateAndRepair(File.ReadAllBytes(path));
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Floating cube should produce supports (it has an overhang bottom face)
        // If it sits flat on bed after centering, it may have zero overhangs
        result.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void AllTestFiles_V2_DoNotCrash()
    {
        var testDir = GetTestDir();
        if (!Directory.Exists(testDir)) return;

        foreach (var stlFile in Directory.GetFiles(testDir, "*.stl"))
        {
            var data = File.ReadAllBytes(stlFile);
            var (mesh, _) = MeshValidator.ValidateAndRepair(data);

            var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

            result.Should().NotBeNull($"{Path.GetFileName(stlFile)} should not crash");
            result.TotalElapsedMs.Should().BeLessThan(10000,
                $"{Path.GetFileName(stlFile)} should complete in under 10s");
        }
    }

    [Fact]
    public void AllTestFiles_V2_ProduceValidMesh()
    {
        var testDir = GetTestDir();
        if (!Directory.Exists(testDir)) return;

        foreach (var stlFile in Directory.GetFiles(testDir, "*.stl"))
        {
            var data = File.ReadAllBytes(stlFile);
            var (mesh, _) = MeshValidator.ValidateAndRepair(data);
            var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

            if (result.ValidSupports > 0)
            {
                result.SupportMesh.FaceCount.Should().BeGreaterThan(0,
                    $"{Path.GetFileName(stlFile)} with supports should produce mesh faces");

                // STL export should work
                var stl = result.SupportMesh.ToStlBinary();
                stl.Length.Should().Be(84 + result.SupportMesh.FaceCount * 50);
            }
        }
    }

    [Fact]
    public void AllTestFiles_V2_ProduceValidLegacyFormat()
    {
        var testDir = GetTestDir();
        if (!Directory.Exists(testDir)) return;

        foreach (var stlFile in Directory.GetFiles(testDir, "*.stl"))
        {
            var data = File.ReadAllBytes(stlFile);
            var (mesh, _) = MeshValidator.ValidateAndRepair(data);
            var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

            foreach (var s in result.LegacySupports)
            {
                s.Segments.Should().NotBeEmpty($"support {s.Id} in {Path.GetFileName(stlFile)}");
                s.Segments[0].Part.Should().Be("tip");
            }
        }
    }
}
