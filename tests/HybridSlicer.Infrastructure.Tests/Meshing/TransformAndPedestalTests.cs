using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class TransformAndPedestalTests
{
    [Fact]
    public void Transform_RotationZ90_RotatesVertices()
    {
        var mesh = new IndexedTriangleSet();
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(0, 1, 0));
        mesh.AddFace(0, 1, 2);

        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        mesh.Transform(rot, Vector3.Zero);

        // (1,0,0) rotated 90° around Z → (0,1,0)
        mesh.Vertices[0].X.Should().BeApproximately(0, 0.1f);
        mesh.Vertices[0].Y.Should().BeApproximately(1, 0.1f);
    }

    [Fact]
    public void Transform_TranslationOnly_MovesAllVertices()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 4);
        var original = mesh.Vertices.ToList();

        mesh.Transform(Quaternion.Identity, new Vector3(100, 200, 300));

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            mesh.Vertices[i].X.Should().BeApproximately(original[i].X + 100, 0.01f);
            mesh.Vertices[i].Y.Should().BeApproximately(original[i].Y + 200, 0.01f);
            mesh.Vertices[i].Z.Should().BeApproximately(original[i].Z + 300, 0.01f);
        }
    }

    [Fact]
    public void Pedestal_ProducesValidMesh()
    {
        var mesh = SupportMesher.Pedestal(new Vector3(10, 20, 0), 0.5f, 2f, 1f, 8);

        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.VertexCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Pedestal_NoNaNVertices()
    {
        var mesh = SupportMesher.Pedestal(new Vector3(0, 0, 0), 1f, 3f, 2f, 12);

        foreach (var v in mesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse();
            float.IsNaN(v.Y).Should().BeFalse();
            float.IsNaN(v.Z).Should().BeFalse();
        }
    }
}
