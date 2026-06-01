using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

/// <summary>
/// Tests for mesh quality — critical for aerospace where defective
/// support geometry can cause print failures or structural issues.
/// </summary>
public class MeshQualityTests
{
    [Fact]
    public void Frustum_HasNoZeroAreaFaces()
    {
        var mesh = SupportMesher.Frustum(1f, 2f, 10f, 12);
        VerifyNoZeroAreaFaces(mesh);
    }

    [Fact]
    public void Sphere_HasNoZeroAreaFaces()
    {
        var mesh = SupportMesher.Sphere(5f, 8, 12);
        VerifyNoZeroAreaFaces(mesh);
    }

    [Fact]
    public void Pinhead_HasNoZeroAreaFaces()
    {
        var mesh = SupportMesher.Pinhead(0.2f, 0.5f, 1.0f, 12);
        VerifyNoZeroAreaFaces(mesh);
    }

    [Fact]
    public void OrientedFrustum_Diagonal_HasNoZeroAreaFaces()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(0, 0, 10), new Vector3(5, 3, 0), 0.5f, 1.0f, 8);
        VerifyNoZeroAreaFaces(mesh);
    }

    [Fact]
    public void Frustum_AllFacesHaveValidNormals()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        VerifyValidNormals(mesh);
    }

    [Fact]
    public void Sphere_AllFacesHaveValidNormals()
    {
        var mesh = SupportMesher.Sphere(3f, 6, 8);
        VerifyValidNormals(mesh);
    }

    [Fact]
    public void Frustum_VertexIndicesInRange()
    {
        var mesh = SupportMesher.Frustum(1f, 2f, 10f, 16);
        VerifyIndicesInRange(mesh);
    }

    [Fact]
    public void StlExport_RoundTrip_PreservesFaceCount()
    {
        var original = SupportMesher.Frustum(1f, 1f, 5f, 8);
        var stlData = original.ToStlBinary();
        var reimported = HybridSlicer.Infrastructure.Resin.StlMesh.FromBinary(stlData);

        reimported.TriangleCount.Should().Be(original.FaceCount,
            "reimported mesh should have same face count");
    }

    [Fact]
    public void WeldVertices_PreservesTopology()
    {
        // Create a mesh with some duplicate vertices
        var mesh = new IndexedTriangleSet();
        // Triangle 1
        mesh.AddVertex(new Vector3(0, 0, 0)); // 0
        mesh.AddVertex(new Vector3(1, 0, 0)); // 1
        mesh.AddVertex(new Vector3(0, 1, 0)); // 2
        mesh.AddFace(0, 1, 2);
        // Triangle 2 sharing edge 1-2 but with duplicate vertices
        mesh.AddVertex(new Vector3(1, 0, 0)); // 3 = dup of 1
        mesh.AddVertex(new Vector3(0, 1, 0)); // 4 = dup of 2
        mesh.AddVertex(new Vector3(1, 1, 0)); // 5
        mesh.AddFace(3, 5, 4);

        int origFaces = mesh.FaceCount;
        mesh.WeldVertices(0.01f);

        mesh.FaceCount.Should().Be(origFaces, "welding should not remove valid faces");
        mesh.VertexCount.Should().Be(4, "2 duplicates should be welded, leaving 4 unique");
    }

    [Fact]
    public void WeldVertices_RemovesDegenerateFaces()
    {
        var mesh = new IndexedTriangleSet();
        // Normal triangle
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);
        // Degenerate triangle (two vertices are the same after welding)
        mesh.AddVertex(new Vector3(5, 5, 5));
        mesh.AddVertex(new Vector3(5.0001f, 5, 5)); // will weld to vertex 3
        mesh.AddVertex(new Vector3(5, 6, 5));
        mesh.AddFace(3, 4, 5);

        mesh.WeldVertices(0.001f);
        mesh.FaceCount.Should().Be(1, "degenerate face should be removed");
    }

    [Fact]
    public void MergedMesh_NonManifoldCount_IsLow()
    {
        // Merge two frustums — their shared boundary may create non-manifold edges
        var m1 = SupportMesher.Frustum(0.5f, 0.5f, 5f, 8);
        var m2 = SupportMesher.Frustum(0.5f, 0.5f, 5f, 8);
        // Offset m2
        for (int i = 0; i < m2.Vertices.Count; i++)
            m2.Vertices[i] += new Vector3(0, 5, 0);

        var merged = new IndexedTriangleSet();
        merged.Merge(m1);
        merged.Merge(m2);

        int nm = MeshMerger.CountNonManifoldEdges(merged);
        // Two separate closed meshes should have 0 non-manifold edges
        nm.Should().Be(0, "two separate closed meshes should have no non-manifold edges");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void VerifyNoZeroAreaFaces(IndexedTriangleSet mesh)
    {
        foreach (var (a, b, c) in mesh.Faces)
        {
            var v0 = mesh.Vertices[a];
            var v1 = mesh.Vertices[b];
            var v2 = mesh.Vertices[c];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            float area = cross.Length() * 0.5f;
            area.Should().BeGreaterThan(1e-10f, "face should have non-zero area");
        }
    }

    private static void VerifyValidNormals(IndexedTriangleSet mesh)
    {
        foreach (var (a, b, c) in mesh.Faces)
        {
            var v0 = mesh.Vertices[a];
            var v1 = mesh.Vertices[b];
            var v2 = mesh.Vertices[c];
            var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
            float.IsNaN(normal.X).Should().BeFalse("normal X should not be NaN");
            float.IsNaN(normal.Y).Should().BeFalse("normal Y should not be NaN");
            float.IsNaN(normal.Z).Should().BeFalse("normal Z should not be NaN");
        }
    }

    private static void VerifyIndicesInRange(IndexedTriangleSet mesh)
    {
        foreach (var (a, b, c) in mesh.Faces)
        {
            a.Should().BeInRange(0, mesh.VertexCount - 1);
            b.Should().BeInRange(0, mesh.VertexCount - 1);
            c.Should().BeInRange(0, mesh.VertexCount - 1);
        }
    }
}
