using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class MeshWeldingStressTests
{
    [Fact]
    public void WeldVertices_AllDuplicates_MergesToOne()
    {
        var mesh = new IndexedTriangleSet();
        for (int i = 0; i < 100; i++)
            mesh.AddVertex(new Vector3(5, 5, 5)); // all same position

        mesh.WeldVertices(0.01f);
        mesh.VertexCount.Should().Be(1);
    }

    [Fact]
    public void WeldVertices_NoDuplicates_PreservesAll()
    {
        var mesh = new IndexedTriangleSet();
        for (int i = 0; i < 100; i++)
            mesh.AddVertex(new Vector3(i * 10, 0, 0)); // all far apart

        mesh.WeldVertices(0.01f);
        mesh.VertexCount.Should().Be(100);
    }

    [Fact]
    public void WeldVertices_WithFaces_PreservesValidFaces()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(10, 0, 0));
        mesh.AddVertex(new Vector3(5, 10, 0));
        mesh.AddFace(0, 1, 2);

        mesh.WeldVertices(0.01f);
        mesh.FaceCount.Should().Be(1);
        mesh.VertexCount.Should().Be(3);
    }

    [Fact]
    public void WeldVertices_LargeEpsilon_MergesMore()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(0.5f, 0, 0));
        mesh.AddVertex(new Vector3(1.0f, 0, 0));

        mesh.WeldVertices(0.6f); // 0 and 0.5 merge, 1.0 stays
        mesh.VertexCount.Should().BeLessThan(3);
    }

    [Fact]
    public void WeldVertices_ZeroEpsilon_NoMerging()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(0.001f, 0, 0));
        mesh.AddVertex(new Vector3(0.002f, 0, 0));

        mesh.WeldVertices(0f);
        mesh.VertexCount.Should().Be(3);
    }
}
