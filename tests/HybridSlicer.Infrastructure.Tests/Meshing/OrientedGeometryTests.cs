using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

/// <summary>
/// Tests for oriented geometry generation — frustums and spheres
/// at arbitrary positions and orientations.
/// </summary>
public class OrientedGeometryTests
{
    [Theory]
    [InlineData(0, 0, 10, 0, 0, 0)]    // vertical down
    [InlineData(0, 0, 0, 0, 0, 10)]     // vertical up
    [InlineData(10, 0, 5, 0, 0, 5)]     // horizontal
    [InlineData(5, 5, 10, -5, -5, 0)]   // diagonal
    [InlineData(0, 0, 100, 0, 0, 0)]    // very tall
    public void OrientedFrustum_AnyDirection_ProducesValidMesh(
        float x1, float y1, float z1, float x2, float y2, float z2)
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(x1, y1, z1), new Vector3(x2, y2, z2), 0.5f, 1.0f, 8);

        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.VertexCount.Should().BeGreaterThan(0);

        // No NaN vertices
        foreach (var v in mesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse();
            float.IsNaN(v.Y).Should().BeFalse();
            float.IsNaN(v.Z).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(100, 200, 300)]
    [InlineData(-50, -50, -50)]
    public void OrientedSphere_AnyPosition_CenteredCorrectly(float x, float y, float z)
    {
        var center = new Vector3(x, y, z);
        var mesh = SupportMesher.OrientedSphere(center, 5f, 4, 6);

        mesh.FaceCount.Should().BeGreaterThan(0);

        // Average vertex position should be near the center
        var avg = new Vector3(
            mesh.Vertices.Average(v => v.X),
            mesh.Vertices.Average(v => v.Y),
            mesh.Vertices.Average(v => v.Z));

        avg.X.Should().BeApproximately(x, 1f);
        avg.Y.Should().BeApproximately(y, 1f);
        avg.Z.Should().BeApproximately(z, 1f);
    }

    [Fact]
    public void Frustum_ZeroTopRadius_MakesCone()
    {
        var mesh = SupportMesher.Frustum(0f, 2f, 5f, 8);
        mesh.FaceCount.Should().BeGreaterThan(0);
        // No top cap (radius is 0)
    }

    [Fact]
    public void Frustum_EqualRadii_MakesCylinder()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 5f, 8);
        mesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Pinhead_SmallDimensions_StillValid()
    {
        // Very small pinhead (0.1mm pin, 0.2mm back, 0.3mm width)
        var mesh = SupportMesher.Pinhead(0.1f, 0.2f, 0.3f, 6);
        mesh.FaceCount.Should().BeGreaterThan(0);
        foreach (var v in mesh.Vertices)
            float.IsNaN(v.X).Should().BeFalse();
    }

    [Fact]
    public void Pinhead_LargeDimensions_StillValid()
    {
        // Large pinhead (5mm pin, 10mm back, 20mm width)
        var mesh = SupportMesher.Pinhead(5f, 10f, 20f, 12);
        mesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PartialSphere_UpperHalf_ProducesMesh()
    {
        var mesh = SupportMesher.PartialSphere(5f, 0, MathF.PI / 2, 4, 8);
        mesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PartialSphere_LowerHalf_ProducesMesh()
    {
        var mesh = SupportMesher.PartialSphere(5f, MathF.PI / 2, MathF.PI, 4, 8);
        mesh.FaceCount.Should().BeGreaterThan(0);
    }
}
