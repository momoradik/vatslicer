using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class SpatialGridTests
{
    [Fact]
    public void Insert_SinglePoint_CountIsOne()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(1, 2, 3), "p1");
        grid.Count.Should().Be(1);
    }

    [Fact]
    public void ExistsInRadius_NearbyPoint_ReturnsTrue()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(10, 10, 10), "p1");

        grid.ExistsInRadius(new Vector3(10.5f, 10, 10), 1f).Should().BeTrue();
    }

    [Fact]
    public void ExistsInRadius_FarPoint_ReturnsFalse()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(10, 10, 10), "p1");

        grid.ExistsInRadius(new Vector3(100, 100, 100), 1f).Should().BeFalse();
    }

    [Fact]
    public void FindInRadius_ReturnsCorrectPoints()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(new Vector3(0, 0, 0), 1);
        grid.Insert(new Vector3(1, 0, 0), 2);
        grid.Insert(new Vector3(2, 0, 0), 3);
        grid.Insert(new Vector3(100, 0, 0), 4); // far away

        var found = grid.FindInRadius(new Vector3(0, 0, 0), 2.5f);

        found.Should().HaveCount(3);
        found.Select(f => f.id).Should().Contain(new[] { 1, 2, 3 });
        found.Select(f => f.id).Should().NotContain(4);
    }

    [Fact]
    public void FindInRadius_ResultsSortedByDistance()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(3, 0, 0), "far");
        grid.Insert(new Vector3(1, 0, 0), "near");
        grid.Insert(new Vector3(2, 0, 0), "mid");

        var found = grid.FindInRadius(new Vector3(0, 0, 0), 5f);

        found.Should().HaveCount(3);
        found[0].id.Should().Be("near");
        found[1].id.Should().Be("mid");
        found[2].id.Should().Be("far");
    }

    [Fact]
    public void NearestN_ReturnsClosestPoints()
    {
        var grid = new SpatialGrid<int>(5f);
        for (int i = 0; i < 20; i++)
            grid.Insert(new Vector3(i * 2f, 0, 0), i);

        var nearest = grid.NearestN(new Vector3(0, 0, 0), 3);

        nearest.Should().HaveCount(3);
        nearest[0].id.Should().Be(0); // at origin
        nearest[1].id.Should().Be(1); // at (2,0,0)
        nearest[2].id.Should().Be(2); // at (4,0,0)
    }

    [Fact]
    public void Clear_RemovesAllPoints()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 0), "a");
        grid.Insert(new Vector3(1, 0, 0), "b");
        grid.Count.Should().Be(2);

        grid.Clear();
        grid.Count.Should().Be(0);
        grid.ExistsInRadius(new Vector3(0, 0, 0), 10f).Should().BeFalse();
    }

    [Fact]
    public void All_ReturnsAllInsertedPoints()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(new Vector3(0, 0, 0), 1);
        grid.Insert(new Vector3(10, 10, 10), 2);
        grid.Insert(new Vector3(20, 20, 20), 3);

        var all = grid.All().ToList();
        all.Should().HaveCount(3);
        all.Select(a => a.id).Should().Contain(new[] { 1, 2, 3 });
    }

    [Fact]
    public void ManyPoints_PerformanceAcceptable()
    {
        var grid = new SpatialGrid<int>(5f);
        var rng = new Random(42);

        // Insert 10,000 points
        for (int i = 0; i < 10000; i++)
            grid.Insert(new Vector3(rng.NextSingle() * 200, rng.NextSingle() * 200, rng.NextSingle() * 200), i);

        grid.Count.Should().Be(10000);

        // 1000 radius queries should complete quickly
        int found = 0;
        for (int i = 0; i < 1000; i++)
        {
            var query = new Vector3(rng.NextSingle() * 200, rng.NextSingle() * 200, rng.NextSingle() * 200);
            if (grid.ExistsInRadius(query, 5f)) found++;
        }
        found.Should().BeGreaterThan(0); // statistically guaranteed
    }
}
