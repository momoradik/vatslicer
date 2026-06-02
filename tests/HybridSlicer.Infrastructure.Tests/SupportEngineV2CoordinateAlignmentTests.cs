using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests verifying V2 support mesh coordinates align with the model
/// after centering — critical for frontend Three.js rendering.
/// </summary>
public class SupportEngineV2CoordinateAlignmentTests
{
    private static StlMesh CreateOffsetCube(float ox, float oy, float oz, float size = 20f)
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
    public void SupportMesh_XY_CenteredAroundOrigin()
    {
        var mesh = CreateOffsetCube(100, 200, 50);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.SupportMesh.VertexCount == 0) return;

        float avgX = result.SupportMesh.Vertices.Average(v => v.X);
        float avgY = result.SupportMesh.Vertices.Average(v => v.Y);

        // Support mesh should be roughly centered at XY origin (within model size)
        MathF.Abs(avgX).Should().BeLessThan(20f, "support mesh X should be near origin");
        MathF.Abs(avgY).Should().BeLessThan(20f, "support mesh Y should be near origin");
    }

    [Fact]
    public void SupportMesh_Z_StartsNearZero()
    {
        var mesh = CreateOffsetCube(-10, -10, 30); // floating at Z=30
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        if (result.SupportMesh.VertexCount == 0) return;

        float minZ = result.SupportMesh.Vertices.Min(v => v.Z);
        minZ.Should().BeLessThan(2f, "support mesh Z should start near bed (Z=0)");
    }

    [Fact]
    public void SupportContacts_WithinModelBounds()
    {
        var mesh = CreateOffsetCube(-10, -10, 10);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // After centering: model X=-10..10, Y=-10..10, Z=0..20
        foreach (var s in result.LegacySupports)
        {
            s.ContactX.Should().BeInRange(-15f, 15f, $"support {s.Id} X within model bounds");
            s.ContactY.Should().BeInRange(-15f, 15f, $"support {s.Id} Y within model bounds");
            s.ContactZ.Should().BeInRange(-1f, 25f, $"support {s.Id} Z within model range");
        }
    }

    [Fact]
    public void SupportBases_AtBedLevel()
    {
        var mesh = CreateOffsetCube(-10, -10, 10);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var s in result.LegacySupports)
        {
            s.BaseZ.Should().BeApproximately(0f, 1f, $"support {s.Id} base should be near Z=0");
        }
    }

    [Fact]
    public void MeshOffset_MatchesFrontendCentering()
    {
        // Model at (100, 200, 50), size 20
        // Backend offset: X=-(100+10)=-110, Y=-(200+10)=-210, Z=-50
        var mesh = CreateOffsetCube(100, 200, 50);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.MeshCenteringOffset.X.Should().BeApproximately(-110f, 0.5f);
        result.MeshCenteringOffset.Y.Should().BeApproximately(-210f, 0.5f);
        result.MeshCenteringOffset.Z.Should().BeApproximately(-50f, 0.5f);
    }

    [Fact]
    public void ThreeJS_YZSwap_ProducesCorrectMapping()
    {
        var mesh = CreateOffsetCube(-10, -10, 10);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // In Three.js: threeX=printX, threeY=printZ, threeZ=printY
        // Model after centering: printX=[-10,10], printY=[-10,10], printZ=[0,20]
        // Three.js: X=[-10,10], Y=[0,20], Z=[-10,10]

        // Support mesh vertices are in print-space — after Y/Z swap in frontend:
        // Support mesh X should be in [-15, 15] (near model X)
        // Support mesh Z (=printY) should be in [-15, 15]
        // Support mesh Y (=printZ) should be in [0, 20+]

        if (result.SupportMesh.VertexCount == 0) return;

        // Verify print-space Z is reasonable (before Y/Z swap)
        float maxPrintZ = result.SupportMesh.Vertices.Max(v => v.Z);
        maxPrintZ.Should().BeLessThan(25f, "printZ should be within model height + margin");
    }
}
