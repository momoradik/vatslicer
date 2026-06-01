using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class IndexedTriangleSetTests
{
    [Fact]
    public void AddVertex_ReturnsSequentialIndices()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0)).Should().Be(0);
        mesh.AddVertex(new Vector3(1, 0, 0)).Should().Be(1);
        mesh.AddVertex(new Vector3(0, 1, 0)).Should().Be(2);
    }

    [Fact]
    public void AddFace_IncreasesCount()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        mesh.FaceCount.Should().Be(1);
        mesh.VertexCount.Should().Be(3);
    }

    [Fact]
    public void Merge_CombinesTwoMeshes()
    {
        var m1 = new IndexedTriangleSet();
        m1.AddVertex(new Vector3(0, 0, 0));
        m1.AddVertex(new Vector3(1, 0, 0));
        m1.AddVertex(new Vector3(0, 1, 0));
        m1.AddFace(0, 1, 2);

        var m2 = new IndexedTriangleSet();
        m2.AddVertex(new Vector3(2, 0, 0));
        m2.AddVertex(new Vector3(3, 0, 0));
        m2.AddVertex(new Vector3(2, 1, 0));
        m2.AddFace(0, 1, 2);

        m1.Merge(m2);
        m1.VertexCount.Should().Be(6);
        m1.FaceCount.Should().Be(2);
        // Second face indices should be offset by 3
        m1.Faces[1].a.Should().Be(3);
    }

    [Fact]
    public void Transform_TranslatesVertices()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));

        mesh.Transform(Quaternion.Identity, new Vector3(10, 20, 30));

        mesh.Vertices[0].Should().Be(new Vector3(10, 20, 30));
        mesh.Vertices[1].Should().Be(new Vector3(11, 20, 30));
    }

    [Fact]
    public void ToStlBinary_ValidHeader()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        var stl = mesh.ToStlBinary();
        stl.Length.Should().Be(84 + 50); // header + 1 triangle
        BitConverter.ToUInt32(stl, 80).Should().Be(1);
    }
}
