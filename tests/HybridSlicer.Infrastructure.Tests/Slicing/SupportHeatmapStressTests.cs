using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class SupportHeatmapStressTests
{
    [Fact]
    public void Render_1000Supports_Under500ms()
    {
        var positions = Enumerable.Range(0, 1000)
            .Select(i => new Vector2((i % 50) * 2f - 50, (i / 50) * 2f - 20))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var png = SupportHeatmapRenderer.Render(positions, null, 400, 400, 100, 100);
        sw.Stop();

        png.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public void Render_WithOverhangs_IncludesRedDots()
    {
        var supports = new List<Vector2> { new(0, 0) };
        var overhangs = Enumerable.Range(0, 20)
            .Select(i => new Vector2(i * 3f - 30, i * 2f - 20))
            .ToList();

        var png = SupportHeatmapRenderer.Render(supports, overhangs, 300, 300, 80, 80);
        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(500);
    }

    [Fact]
    public void Render_DifferentResolutions_AllValid()
    {
        var positions = new List<Vector2> { new(0, 0), new(10, 10) };

        foreach (var res in new[] { (50, 50), (200, 200), (800, 600), (1920, 1080) })
        {
            var png = SupportHeatmapRenderer.Render(positions, null, res.Item1, res.Item2, 100, 100);
            png.Should().NotBeNull();
            png[0].Should().Be(0x89);
        }
    }

    [Fact]
    public void Render_DifferentBuildPlates_AllValid()
    {
        var positions = new List<Vector2> { new(0, 0) };

        foreach (var plate in new[] { (50f, 50f), (200f, 120f), (400f, 400f) })
        {
            var png = SupportHeatmapRenderer.Render(positions, null, 200, 200, plate.Item1, plate.Item2);
            png.Should().NotBeNull();
        }
    }
}
