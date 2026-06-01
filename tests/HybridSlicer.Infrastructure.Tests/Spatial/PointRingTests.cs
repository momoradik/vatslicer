using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class PointRingTests
{
    [Fact]
    public void Generate_8Points_AllOnCircle()
    {
        var points = PointRing.Generate(Vector3.UnitZ, 1.0f, 8);

        points.Should().HaveCount(8);
        foreach (var p in points)
        {
            var dist = p.Length();
            dist.Should().BeApproximately(1.0f, 0.01f, "all points should be on unit circle");
            p.Z.Should().BeApproximately(0f, 0.01f, "all points should be perpendicular to Z");
        }
    }

    [Fact]
    public void Generate_PointsEvenlySpaced()
    {
        var points = PointRing.Generate(Vector3.UnitY, 2.0f, 4);

        points.Should().HaveCount(4);
        // Adjacent points should be sqrt(2)*radius apart (90 degrees)
        float expected = 2.0f * MathF.Sqrt(2);
        for (int i = 0; i < 4; i++)
        {
            float dist = Vector3.Distance(points[i], points[(i + 1) % 4]);
            dist.Should().BeApproximately(expected, 0.1f);
        }
    }

    [Fact]
    public void BeamOrigins_IncludesCenterAndRing()
    {
        var origins = PointRing.BeamOrigins(new Vector3(5, 5, 5), Vector3.UnitZ, 1.0f, 4);

        origins.Should().HaveCount(5); // center + 4 ring
        origins[0].Should().Be(new Vector3(5, 5, 5), "first origin is center");
    }
}
