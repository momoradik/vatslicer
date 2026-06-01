using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests verifying the V2 engine's coordinate system is consistent
/// and the centering offset is correct — critical for frontend alignment.
/// </summary>
public class SupportEngineV2CoordinateTests
{
    private static StlMesh CreateOffsetCube(float size, float ox, float oy, float oz)
    {
        var o = new Vector3(ox, oy, oz);
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
    public void CenteringOffset_CentersXY()
    {
        // Cube from (100,200,50) to (120,220,70)
        var mesh = CreateOffsetCube(20f, 100f, 200f, 50f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Offset should center XY: offX = -(100 + 10) = -110
        result.MeshCenteringOffset.X.Should().BeApproximately(-110f, 0.5f);
        result.MeshCenteringOffset.Y.Should().BeApproximately(-210f, 0.5f);
        result.MeshCenteringOffset.Z.Should().BeApproximately(-50f, 0.5f);
    }

    [Fact]
    public void CenteringOffset_ZBottomAtZero()
    {
        var mesh = CreateOffsetCube(10f, 0f, 0f, 30f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Z offset should bring bottom to Z=0
        result.MeshCenteringOffset.Z.Should().BeApproximately(-30f, 0.5f);
    }

    [Fact]
    public void SupportPositions_AreInCenteredFrame()
    {
        // Cube from (100,100,50) to (120,120,70) — after centering: (-10,-10,0) to (10,10,20)
        var mesh = CreateOffsetCube(20f, 100f, 100f, 50f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var s in result.LegacySupports)
        {
            // All support X,Y should be near centered range (-10 to 10)
            s.ContactX.Should().BeInRange(-15f, 15f, $"support {s.Id} X should be in centered frame");
            s.ContactY.Should().BeInRange(-15f, 15f, $"support {s.Id} Y should be in centered frame");
            // Z should be above 0 (bed level)
            s.ContactZ.Should().BeGreaterThan(-1f, $"support {s.Id} Z should be above bed");
        }
    }

    [Fact]
    public void DifferentOriginalPositions_SameSizeModel_SameSupports()
    {
        // Two cubes same size, different positions — after centering they're the same
        var mesh1 = CreateOffsetCube(15f, 0f, 0f, 10f);
        var mesh2 = CreateOffsetCube(15f, 500f, 500f, 10f);

        var r1 = SupportEngineV2.Generate(mesh1, new SupportEngineV2.EngineConfig { Seed = 42 });
        var r2 = SupportEngineV2.Generate(mesh2, new SupportEngineV2.EngineConfig { Seed = 42 });

        r1.ValidSupports.Should().Be(r2.ValidSupports,
            "same geometry at different positions should produce same supports after centering");
    }

    [Fact]
    public void MeshOffset_AppliedCorrectly_SupportsAlignWithModel()
    {
        var mesh = CreateOffsetCube(20f, 50f, 50f, 20f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var offset = result.MeshCenteringOffset;

        // Verify: original mesh center + offset ≈ (0, 0, ?)
        float origCX = 50f + 10f; // center X of original cube
        float origCY = 50f + 10f;
        (origCX + offset.X).Should().BeApproximately(0f, 0.5f, "offset should center X");
        (origCY + offset.Y).Should().BeApproximately(0f, 0.5f, "offset should center Y");
    }

    [Fact]
    public void STLMesh_VerticesInCenteredFrame()
    {
        var mesh = CreateOffsetCube(20f, 100f, 100f, 50f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.SupportMesh.VertexCount == 0) return;

        float meshMinX = result.SupportMesh.Vertices.Min(v => v.X);
        float meshMaxX = result.SupportMesh.Vertices.Max(v => v.X);
        float meshMinY = result.SupportMesh.Vertices.Min(v => v.Y);
        float meshMaxY = result.SupportMesh.Vertices.Max(v => v.Y);

        // Support mesh vertices should be in centered frame (roughly -15 to 15 for a 20mm cube)
        meshMinX.Should().BeGreaterThan(-20f);
        meshMaxX.Should().BeLessThan(20f);
        meshMinY.Should().BeGreaterThan(-20f);
        meshMaxY.Should().BeLessThan(20f);
    }
}
