using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class SupportMesherPartialSphereTests
{
    [Fact]
    public void PartialSphere_FullRange_MatchesSphere()
    {
        var full = SupportMesher.Sphere(5f, 6, 8);
        var partial = SupportMesher.PartialSphere(5f, 0, MathF.PI, 6, 8);

        // Partial sphere over full range should have similar face count
        partial.FaceCount.Should().BeGreaterThan(full.FaceCount / 2);
    }

    [Fact]
    public void PartialSphere_TopQuarter_FewerFaces()
    {
        var half = SupportMesher.PartialSphere(5f, 0, MathF.PI / 2, 4, 8);
        var full = SupportMesher.PartialSphere(5f, 0, MathF.PI, 6, 8);

        half.FaceCount.Should().BeLessThan(full.FaceCount);
    }

    [Fact]
    public void PartialSphere_NoNaNVertices()
    {
        var mesh = SupportMesher.PartialSphere(3f, MathF.PI / 4, 3 * MathF.PI / 4, 4, 8);
        foreach (var v in mesh.Vertices)
        {
            float.IsNaN(v.X).Should().BeFalse();
            float.IsNaN(v.Y).Should().BeFalse();
            float.IsNaN(v.Z).Should().BeFalse();
        }
    }
}
