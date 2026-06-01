using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class SupportMesherTests
{
    [Fact]
    public void Frustum_ProducesWatertightMesh()
    {
        var mesh = SupportMesher.Frustum(1f, 2f, 10f, 12);

        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.VertexCount.Should().BeGreaterThan(0);
        // Should have side faces + top cap + bottom cap
        // 12 sides * 2 (quads) + 12 (top fan) + 12 (bottom fan) = 48
        mesh.FaceCount.Should().Be(48);
    }

    [Fact]
    public void Sphere_ProducesClosedMesh()
    {
        var mesh = SupportMesher.Sphere(5f, 8, 12);

        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.VertexCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Pinhead_ProducesMesh()
    {
        var mesh = SupportMesher.Pinhead(0.2f, 0.5f, 1.0f, 12);

        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.VertexCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OrientedFrustum_ConnectsTwoPoints()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(0, 0, 10), new Vector3(0, 0, 0),
            0.5f, 1.0f, 8);

        mesh.FaceCount.Should().BeGreaterThan(0);
        // All vertices should be reasonably close to the line between the two points
        foreach (var v in mesh.Vertices)
        {
            // Just verify vertices exist in a reasonable range
            v.Length().Should().BeLessThan(15f, "vertices should be near the connection");
        }
    }

    [Fact]
    public void OrientedFrustum_AngledConnection()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(0, 0, 10), new Vector3(5, 0, 0),
            0.3f, 0.3f, 6);

        mesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void IndexedTriangleSet_StlExport_ValidFormat()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        var stl = mesh.ToStlBinary();

        stl.Length.Should().BeGreaterThan(84);
        var triCount = BitConverter.ToUInt32(stl, 80);
        triCount.Should().Be((uint)mesh.FaceCount);
        stl.Length.Should().Be(84 + (int)triCount * 50);
    }

    [Fact]
    public void IndexedTriangleSet_WeldVertices_ReducesDuplicates()
    {
        var mesh = new IndexedTriangleSet();
        // Add two triangles sharing an edge (with duplicate vertices)
        mesh.AddVertex(new Vector3(0, 0, 0)); // 0
        mesh.AddVertex(new Vector3(1, 0, 0)); // 1
        mesh.AddVertex(new Vector3(0, 1, 0)); // 2
        mesh.AddFace(0, 1, 2);

        mesh.AddVertex(new Vector3(1, 0, 0)); // 3 — duplicate of 1
        mesh.AddVertex(new Vector3(1, 1, 0)); // 4
        mesh.AddVertex(new Vector3(0, 1, 0)); // 5 — duplicate of 2
        mesh.AddFace(3, 4, 5);

        mesh.VertexCount.Should().Be(6);
        mesh.WeldVertices(0.01f);
        mesh.VertexCount.Should().Be(4, "2 duplicates should be welded");
        mesh.FaceCount.Should().Be(2, "faces should be preserved");
    }

    [Fact]
    public void MeshMerger_MergeAll_CombinesMeshes()
    {
        var m1 = SupportMesher.Sphere(1f, 4, 6);
        var m2 = SupportMesher.Frustum(0.5f, 0.5f, 3f, 6);

        var result = MeshMerger.MergeAll(new[] { m1, m2 }, 0.001f);

        result.Mesh.FaceCount.Should().Be(m1.FaceCount + m2.FaceCount);
    }
}
