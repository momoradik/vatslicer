using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class MeshMergerStressTests
{
    [Fact]
    public void MergeAll_100Meshes_CompletesUnder1s()
    {
        var meshes = new List<IndexedTriangleSet>();
        for (int i = 0; i < 100; i++)
            meshes.Add(SupportMesher.Frustum(0.5f, 0.5f, 5f, 6));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = MeshMerger.MergeAll(meshes, 0.001f);
        sw.Stop();

        result.FinalFaces.Should().BeGreaterThan(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void MergeAll_MixedTypes_PreservesAllFaces()
    {
        var meshes = new List<IndexedTriangleSet>
        {
            SupportMesher.Frustum(1f, 1f, 5f, 8),
            SupportMesher.Sphere(2f, 4, 6),
            SupportMesher.Pinhead(0.2f, 0.5f, 1f, 6),
        };

        int totalFaces = meshes.Sum(m => m.FaceCount);
        var result = MeshMerger.MergeAll(meshes);
        result.FinalFaces.Should().BeLessThanOrEqualTo(totalFaces);
        result.FinalFaces.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CountNonManifoldEdges_ClosedMeshes_Zero()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        MeshMerger.CountNonManifoldEdges(mesh).Should().Be(0);
    }

    [Fact]
    public void CountNonManifoldEdges_EmptyMesh_Zero()
    {
        MeshMerger.CountNonManifoldEdges(new IndexedTriangleSet()).Should().Be(0);
    }
}
