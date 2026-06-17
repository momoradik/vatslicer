using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PixelBleedCompensatorTests
{
    [Fact]
    public void AllBlack_Unchanged()
    {
        var pixels = new byte[9]; // 3x3 all black
        var result = PixelBleedCompensator.ApplyEdgeFalloff(pixels, 3, 3);
        result.Should().BeEquivalentTo(pixels);
    }

    [Fact]
    public void EdgePixels_GetReducedIntensity()
    {
        // 5x5 with center 3x3 white
        var pixels = new byte[25];
        for (int y = 1; y <= 3; y++)
        for (int x = 1; x <= 3; x++)
            pixels[y * 5 + x] = 255;

        var result = PixelBleedCompensator.ApplyEdgeFalloff(pixels, 5, 5, falloffPixels: 1);
        // Edge pixels (row 1/3, col 1/3) should be dimmer than center
        var anyDimmed = result.Zip(pixels).Any(p => p.First < p.Second && p.Second >= 128);
        anyDimmed.Should().BeTrue("edge falloff should reduce some pixel intensities");
    }

    [Fact]
    public void ZeroFalloff_Unchanged()
    {
        var pixels = new byte[] { 0, 255, 0, 255, 255, 255, 0, 255, 0 };
        var result = PixelBleedCompensator.ApplyEdgeFalloff(pixels, 3, 3, falloffPixels: 0);
        result.Should().BeEquivalentTo(pixels);
    }
}
