using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class SupportMesherSphereTests
{
    [Theory]
    [InlineData(4, 6)]
    [InlineData(8, 12)]
    [InlineData(12, 24)]
    public void Sphere_VariousDetail_ProducesFaces(int rings, int sides)
    {
        var mesh = SupportMesher.Sphere(5f, rings, sides);
        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.VertexCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Sphere_AllVerticesOnSurface()
    {
        float r = 7f;
        var mesh = SupportMesher.Sphere(r, 8, 12);
        foreach (var v in mesh.Vertices)
        {
            float dist = v.Length();
            dist.Should().BeApproximately(r, 0.5f, "all vertices should be at radius distance");
        }
    }

    [Fact]
    public void Sphere_CenteredAtOrigin()
    {
        var mesh = SupportMesher.Sphere(5f, 6, 8);
        var avg = new Vector3(
            mesh.Vertices.Average(v => v.X),
            mesh.Vertices.Average(v => v.Y),
            mesh.Vertices.Average(v => v.Z));
        avg.Length().Should().BeLessThan(0.5f, "sphere should be centered at origin");
    }

    [Fact]
    public void Sphere_HasPoles()
    {
        var mesh = SupportMesher.Sphere(5f, 8, 12);
        var maxY = mesh.Vertices.Max(v => v.Y);
        var minY = mesh.Vertices.Min(v => v.Y);
        maxY.Should().BeApproximately(5f, 0.1f, "top pole");
        minY.Should().BeApproximately(-5f, 0.1f, "bottom pole");
    }

    [Fact]
    public void Sphere_AllFaceIndicesValid()
    {
        var mesh = SupportMesher.Sphere(3f, 6, 8);
        foreach (var (a, b, c) in mesh.Faces)
        {
            a.Should().BeInRange(0, mesh.VertexCount - 1);
            b.Should().BeInRange(0, mesh.VertexCount - 1);
            c.Should().BeInRange(0, mesh.VertexCount - 1);
        }
    }

    [Fact]
    public void Sphere_NoNaNVertices()
    {
        var mesh = SupportMesher.Sphere(10f, 12, 24);
        foreach (var v in mesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse();
            float.IsNaN(v.Y).Should().BeFalse();
            float.IsNaN(v.Z).Should().BeFalse();
        }
    }
}
