using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class SpatialGridConcurrencyTests
{
    [Fact]
    public void InsertMany_ThenQueryAll_Consistent()
    {
        var grid = new SpatialGrid<int>(5f);
        for (int i = 0; i < 1000; i++)
            grid.Insert(new Vector3(i, 0, 0), i);

        grid.Count.Should().Be(1000);
        grid.All().Count().Should().Be(1000);
    }

    [Fact]
    public void FindInRadius_LargeRadius_FindsAll()
    {
        var grid = new SpatialGrid<int>(5f);
        for (int i = 0; i < 10; i++)
            grid.Insert(new Vector3(i, 0, 0), i);

        var found = grid.FindInRadius(new Vector3(5, 0, 0), 100f);
        found.Should().HaveCount(10);
    }

    [Fact]
    public void NearestN_ExactMatch_ReturnsCorrect()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(0, 0, 0), "origin");
        grid.Insert(new Vector3(1, 0, 0), "close");
        grid.Insert(new Vector3(100, 0, 0), "far");

        var nearest = grid.NearestN(new Vector3(0, 0, 0), 1);
        nearest.Should().HaveCount(1);
        nearest[0].id.Should().Be("origin");
    }

    [Fact]
    public void ExistsInRadius_BoundaryCase_ExactDistance()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(new Vector3(10, 0, 0), 1);

        // Exact distance = 10, query radius = 10 → should find
        grid.ExistsInRadius(new Vector3(0, 0, 0), 10.01f).Should().BeTrue();
        // Just under → should not find
        grid.ExistsInRadius(new Vector3(0, 0, 0), 9.99f).Should().BeFalse();
    }

    [Fact]
    public void InsertAndQuery_3DSpread_Works()
    {
        var grid = new SpatialGrid<int>(10f);
        var rng = new Random(123);

        for (int i = 0; i < 500; i++)
        {
            var p = new Vector3(rng.NextSingle() * 100, rng.NextSingle() * 100, rng.NextSingle() * 100);
            grid.Insert(p, i);
        }

        grid.Count.Should().Be(500);

        // Query at center should find some points
        var found = grid.FindInRadius(new Vector3(50, 50, 50), 30f);
        found.Count.Should().BeGreaterThan(10, "center query with large radius should find points");
    }

    [Fact]
    public void Clear_ThenInsert_WorksCorrectly()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(Vector3.Zero, 1);
        grid.Clear();
        grid.Insert(new Vector3(10, 10, 10), 2);

        grid.Count.Should().Be(1);
        grid.ExistsInRadius(Vector3.Zero, 1f).Should().BeFalse();
        grid.ExistsInRadius(new Vector3(10, 10, 10), 1f).Should().BeTrue();
    }

    [Fact]
    public void NearestN_ZeroN_ReturnsEmpty()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(Vector3.Zero, 1);
        grid.NearestN(Vector3.Zero, 0).Should().BeEmpty();
    }

    [Fact]
    public void NearestN_NegativeN_ReturnsEmpty()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(Vector3.Zero, 1);
        grid.NearestN(Vector3.Zero, -1).Should().BeEmpty();
    }
}
