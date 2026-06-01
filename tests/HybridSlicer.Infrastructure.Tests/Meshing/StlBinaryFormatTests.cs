using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

/// <summary>
/// Tests that STL binary format output is correct and can be parsed
/// by standard STL readers (Three.js STLLoader, MeshLab, etc).
/// </summary>
public class StlBinaryFormatTests
{
    [Fact]
    public void StlBinary_HeaderIs80Bytes()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        var stl = mesh.ToStlBinary();

        // First 80 bytes are header (usually zeros)
        stl.Length.Should().BeGreaterThanOrEqualTo(84);
    }

    [Fact]
    public void StlBinary_TriangleCountAt80()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 6);
        var stl = mesh.ToStlBinary();

        uint triCount = BitConverter.ToUInt32(stl, 80);
        triCount.Should().Be((uint)mesh.FaceCount);
    }

    [Fact]
    public void StlBinary_EachTriangleIs50Bytes()
    {
        var mesh = SupportMesher.Sphere(2f, 4, 6);
        var stl = mesh.ToStlBinary();

        int expectedSize = 84 + mesh.FaceCount * 50;
        stl.Length.Should().Be(expectedSize);
    }

    [Fact]
    public void StlBinary_NormalsAreUnitLength()
    {
        var mesh = SupportMesher.Frustum(1f, 2f, 10f, 8);
        var stl = mesh.ToStlBinary();

        int offset = 84;
        for (int t = 0; t < mesh.FaceCount; t++)
        {
            float nx = BitConverter.ToSingle(stl, offset);
            float ny = BitConverter.ToSingle(stl, offset + 4);
            float nz = BitConverter.ToSingle(stl, offset + 8);
            var normal = new Vector3(nx, ny, nz);

            if (normal.LengthSquared() > 0.01f) // skip degenerate
            {
                normal.Length().Should().BeApproximately(1f, 0.1f,
                    $"triangle {t} normal should be unit length");
            }

            offset += 50;
        }
    }

    [Fact]
    public void StlBinary_VerticesAreFinite()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(0, 0, 50), new Vector3(10, 5, 0), 0.5f, 1.0f, 8);
        var stl = mesh.ToStlBinary();

        int offset = 84;
        for (int t = 0; t < mesh.FaceCount; t++)
        {
            offset += 12; // skip normal
            for (int v = 0; v < 3; v++)
            {
                float x = BitConverter.ToSingle(stl, offset);
                float y = BitConverter.ToSingle(stl, offset + 4);
                float z = BitConverter.ToSingle(stl, offset + 8);

                float.IsNaN(x).Should().BeFalse($"tri {t} vert {v} X");
                float.IsNaN(y).Should().BeFalse($"tri {t} vert {v} Y");
                float.IsNaN(z).Should().BeFalse($"tri {t} vert {v} Z");
                float.IsInfinity(x).Should().BeFalse();
                float.IsInfinity(y).Should().BeFalse();
                float.IsInfinity(z).Should().BeFalse();

                offset += 12;
            }
            offset += 2; // attribute byte count
        }
    }

    [Fact]
    public void StlBinary_CanBeReimportedAsStlMesh()
    {
        var mesh = SupportMesher.Pinhead(0.2f, 0.5f, 1.0f, 8);
        var stl = mesh.ToStlBinary();

        var reimported = StlMesh.FromBinary(stl);
        reimported.TriangleCount.Should().Be(mesh.FaceCount);
        reimported.Max.Should().NotBe(reimported.Min, "bounds should be non-degenerate");
    }

    [Fact]
    public void StlBinary_V2Engine_ProducesValidStl()
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

        var mesh = StlMesh.FromBinary(BuildStl(verts));
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var stl = result.SupportMesh.ToStlBinary();

        // Verify format
        stl.Length.Should().Be(84 + result.SupportMesh.FaceCount * 50);
        BitConverter.ToUInt32(stl, 80).Should().Be((uint)result.SupportMesh.FaceCount);

        // Verify reimportable
        var reimported = StlMesh.FromBinary(stl);
        reimported.TriangleCount.Should().Be(result.SupportMesh.FaceCount);
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
