using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Final integration tests — full pipeline from mesh to sliced layer images.
/// These verify the complete aerospace manufacturing workflow.
/// </summary>
public class SupportEngineV2FinalIntegrationTests
{
    private static StlMesh CreateCube(float size, float z)
    {
        var o = new Vector3(-size/2, -size/2, z);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
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
    public void FullPipeline_Generate_Slice_Render_Export()
    {
        // 1. Generate supports
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.ValidSupports.Should().BeGreaterThan(0);

        // 2. Analytical slice
        if (result.SliceElements.Count == 0) return;
        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, 5f);
        circles.Should().NotBeEmpty();

        // 3. Render to PNG
        var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 100, 100, 50, 50);
        png.Should().NotBeNull();
        png[0].Should().Be(0x89); // PNG magic

        // 4. Export STL
        var stl = result.SupportMesh.ToStlBinary();
        stl.Length.Should().Be(84 + result.SupportMesh.FaceCount * 50);

        // 5. Reimport STL
        var reimported = StlMesh.FromBinary(stl);
        reimported.TriangleCount.Should().Be(result.SupportMesh.FaceCount);

        // 6. Base64 encode for API
        var base64 = Convert.ToBase64String(stl);
        base64.Should().NotBeNullOrEmpty();
        Convert.FromBase64String(base64).Length.Should().Be(stl.Length);
    }

    [Fact]
    public void FullPipeline_WithAllOptions()
    {
        var mesh = CreateCube(25f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.7f,
            EnableInterconnections = true,
            PinRadiusMm = 0.3f,
            BackRadiusMm = 0.6f,
            PillarRadiusMm = 0.6f,
            BaseRadiusMm = 2.5f,
            WideningFactor = 0.025f,
            DrainHoleExclusions = new() { (new Vector3(0, 0, 15), 3f) },
            Seed = 99,
        });

        result.Should().NotBeNull();
        result.ValidSupports.Should().BeGreaterThan(0);
        result.SupportMesh.FaceCount.Should().BeGreaterThan(0);
        result.TotalSupportVolumeMm3.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FullPipeline_DeterministicAcrossRuns()
    {
        var mesh = CreateCube(15f, 8f);
        var cfg = new SupportEngineV2.EngineConfig { Seed = 777 };

        var r1 = SupportEngineV2.Generate(mesh, cfg);
        var r2 = SupportEngineV2.Generate(mesh, cfg);

        r1.ValidSupports.Should().Be(r2.ValidSupports);
        r1.Routes.Count.Should().Be(r2.Routes.Count);
        r1.SupportMesh.FaceCount.Should().Be(r2.SupportMesh.FaceCount);
        r1.TotalSupportVolumeMm3.Should().BeApproximately(r2.TotalSupportVolumeMm3, 0.01f);
    }

    [Fact]
    public void FullPipeline_StatsMatch()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Verify internal consistency
        result.LegacySupports.Count.Should().Be(result.Routes.Count);
        result.Pinheads.Count.Should().Be(result.Points.Count);
        result.TotalElapsedMs.Should().BeGreaterThan(0);
        result.MeshCenteringOffset.Should().NotBe(default(Vector3));
    }
}
