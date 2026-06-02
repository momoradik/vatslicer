using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class IndexedTriangleSetEdgeCaseTests
{
    [Fact]
    public void Empty_HasZeroCounts()
    {
        var mesh = new IndexedTriangleSet();
        mesh.VertexCount.Should().Be(0);
        mesh.FaceCount.Should().Be(0);
    }

    [Fact]
    public void ToStlBinary_Empty_ProducesHeader()
    {
        var mesh = new IndexedTriangleSet();
        var stl = mesh.ToStlBinary();
        stl.Length.Should().Be(84);
        BitConverter.ToUInt32(stl, 80).Should().Be(0);
    }

    [Fact]
    public void WeldVertices_Empty_NoError()
    {
        var mesh = new IndexedTriangleSet();
        mesh.WeldVertices();
        mesh.VertexCount.Should().Be(0);
    }

    [Fact]
    public void Merge_WithEmpty_PreservesOriginal()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        mesh.Merge(new IndexedTriangleSet());

        mesh.VertexCount.Should().Be(3);
        mesh.FaceCount.Should().Be(1);
    }

    [Fact]
    public void Transform_EmptyMesh_NoError()
    {
        var mesh = new IndexedTriangleSet();
        mesh.Transform(Quaternion.Identity, new Vector3(10, 20, 30));
        mesh.VertexCount.Should().Be(0);
    }

    [Fact]
    public void AddVertex_ReturnsIncrementingIndices()
    {
        var mesh = new IndexedTriangleSet();
        for (int i = 0; i < 100; i++)
        {
            int idx = mesh.AddVertex(new Vector3(i, 0, 0));
            idx.Should().Be(i);
        }
        mesh.VertexCount.Should().Be(100);
    }
}
