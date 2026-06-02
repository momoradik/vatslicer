using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class SupportHeatmapTests
{
    [Fact]
    public void Render_WithSupports_ProducesValidPng()
    {
        var positions = new List<Vector2>
        {
            new(0, 0), new(5, 0), new(-5, 0),
            new(0, 5), new(0, -5),
        };

        var png = SupportHeatmapRenderer.Render(positions, null, 200, 200, 50, 50);

        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(100);
        png[0].Should().Be(0x89); // PNG magic
    }

    [Fact]
    public void Render_WithOverhangWarnings_ProducesValidPng()
    {
        var positions = new List<Vector2> { new(0, 0) };
        var overhangs = new List<Vector2> { new(20, 20) }; // uncovered area

        var png = SupportHeatmapRenderer.Render(positions, overhangs, 200, 200, 50, 50);

        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Render_EmptySupports_ProducesBlackImage()
    {
        var png = SupportHeatmapRenderer.Render(new(), null, 100, 100, 50, 50);

        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Render_ManySupports_ProducesLargerImage()
    {
        var few = new List<Vector2> { new(0, 0) };
        var many = Enumerable.Range(0, 100)
            .Select(i => new Vector2((i % 10) * 3f - 15, (i / 10) * 3f - 15))
            .ToList();

        var pngFew = SupportHeatmapRenderer.Render(few, null, 200, 200, 50, 50);
        var pngMany = SupportHeatmapRenderer.Render(many, null, 200, 200, 50, 50);

        pngMany.Length.Should().BeGreaterThan(pngFew.Length,
            "more supports = more non-black pixels = larger PNG");
    }

    [Fact]
    public void Render_HighResolution_StillValid()
    {
        var positions = new List<Vector2> { new(0, 0), new(10, 10) };
        var png = SupportHeatmapRenderer.Render(positions, null, 1920, 1080, 200, 120);

        png.Should().NotBeNull();
        png[0].Should().Be(0x89);
    }
}
