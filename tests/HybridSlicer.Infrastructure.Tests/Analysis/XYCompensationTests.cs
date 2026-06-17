using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class XYCompensationTests
{
    [Fact]
    public void ZeroOffset_Unchanged()
    {
        var pixels = new byte[] { 0, 255, 0, 255 };
        var result = XYCompensationProcessor.Apply(pixels, 2, 2, 0);
        result.Should().BeEquivalentTo(pixels);
    }

    [Fact]
    public void Erode_ShrinksSinglePixel()
    {
        // 3x3 grid with center pixel white
        var pixels = new byte[9];
        pixels[4] = 255; // center
        var result = XYCompensationProcessor.Apply(pixels, 3, 3, -1);
        result[4].Should().Be(0, "isolated pixel eroded by radius 1 should disappear");
    }

    [Fact]
    public void Dilate_GrowsPixel()
    {
        var pixels = new byte[9];
        pixels[4] = 255; // center
        var result = XYCompensationProcessor.Apply(pixels, 3, 3, 1);
        // All neighbors should become white
        result.Count(b => b >= 128).Should().BeGreaterThan(1, "dilating center pixel should spread to neighbors");
    }
}
