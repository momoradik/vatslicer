using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class MeshMergerTests
{
    [Fact]
    public void MergeAll_EmptyList_ProducesEmptyMesh()
    {
        var result = MeshMerger.MergeAll(new List<IndexedTriangleSet>());
        result.Mesh.FaceCount.Should().Be(0);
        result.Mesh.VertexCount.Should().Be(0);
    }

    [Fact]
    public void MergeAll_SingleMesh_PreservesGeometry()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 6);
        var result = MeshMerger.MergeAll(new[] { mesh });

        result.FinalFaces.Should().Be(mesh.FaceCount);
    }

    [Fact]
    public void MergeAll_TwoMeshes_CombinesFaces()
    {
        var m1 = SupportMesher.Frustum(1f, 1f, 5f, 6);
        var m2 = SupportMesher.Sphere(2f, 4, 6);
        int totalFaces = m1.FaceCount + m2.FaceCount;

        var result = MeshMerger.MergeAll(new[] { m1, m2 });
        result.FinalFaces.Should().BeLessThanOrEqualTo(totalFaces, "welding may remove degenerates");
        result.FinalFaces.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CountNonManifoldEdges_ClosedFrustum_Zero()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        MeshMerger.CountNonManifoldEdges(mesh).Should().Be(0);
    }

    [Fact]
    public void CountNonManifoldEdges_OpenTriangle_HasBoundary()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        var nm = MeshMerger.CountNonManifoldEdges(mesh);
        nm.Should().BeGreaterThan(0, "single triangle has 3 boundary edges");
    }
}
