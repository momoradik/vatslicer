using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests for MeshValidator integration with the V2 engine.
/// Ensures corrupted/repaired meshes still produce valid supports.
/// </summary>
public class MeshValidatorIntegrationTests
{
    [Fact]
    public void ValidateAndRepair_ValidStl_ReturnsValidMesh()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(10,0,0), new(0,10,0),
            new(0,0,0), new(0,10,0), new(0,0,10),
        };
        var data = BuildStl(verts);

        var (mesh, report) = MeshValidator.ValidateAndRepair(data);

        mesh.Should().NotBeNull();
        mesh.TriangleCount.Should().Be(2);
        report.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateAndRepair_ThenGenerate_Works()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", "floating_model.stl");
        if (!File.Exists(path)) return;

        var data = File.ReadAllBytes(path);
        var (mesh, report) = MeshValidator.ValidateAndRepair(data);

        mesh.Should().NotBeNull();

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.ValidSupports.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ValidateAndRepair_ReportsTriangleCount()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(10,0,0), new(0,10,0),
        };
        var data = BuildStl(verts);

        var (mesh, report) = MeshValidator.ValidateAndRepair(data);
        report.TriangleCount.Should().Be(1);
    }

    [Fact]
    public void ValidateAndRepair_ReportsBoundsValid()
    {
        var verts = new Vector3[]
        {
            new(-5, -3, 0), new(10, 8, 15), new(0, 0, 0),
        };
        var data = BuildStl(verts);

        var (mesh, report) = MeshValidator.ValidateAndRepair(data);
        report.BoundsValid.Should().BeTrue();
        report.VolumeMm3.Should().BeGreaterThanOrEqualTo(0);
        mesh.Max.X.Should().BeGreaterThan(mesh.Min.X);
    }

    private static byte[] BuildStl(Vector3[] verts)
    {
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
        return data;
    }
}
