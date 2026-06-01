using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class PointRingAccuracyTests
{
    [Fact]
    public void Generate_PerpendicularToZ_AllPointsHaveZeroZ()
    {
        var pts = PointRing.Generate(Vector3.UnitZ, 5f, 8);
        foreach (var p in pts)
            MathF.Abs(p.Z).Should().BeLessThan(0.01f, "points perpendicular to Z should have Z≈0");
    }

    [Fact]
    public void Generate_PerpendicularToX_AllPointsHaveZeroX()
    {
        var pts = PointRing.Generate(Vector3.UnitX, 5f, 8);
        foreach (var p in pts)
            MathF.Abs(p.X).Should().BeLessThan(0.01f, "points perpendicular to X should have X≈0");
    }

    [Fact]
    public void Generate_AllPointsOnCircle()
    {
        float r = 3.5f;
        var pts = PointRing.Generate(Vector3.UnitY, r, 16);
        foreach (var p in pts)
            p.Length().Should().BeApproximately(r, 0.01f, "all points should be at radius distance");
    }

    [Fact]
    public void BeamOrigins_FirstIsCenter()
    {
        var center = new Vector3(10, 20, 30);
        var origins = PointRing.BeamOrigins(center, Vector3.UnitZ, 5f, 8);

        origins[0].Should().Be(center);
        origins.Length.Should().Be(9); // 1 center + 8 ring
    }

    [Fact]
    public void BeamOrigins_RingPointsOffsetFromCenter()
    {
        var center = new Vector3(0, 0, 0);
        var origins = PointRing.BeamOrigins(center, Vector3.UnitZ, 5f, 4);

        for (int i = 1; i < origins.Length; i++)
        {
            Vector3.Distance(origins[i], center).Should().BeApproximately(5f, 0.01f,
                "ring points should be at radius distance from center");
        }
    }

    [Fact]
    public void Generate_DiagonalDirection_StillValid()
    {
        var dir = Vector3.Normalize(new Vector3(1, 1, 1));
        var pts = PointRing.Generate(dir, 2f, 6);

        pts.Should().HaveCount(6);
        foreach (var p in pts)
        {
            p.Length().Should().BeApproximately(2f, 0.01f);
            // All points should be perpendicular to direction
            MathF.Abs(Vector3.Dot(p, dir)).Should().BeLessThan(0.01f,
                "ring points should be perpendicular to direction");
        }
    }

    [Fact]
    public void Generate_ZeroCount_ReturnsEmpty()
    {
        PointRing.Generate(Vector3.UnitZ, 5f, 0).Should().BeEmpty();
    }
}
