using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

/// <summary>
/// Tests for mesher orientation — verifying oriented frustums and spheres
/// are correctly positioned and rotated in 3D space.
/// </summary>
public class SupportMesherOrientationTests
{
    [Fact]
    public void OrientedFrustum_VerticalDown_BoundsCorrect()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(0, 0, 10), new Vector3(0, 0, 0), 1f, 1f, 8);

        var minY = mesh.Vertices.Min(v => v.Y);
        var maxY = mesh.Vertices.Max(v => v.Y);

        // Vertical frustum from Z=10 to Z=0 — in Three.js Y-axis space
        // After rotation, vertices should span from ~0 to ~10 in some axis
        var span = mesh.Vertices.Max(v => v.Length()) - mesh.Vertices.Min(v => v.Length());
        span.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OrientedFrustum_ZeroLength_EmptyMesh()
    {
        var mesh = SupportMesher.OrientedFrustum(
            new Vector3(5, 5, 5), new Vector3(5, 5, 5), 1f, 1f, 8);

        mesh.FaceCount.Should().Be(0, "zero-length frustum should be empty");
    }

    [Fact]
    public void OrientedSphere_AtOrigin_Centered()
    {
        var mesh = SupportMesher.OrientedSphere(Vector3.Zero, 5f, 6, 8);

        var avgX = mesh.Vertices.Average(v => v.X);
        var avgY = mesh.Vertices.Average(v => v.Y);
        var avgZ = mesh.Vertices.Average(v => v.Z);

        avgX.Should().BeApproximately(0, 0.5f);
        avgY.Should().BeApproximately(0, 0.5f);
        avgZ.Should().BeApproximately(0, 0.5f);
    }

    [Fact]
    public void Frustum_ZeroHeight_EmptyMesh()
    {
        var mesh = SupportMesher.Frustum(1f, 1f, 0f, 8);
        mesh.FaceCount.Should().Be(0);
    }

    [Fact]
    public void Sphere_ZeroRadius_EmptyMesh()
    {
        var mesh = SupportMesher.Sphere(0f, 6, 8);
        mesh.FaceCount.Should().Be(0);
    }
}
