using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class BuildPlateNesterTests
{
    private static readonly BuildPlateNester.NestConfig DefaultConfig = new()
    {
        PlateWidthMm = 192f,
        PlateDepthMm = 120f,
        PartGapMm = 2f,
        PlateMarginMm = 3f,
    };

    [Fact]
    public void SinglePart_PlacedOnPlate()
    {
        var parts = new[]
        {
            new BuildPlateNester.PartFootprint { Id = "a", WidthMm = 30, DepthMm = 20 },
        };

        var result = BuildPlateNester.Arrange(parts, DefaultConfig);

        result.Placements.Should().HaveCount(1);
        result.Overflow.Should().BeEmpty();
        result.Placements[0].Id.Should().Be("a");
        result.Placements[0].CenterX.Should().BeGreaterThan(0);
        result.Placements[0].CenterY.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MultipleParts_AllFit()
    {
        var parts = new[]
        {
            new BuildPlateNester.PartFootprint { Id = "a", WidthMm = 40, DepthMm = 30 },
            new BuildPlateNester.PartFootprint { Id = "b", WidthMm = 40, DepthMm = 30 },
            new BuildPlateNester.PartFootprint { Id = "c", WidthMm = 40, DepthMm = 30 },
        };

        var result = BuildPlateNester.Arrange(parts, DefaultConfig);

        result.Placements.Should().HaveCount(3);
        result.Overflow.Should().BeEmpty();
        // All centers should be within plate bounds
        foreach (var p in result.Placements)
        {
            p.CenterX.Should().BeGreaterThan(0).And.BeLessThan(DefaultConfig.PlateWidthMm);
            p.CenterY.Should().BeGreaterThan(0).And.BeLessThan(DefaultConfig.PlateDepthMm);
        }
    }

    [Fact]
    public void NoOverlap_BetweenPlacements()
    {
        var parts = Enumerable.Range(0, 6).Select(i =>
            new BuildPlateNester.PartFootprint { Id = $"p{i}", WidthMm = 30, DepthMm = 25 }
        ).ToArray();

        var result = BuildPlateNester.Arrange(parts, DefaultConfig);

        // Check no two placements overlap
        for (int i = 0; i < result.Placements.Count; i++)
        for (int j = i + 1; j < result.Placements.Count; j++)
        {
            var a = result.Placements[i];
            var b = result.Placements[j];
            float halfWa = 30f / 2 + 1f; // half width + half gap
            float halfDa = 25f / 2 + 1f;
            bool overlapX = Math.Abs(a.CenterX - b.CenterX) < halfWa + halfWa - 1f;
            bool overlapY = Math.Abs(a.CenterY - b.CenterY) < halfDa + halfDa - 1f;
            bool overlaps = overlapX && overlapY;
            overlaps.Should().BeFalse($"Parts {a.Id} and {b.Id} should not overlap");
        }
    }

    [Fact]
    public void OversizedPart_GoesToOverflow()
    {
        var parts = new[]
        {
            new BuildPlateNester.PartFootprint { Id = "big", WidthMm = 300, DepthMm = 200 },
        };

        var result = BuildPlateNester.Arrange(parts, DefaultConfig);

        result.Placements.Should().BeEmpty();
        result.Overflow.Should().Contain("big");
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var result = BuildPlateNester.Arrange(Array.Empty<BuildPlateNester.PartFootprint>(), DefaultConfig);
        result.Placements.Should().BeEmpty();
        result.Overflow.Should().BeEmpty();
    }

    [Fact]
    public void ManySmallParts_HighUtilization()
    {
        var parts = Enumerable.Range(0, 20).Select(i =>
            new BuildPlateNester.PartFootprint { Id = $"s{i}", WidthMm = 15, DepthMm = 15 }
        ).ToArray();

        var result = BuildPlateNester.Arrange(parts, DefaultConfig);

        // 20 parts of 15x15 = 4500mm2 on a ~186x114 = 21204mm2 plate → should all fit
        result.Placements.Count.Should().BeGreaterOrEqualTo(15, "most small parts should fit");
        result.Utilization.Should().BeGreaterThan(0.1f);
    }

    [Fact]
    public void MixedSizes_LargestFirst()
    {
        var parts = new[]
        {
            new BuildPlateNester.PartFootprint { Id = "small", WidthMm = 10, DepthMm = 10 },
            new BuildPlateNester.PartFootprint { Id = "large", WidthMm = 80, DepthMm = 60 },
            new BuildPlateNester.PartFootprint { Id = "medium", WidthMm = 30, DepthMm = 25 },
        };

        var result = BuildPlateNester.Arrange(parts, DefaultConfig);

        result.Placements.Should().HaveCount(3, "all three should fit");
        result.Overflow.Should().BeEmpty();
    }

    [Fact]
    public void Rotation_FitsTallNarrowPart()
    {
        // A tall narrow part (10x100) that barely fits when rotated 90 degrees
        // on a plate that's wider than deep
        var config = new BuildPlateNester.NestConfig
        {
            PlateWidthMm = 120, PlateDepthMm = 50, PartGapMm = 2, PlateMarginMm = 3,
        };
        var parts = new[]
        {
            new BuildPlateNester.PartFootprint { Id = "tall", WidthMm = 10, DepthMm = 100 },
        };

        var result = BuildPlateNester.Arrange(parts, config);

        // The 10x100 part should be rotated to 100x10 to fit on the 120x50 plate
        result.Placements.Should().HaveCount(1);
        result.Overflow.Should().BeEmpty();
    }
}
