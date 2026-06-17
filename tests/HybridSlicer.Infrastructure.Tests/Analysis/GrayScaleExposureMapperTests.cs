using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class GrayScaleExposureMapperTests
{
    [Fact]
    public void White_FullExposure()
    {
        var m = GrayScaleExposureMapper.Map(255, 2000);
        m.EffectiveExposureMs.Should().BeApproximately(2000, 10);
        m.ExposurePct.Should().BeApproximately(100, 1);
    }

    [Fact]
    public void Black_ZeroExposure()
    {
        var m = GrayScaleExposureMapper.Map(0, 2000);
        m.EffectiveExposureMs.Should().Be(0);
    }

    [Fact]
    public void MidGray_LessThanHalf()
    {
        // Gamma 2.2: 50% gray ≈ 21.8% linear
        var m = GrayScaleExposureMapper.Map(128, 2000);
        m.ExposurePct.Should().BeLessThan(50, "gamma correction makes mid-gray much darker than 50%");
    }

    [Fact]
    public void HigherGray_MoreExposure()
    {
        var low = GrayScaleExposureMapper.Map(100, 2000);
        var high = GrayScaleExposureMapper.Map(200, 2000);
        high.EffectiveExposureMs.Should().BeGreaterThan(low.EffectiveExposureMs);
    }

    [Fact]
    public void GrayValueForDepth_ReturnsValid()
    {
        byte gray = GrayScaleExposureMapper.GrayValueForCureDepth(0.05f, 2000);
        gray.Should().BeInRange(0, 255);
    }
}
