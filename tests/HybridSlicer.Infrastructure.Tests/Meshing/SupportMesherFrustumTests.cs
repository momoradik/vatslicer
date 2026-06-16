using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class SupportMesherFrustumTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(48)]
    public void Frustum_VariousSides_CorrectFaceCount(int sides)
    {
        var mesh = SupportMesher.Frustum(1f, 2f, 5f, sides);
        // sides * 2 (side quads) + sides (top fan) + sides (bottom fan) = 4 * sides
        mesh.FaceCount.Should().Be(4 * sides);
    }

    [Fact]
    public void Frustum_CrossShape_Produces48Faces()
    {
        // sides=8 triggers the plus-shaped cross frustum with 12 vertices per ring
        var mesh = SupportMesher.Frustum(1f, 2f, 5f, 8);
        // 12*2 (side quads) + 12 (top fan) + 12 (bottom fan) = 48
        mesh.FaceCount.Should().Be(48);
    }

    [Fact]
    public void Frustum_HeightMatchesVertices()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 10f, 12);
        var minY = mesh.Vertices.Min(v => v.Y);
        var maxY = mesh.Vertices.Max(v => v.Y);
        (maxY - minY).Should().BeApproximately(10f, 0.01f);
    }

    [Fact]
    public void Frustum_TopRadiusMatchesVertices()
    {
        var mesh = SupportMesher.Frustum(3f, 1f, 5f, 12);
        // Top ring vertices are at Y=5 (height), radius=3
        var topVerts = mesh.Vertices.Where(v => MathF.Abs(v.Y - 5f) < 0.01f).ToList();
        if (topVerts.Count > 0)
        {
            var maxR = topVerts.Max(v => MathF.Sqrt(v.X * v.X + v.Z * v.Z));
            maxR.Should().BeApproximately(3f, 0.1f);
        }
    }

    [Fact]
    public void Frustum_BottomRadiusMatchesVertices()
    {
        var mesh = SupportMesher.Frustum(1f, 5f, 10f, 12);
        // Bottom ring vertices at Y=0, radius=5
        var botVerts = mesh.Vertices.Where(v => MathF.Abs(v.Y) < 0.01f).ToList();
        if (botVerts.Count > 0)
        {
            var maxR = botVerts.Max(v => MathF.Sqrt(v.X * v.X + v.Z * v.Z));
            maxR.Should().BeApproximately(5f, 0.1f);
        }
    }

    [Fact]
    public void Frustum_AllVerticesFinite()
    {
        var mesh = SupportMesher.Frustum(0.5f, 2f, 20f, 16);
        foreach (var v in mesh.Vertices)
        {
            float.IsFinite(v.X).Should().BeTrue();
            float.IsFinite(v.Y).Should().BeTrue();
            float.IsFinite(v.Z).Should().BeTrue();
        }
    }

    [Fact]
    public void Frustum_AllFaceIndicesValid()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 12);
        foreach (var (a, b, c) in mesh.Faces)
        {
            a.Should().BeInRange(0, mesh.VertexCount - 1);
            b.Should().BeInRange(0, mesh.VertexCount - 1);
            c.Should().BeInRange(0, mesh.VertexCount - 1);
        }
    }
}
