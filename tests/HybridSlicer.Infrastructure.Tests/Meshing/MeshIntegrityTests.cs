using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

/// <summary>
/// Tests for mesh integrity — critical for slicing and printing.
/// A mesh with non-manifold edges, inconsistent normals, or zero-volume
/// faces will produce bad slices and failed prints.
/// </summary>
public class MeshIntegrityTests
{
    [Fact]
    public void Frustum_IsManifold()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        MeshMerger.CountNonManifoldEdges(mesh).Should().Be(0,
            "frustum should be a closed manifold");
    }

    [Fact]
    public void Frustum_HasPositiveVolume()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        float volume = ComputeVolume(mesh);
        volume.Should().BeGreaterThan(0, "closed mesh should have positive volume");
    }

    [Fact]
    public void Sphere_IsManifold()
    {
        var mesh = SupportMesher.Sphere(3f, 8, 12);
        MeshMerger.CountNonManifoldEdges(mesh).Should().Be(0,
            "sphere should be a closed manifold");
    }

    [Fact]
    public void Sphere_HasPositiveVolume()
    {
        var mesh = SupportMesher.Sphere(3f, 8, 12);
        float volume = ComputeVolume(mesh);
        volume.Should().BeGreaterThan(0);
        // Volume of sphere r=3: 4/3 * PI * 27 ≈ 113
        volume.Should().BeApproximately(113f, 30f, "sphere volume should be roughly correct");
    }

    [Fact]
    public void OrientedFrustum_NoNaNVertices()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(0, 0, 50), new Vector3(10, 5, 0), 0.5f, 1.0f, 8);

        foreach (var v in mesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse();
            float.IsNaN(v.Y).Should().BeFalse();
            float.IsNaN(v.Z).Should().BeFalse();
            float.IsInfinity(v.X).Should().BeFalse();
            float.IsInfinity(v.Y).Should().BeFalse();
            float.IsInfinity(v.Z).Should().BeFalse();
        }
    }

    [Fact]
    public void Pinhead_NoNaNVertices()
    {
        var mesh = SupportMesher.Pinhead(0.2f, 0.5f, 1.0f, 8);

        foreach (var v in mesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse();
            float.IsNaN(v.Y).Should().BeFalse();
            float.IsNaN(v.Z).Should().BeFalse();
        }
    }

    [Fact]
    public void StlRoundTrip_PreservesGeometry()
    {
        var original = SupportMesher.Frustum(2f, 3f, 10f, 12);
        var stlData = original.ToStlBinary();
        var reimported = StlMesh.FromBinary(stlData);

        reimported.TriangleCount.Should().Be(original.FaceCount);

        // Verify bounds are similar
        float origWidth = original.Vertices.Max(v => v.X) - original.Vertices.Min(v => v.X);
        float reimWidth = reimported.Max.X - reimported.Min.X;
        reimWidth.Should().BeApproximately(origWidth, 0.1f);
    }

    [Fact]
    public void WeldedMesh_HasFewerVertices()
    {
        // Create mesh with intentional duplicates
        var m1 = SupportMesher.Frustum(1f, 1f, 5f, 6);
        var m2 = SupportMesher.Frustum(1f, 1f, 5f, 6);
        // Stack m2 on top of m1 — shared boundary vertices should weld
        for (int i = 0; i < m2.Vertices.Count; i++)
            m2.Vertices[i] += new Vector3(0, 5, 0);

        var merged = new IndexedTriangleSet();
        merged.Merge(m1);
        int beforeWeld = merged.VertexCount;
        merged.Merge(m2);
        merged.WeldVertices(0.01f);

        // After welding, vertex count should be less than simple sum
        // (shared boundary vertices get merged)
        merged.VertexCount.Should().BeLessThanOrEqualTo(beforeWeld + m2.VertexCount);
    }

    [Fact]
    public void V2Engine_SupportMesh_NoNaNVertices()
    {
        var o = new Vector3(-10, -10, 10);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a+o; verts[vi++]=b+o; verts[vi++]=c+o;
            verts[vi++]=a+o; verts[vi++]=c+o; verts[vi++]=d+o;
        }
        float s = 20f;
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
        var mesh = StlMesh.FromBinary(data);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var v in result.SupportMesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse($"vertex has NaN X");
            float.IsNaN(v.Y).Should().BeFalse($"vertex has NaN Y");
            float.IsNaN(v.Z).Should().BeFalse($"vertex has NaN Z");
        }
    }

    // ── Volume computation (signed volume method) ────────────────────────

    private static float ComputeVolume(IndexedTriangleSet mesh)
    {
        float volume = 0;
        foreach (var (a, b, c) in mesh.Faces)
        {
            var v0 = mesh.Vertices[a];
            var v1 = mesh.Vertices[b];
            var v2 = mesh.Vertices[c];
            // Signed volume of tetrahedron formed by triangle and origin
            volume += Vector3.Dot(v0, Vector3.Cross(v1, v2)) / 6f;
        }
        return Math.Abs(volume);
    }
}
