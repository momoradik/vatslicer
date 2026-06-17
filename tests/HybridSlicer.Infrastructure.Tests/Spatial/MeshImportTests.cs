using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class MeshImportTests
{
    [Fact]
    public void FromAsciiStl_ParsesSimpleTriangle()
    {
        var ascii = @"
solid test
  facet normal 0 0 1
    outer loop
      vertex 0 0 0
      vertex 10 0 0
      vertex 5 10 0
    endloop
  endfacet
endsolid test";

        var mesh = StlMesh.FromAsciiStl(ascii);
        mesh.TriangleCount.Should().Be(1);
        mesh.Vertices.Length.Should().Be(3);
    }

    [Fact]
    public void FromObj_ParsesSimpleTriangle()
    {
        var obj = @"
v 0 0 0
v 10 0 0
v 5 10 0
f 1 2 3";

        var mesh = StlMesh.FromObj(obj);
        mesh.TriangleCount.Should().Be(1);
        mesh.Vertices.Length.Should().Be(3);
    }

    [Fact]
    public void FromObj_ParsesQuad()
    {
        var obj = @"
v 0 0 0
v 10 0 0
v 10 10 0
v 0 10 0
f 1 2 3 4";

        var mesh = StlMesh.FromObj(obj);
        mesh.TriangleCount.Should().Be(2, "quad should be triangulated into 2 triangles");
    }

    [Fact]
    public void FromFile_DetectsAsciiStl()
    {
        var ascii = @"solid test
  facet normal 0 0 1
    outer loop
      vertex 0 0 0
      vertex 10 0 0
      vertex 5 10 0
    endloop
  endfacet
endsolid test";

        var data = System.Text.Encoding.UTF8.GetBytes(ascii);
        var mesh = StlMesh.FromFile(data, "test.stl");
        mesh.TriangleCount.Should().Be(1);
    }

    [Fact]
    public void FromFile_DetectsObj()
    {
        var obj = "v 0 0 0\nv 10 0 0\nv 5 10 0\nf 1 2 3";
        var data = System.Text.Encoding.UTF8.GetBytes(obj);
        var mesh = StlMesh.FromFile(data, "test.obj");
        mesh.TriangleCount.Should().Be(1);
    }
}
