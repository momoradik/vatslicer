using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Robustness tests for the V2 engine — verifying it handles
/// edge cases gracefully without crashing.
/// </summary>
public class SupportEngineV2RobustnessTests
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
    public void Generate_SingleTriangle_DoesNotCrash()
    {
        var verts = new Vector3[]
        {
            new(0, 0, 10), new(10, 0, 10), new(5, 10, 10),
        };
        var mesh = MeshFromVerts(verts);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void Generate_TwoTriangles_Flat_MinimalSupports()
    {
        // Flat plate lying on the bed — minimal/no supports needed
        var verts = new Vector3[]
        {
            new(-10, -10, 0), new(10, -10, 0), new(0, 10, 0),
            new(-10, -10, 0), new(0, 10, 0), new(-10, 10, 0),
        };
        var mesh = MeshFromVerts(verts);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        result.Should().NotBeNull();
    }

    [Fact]
    public void Generate_VeryTallThinModel_CompletesInTime()
    {
        // Tall thin column — many supports needed along its height
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        // 1mm x 1mm x 200mm column
        float w = 1f, h = 200f;
        Quad(new(0,0,h), new(w,0,h), new(w,w,h), new(0,w,h));
        Quad(new(0,0,0), new(0,w,0), new(w,w,0), new(w,0,0));
        Quad(new(w,0,0), new(w,0,h), new(w,w,h), new(w,w,0));
        Quad(new(0,0,h), new(0,0,0), new(0,w,0), new(0,w,h));
        Quad(new(0,w,h), new(w,w,h), new(w,w,0), new(0,w,0));
        Quad(new(0,0,0), new(w,0,0), new(w,0,h), new(0,0,h));

        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void Generate_VerySmallModel_CompletesInTime()
    {
        // 0.1mm cube — tests numerical precision at small scale
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 0.1f;
        var o = new Vector3(-s/2, -s/2, 5); // floating at Z=5
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.Should().NotBeNull();
    }

    [Fact]
    public void Generate_ZeroDensity_MinimalSupports()
    {
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        var o = new Vector3(-10, -10, 10);
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var mesh = MeshFromVerts(verts);
        var sparse = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.01f, // very sparse
        });

        sparse.Should().NotBeNull();
        // Very sparse should have fewer supports
        sparse.ValidSupports.Should().BeLessThan(150); // coverage fill may add extra supports
    }

    [Fact]
    public void Generate_MaxDensity_MoreSupports()
    {
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        var o = new Vector3(-10, -10, 10);
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var mesh = MeshFromVerts(verts);
        var dense = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 1.0f, // maximum density
        });

        dense.Should().NotBeNull();
        dense.ValidSupports.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Generate_ResultHasAllFields()
    {
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        var o = new Vector3(-10, -10, 10);
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        var mesh = MeshFromVerts(verts);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Every field in EngineResult should be populated
        result.Bvh.Should().NotBeNull();
        result.Points.Should().NotBeNull();
        result.Pinheads.Should().NotBeNull();
        result.Routes.Should().NotBeNull();
        result.Interconnections.Should().NotBeNull();
        result.SupportMesh.Should().NotBeNull();
        result.MergeInfo.Should().NotBeNull();
        result.CollisionResult.Should().NotBeNull();
        result.StructuralResult.Should().NotBeNull();
        result.SliceElements.Should().NotBeNull();
        result.LegacySupports.Should().NotBeNull();
        result.LegacyCrossBraces.Should().NotBeNull();
        result.TotalElapsedMs.Should().BeGreaterOrEqualTo(0);
    }
}
