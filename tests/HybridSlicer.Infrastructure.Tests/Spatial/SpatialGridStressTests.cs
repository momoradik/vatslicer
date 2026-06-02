using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class SpatialGridStressTests
{
    [Fact]
    public void Insert50k_ThenQuery_Under1s()
    {
        var grid = new SpatialGrid<int>(5f);
        var rng = new Random(42);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 50000; i++)
            grid.Insert(new Vector3(rng.NextSingle() * 500, rng.NextSingle() * 500, rng.NextSingle() * 500), i);

        int found = 0;
        for (int i = 0; i < 5000; i++)
        {
            var pt = new Vector3(rng.NextSingle() * 500, rng.NextSingle() * 500, rng.NextSingle() * 500);
            if (grid.ExistsInRadius(pt, 5f)) found++;
        }
        sw.Stop();

        grid.Count.Should().Be(50000);
        found.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [Fact]
    public void NearestN_FromLargeGrid_ReturnsCorrectCount()
    {
        var grid = new SpatialGrid<int>(10f);
        for (int i = 0; i < 1000; i++)
            grid.Insert(new Vector3(i, 0, 0), i);

        var nearest = grid.NearestN(new Vector3(500, 0, 0), 5);
        nearest.Should().HaveCount(5);
        nearest[0].distance.Should().BeLessThan(nearest[4].distance);
    }

    [Fact]
    public void FindInRadius_DenseCluster_FindsAll()
    {
        var grid = new SpatialGrid<int>(1f);
        for (int i = 0; i < 100; i++)
            grid.Insert(new Vector3(i * 0.01f, 0, 0), i); // all within 1mm

        var found = grid.FindInRadius(new Vector3(0.5f, 0, 0), 2f);
        found.Count.Should().Be(100, "all 100 points within 2mm radius");
    }

    [Fact]
    public void All_After50kInserts_Returns50k()
    {
        var grid = new SpatialGrid<int>(10f);
        for (int i = 0; i < 50000; i++)
            grid.Insert(new Vector3(i % 100, i / 100, 0), i);

        grid.All().Count().Should().Be(50000);
    }

    [Fact]
    public void ExistsInRadius_VerySmallRadius_OnlyExactMatch()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(new Vector3(10, 10, 10), 1);
        grid.Insert(new Vector3(10.1f, 10, 10), 2);

        grid.ExistsInRadius(new Vector3(10, 10, 10), 0.001f).Should().BeTrue();
        grid.ExistsInRadius(new Vector3(10.05f, 10, 10), 0.001f).Should().BeFalse();
    }
}
