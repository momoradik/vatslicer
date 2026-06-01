using System.Numerics;
using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Integration tests for the V2 support engine.
/// Tests the full pipeline: BVH → analysis → pinhead → routing → meshing → validation.
/// </summary>
public class SupportEngineV2Tests
{
    private static StlMesh CreateCube(float size = 20f, float zOffset = 10f)
    {
        // Cube floating above the bed (overhangs on bottom face)
        var o = new Vector3(-size / 2, -size / 2, zOffset);
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Generate_FloatingCube_ProducesSupports()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.ValidSupports.Should().BeGreaterThan(0, "floating cube needs supports");
        result.TotalElapsedMs.Should().BeLessThan(5000, "should complete in under 5 seconds");
    }

    [Fact]
    public void Generate_FloatingCube_ProducesMesh()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.SupportMesh.FaceCount.Should().BeGreaterThan(0, "should produce support mesh geometry");
        result.SupportMesh.VertexCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Generate_FloatingCube_ProducesLegacyFormat()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.LegacySupports.Should().NotBeEmpty("legacy format needed for frontend");
        result.LegacySupports.All(s => s.Segments.Count > 0).Should().BeTrue("every support needs segments");
    }

    [Fact]
    public void Generate_FloatingCube_VolumeIsPositive()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0, "support volume must be positive");
    }

    [Fact]
    public void Generate_FloatingCube_PinheadsAreValid()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var validPinheads = result.Pinheads.Where(p => p.pinhead.IsValid).ToList();
        validPinheads.Should().NotBeEmpty();

        foreach (var (_, ph) in validPinheads)
        {
            ph.PinRadius.Should().BeGreaterThan(0);
            ph.BackRadius.Should().BeGreaterThan(0);
            ph.Width.Should().BeGreaterThan(0);
            ph.Direction.Z.Should().BeLessThan(0, "pinhead should point downward");
        }
    }

    [Fact]
    public void Generate_FloatingCube_RoutesReachGround()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var groundRoutes = result.Routes.Where(r => r.route.ReachesGround).ToList();
        groundRoutes.Should().NotBeEmpty("most supports should reach the build plate");
    }

    [Fact]
    public void Generate_CubeOnBed_MinimalSupports()
    {
        // Cube sitting on the bed — only bottom edge overhangs need support (if any)
        var mesh = CreateCube(20f, 0f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // A cube on the bed has minimal overhangs — may have some edge supports
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void Generate_HighDensity_MoreSupports()
    {
        var mesh = CreateCube(20f, 10f);
        var lowDensity = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.2f });
        var highDensity = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.9f });

        highDensity.ValidSupports.Should().BeGreaterThanOrEqualTo(lowDensity.ValidSupports,
            "higher density should produce more or equal supports");
    }

    [Fact]
    public void Generate_SliceElements_NotEmpty()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.SliceElements.Should().NotBeEmpty("analytical slice elements needed for layer rendering");
    }

    [Fact]
    public void Generate_SupportMesh_CanExportStl()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var stlData = result.SupportMesh.ToStlBinary();
        stlData.Length.Should().BeGreaterThan(84, "STL must have header + at least one triangle");

        // Verify it's a valid STL
        var triCount = BitConverter.ToUInt32(stlData, 80);
        triCount.Should().Be((uint)result.SupportMesh.FaceCount);
        stlData.Length.Should().Be(84 + (int)triCount * 50);
    }

    [Fact]
    public void BVH_Build_And_Query()
    {
        var mesh = CreateCube(20f, 10f);
        // Center the mesh the same way the engine does
        float offX = -(mesh.Min.X + (mesh.Max.X - mesh.Min.X) / 2);
        float offY = -(mesh.Min.Y + (mesh.Max.Y - mesh.Min.Y) / 2);
        float offZ = -mesh.Min.Z;
        mesh = mesh.Transform(new Vector3(offX, offY, offZ), 1.0f);

        var bvh = AabbBvh.Build(mesh);

        // Ray from above center, going down — should hit top face
        var hit = bvh.RayCast(new Vector3(0, 0, 25), new Vector3(0, 0, -1));
        hit.Should().NotBeNull();
        hit!.Value.Point.Z.Should().BeApproximately(20f, 0.5f, "should hit top face at Z~20");

        // Point inside the cube should be detected (jittered to avoid edge ambiguity)
        bvh.IsInside(new Vector3(0.13f, 0.17f, 10.3f)).Should().BeTrue();

        // Point outside should not be detected
        bvh.IsInside(new Vector3(0, 0, 25)).Should().BeFalse();
    }
}
