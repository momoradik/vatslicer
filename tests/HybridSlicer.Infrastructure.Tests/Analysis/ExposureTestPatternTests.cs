using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ExposureTestPatternTests
{
    [Fact]
    public void Generate_ProducesValidPng()
    {
        var pattern = ExposureTestPatternGenerator.Generate(resX: 192, resY: 108);
        pattern.PngData.Should().NotBeEmpty();
        pattern.PngData.Length.Should().BeGreaterThan(100);
        pattern.SquareCount.Should().Be(20); // 5x4
    }

    [Fact]
    public void Generate_CustomGrid()
    {
        var pattern = ExposureTestPatternGenerator.Generate(192, 108, columns: 3, rows: 2, minPct: 60, maxPct: 100);
        pattern.SquareCount.Should().Be(6);
        pattern.MinExposurePct.Should().Be(60f);
        pattern.MaxExposurePct.Should().Be(100f);
    }
}
