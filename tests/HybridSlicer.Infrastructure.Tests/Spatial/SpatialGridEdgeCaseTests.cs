using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class SpatialGridEdgeCaseTests
{
    [Fact]
    public void ExistsInRadius_EmptyGrid_ReturnsFalse()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.ExistsInRadius(Vector3.Zero, 100f).Should().BeFalse();
    }

    [Fact]
    public void NearestN_EmptyGrid_ReturnsEmpty()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.NearestN(Vector3.Zero, 5).Should().BeEmpty();
    }

    [Fact]
    public void NearestN_RequestMoreThanAvailable_ReturnsAll()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(new Vector3(1, 0, 0), 1);
        grid.Insert(new Vector3(2, 0, 0), 2);

        var result = grid.NearestN(Vector3.Zero, 10);
        result.Should().HaveCount(2, "only 2 points available");
    }

    [Fact]
    public void FindInRadius_ZeroRadius_ReturnsExactMatches()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(new Vector3(5, 5, 5), 1);
        grid.Insert(new Vector3(5.001f, 5, 5), 2); // very close

        var result = grid.FindInRadius(new Vector3(5, 5, 5), 0.01f);
        result.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void Insert_NegativeCoordinates_Works()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(-100, -200, -300), "neg");
        grid.ExistsInRadius(new Vector3(-100, -200, -300), 1f).Should().BeTrue();
    }

    [Fact]
    public void Insert_LargeCoordinates_Works()
    {
        var grid = new SpatialGrid<string>(5f);
        grid.Insert(new Vector3(10000, 20000, 30000), "big");
        grid.ExistsInRadius(new Vector3(10000, 20000, 30000), 1f).Should().BeTrue();
    }

    [Fact]
    public void All_AfterClear_IsEmpty()
    {
        var grid = new SpatialGrid<int>(5f);
        grid.Insert(Vector3.Zero, 1);
        grid.Insert(Vector3.One, 2);
        grid.Clear();
        grid.All().Should().BeEmpty();
        grid.Count.Should().Be(0);
    }

    [Fact]
    public void FindInRadius_SortedByDistance()
    {
        var grid = new SpatialGrid<string>(10f);
        grid.Insert(new Vector3(10, 0, 0), "far");
        grid.Insert(new Vector3(1, 0, 0), "near");
        grid.Insert(new Vector3(5, 0, 0), "mid");

        var result = grid.FindInRadius(Vector3.Zero, 15f);
        result.Should().HaveCount(3);
        result[0].id.Should().Be("near");
        result[2].id.Should().Be("far");
    }

    [Fact]
    public void SmallCellSize_StillWorks()
    {
        var grid = new SpatialGrid<int>(0.1f); // very small cells
        for (int i = 0; i < 100; i++)
            grid.Insert(new Vector3(i * 0.01f, 0, 0), i);

        grid.Count.Should().Be(100);
        grid.ExistsInRadius(new Vector3(0.5f, 0, 0), 0.1f).Should().BeTrue();
    }

    [Fact]
    public void LargeCellSize_StillWorks()
    {
        var grid = new SpatialGrid<int>(1000f); // very large cells
        grid.Insert(new Vector3(1, 2, 3), 1);
        grid.Insert(new Vector3(500, 500, 500), 2);

        grid.Count.Should().Be(2);
        grid.ExistsInRadius(new Vector3(1, 2, 3), 5f).Should().BeTrue();
        grid.ExistsInRadius(new Vector3(500, 500, 500), 5f).Should().BeTrue();
    }
}
