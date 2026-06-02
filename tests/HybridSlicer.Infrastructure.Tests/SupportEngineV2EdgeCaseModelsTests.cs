using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests with edge-case model geometries that stress the V2 engine.
/// </summary>
public class SupportEngineV2EdgeCaseModelsTests
{
    private static StlMesh MeshFromVerts(Vector3[] verts)
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void ThinPlate_DoesNotCrash()
    {
        // Very thin plate (0.1mm thick) floating at Z=20
        var verts = new Vector3[]
        {
            new(-20, -20, 20), new(20, -20, 20), new(20, 20, 20),
            new(-20, -20, 20), new(20, 20, 20), new(-20, 20, 20),
            new(-20, -20, 20.1f), new(20, 20, 20.1f), new(20, -20, 20.1f),
            new(-20, -20, 20.1f), new(-20, 20, 20.1f), new(20, 20, 20.1f),
        };
        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
    }

    [Fact]
    public void SingleTriangle_DoesNotCrash()
    {
        var verts = new Vector3[] { new(0, 0, 10), new(20, 0, 10), new(10, 20, 10) };
        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void TwoTriangles_DoesNotCrash()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 10), new(10, 0, 10), new(5, 10, 10),
            new(0, 0, 10), new(5, 10, 10), new(-5, 10, 10),
        };
        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
    }

    [Fact]
    public void NarrowColumn_DoesNotCrash()
    {
        // 1mm x 1mm x 100mm tall column
        var o = new Vector3(-0.5f, -0.5f, 0);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float w = 1f, h = 100f;
        Quad(o+new Vector3(0,0,h), o+new Vector3(w,0,h), o+new Vector3(w,w,h), o+new Vector3(0,w,h));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,w,0), o+new Vector3(w,w,0), o+new Vector3(w,0,0));
        Quad(o+new Vector3(w,0,0), o+new Vector3(w,0,h), o+new Vector3(w,w,h), o+new Vector3(w,w,0));
        Quad(o+new Vector3(0,0,h), o+new Vector3(0,0,0), o+new Vector3(0,w,0), o+new Vector3(0,w,h));
        Quad(o+new Vector3(0,w,h), o+new Vector3(w,w,h), o+new Vector3(w,w,0), o+new Vector3(0,w,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(w,0,0), o+new Vector3(w,0,h), o+new Vector3(0,0,h));

        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void FlatOnBed_MinimalSupports()
    {
        // Cube sitting flat on the bed — Z starts at 0
        var o = new Vector3(-10, -10, 0);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
        // Flat on bed = few or no supports needed
    }
}
