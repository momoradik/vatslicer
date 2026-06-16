using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportSizerManualOverrideTests
{
    [Fact]
    public void Size_WithoutOverrides_ReturnsPhysicsValues()
    {
        var sizing = SupportSizer.Size(
            supportHeight: 50f, layerArea: 100f,
            supportsInLayer: 10, rootsOnPlate: true);

        sizing.TipRadius.Should().BeGreaterThan(0);
        sizing.PillarRadius.Should().BeGreaterThan(0);
        sizing.BaseRadius.Should().BeGreaterThan(0);
        sizing.RecommendedTipRadius.Should().Be(sizing.TipRadius);
        sizing.RecommendedPillarRadius.Should().Be(sizing.PillarRadius);
    }

    [Fact]
    public void Size_WithManualOverrides_OverridesPhysicsValues()
    {
        var ov = new SupportSizer.ManualOverrides
        {
            TipRadiusMm = 0.5f,
            PillarRadiusMm = 0.8f,
            BaseRadiusMm = 2.0f,
            BaseHeightMm = 1.5f,
            ContactDepthMm = 0.4f,
        };

        var sizing = SupportSizer.Size(
            supportHeight: 50f, layerArea: 100f,
            supportsInLayer: 10, rootsOnPlate: true, ov: ov);

        sizing.TipRadius.Should().Be(0.5f);
        sizing.PillarRadius.Should().Be(0.8f);
        sizing.BaseRadius.Should().Be(2.0f);
        sizing.BaseHeight.Should().Be(1.5f);
        sizing.ContactDepth.Should().Be(0.4f);

        // Recommended values should still reflect physics
        sizing.RecommendedTipRadius.Should().NotBe(0.5f);
        sizing.RecommendedPillarRadius.Should().NotBe(0.8f);
    }

    [Fact]
    public void Size_PartialOverrides_MixesPhysicsAndManual()
    {
        var ov = new SupportSizer.ManualOverrides
        {
            PillarRadiusMm = 1.0f,
            // Leave tip, base, etc. null -> physics values
        };

        var sizing = SupportSizer.Size(
            supportHeight: 50f, layerArea: 100f,
            supportsInLayer: 10, rootsOnPlate: true, ov: ov);

        sizing.PillarRadius.Should().Be(1.0f);
        // Tip should still be physics-computed
        sizing.TipRadius.Should().BeGreaterThanOrEqualTo(SupportSizer.R_TIP_MIN);
        sizing.RecommendedPillarRadius.Should().NotBe(1.0f);
    }

    [Fact]
    public void ManualOverrides_IsEmpty_WhenAllNull()
    {
        var ov = new SupportSizer.ManualOverrides();
        ov.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ManualOverrides_IsNotEmpty_WhenAnySet()
    {
        var ov = new SupportSizer.ManualOverrides { TipRadiusMm = 0.3f };
        ov.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Size_NullOverrides_SameAsDefault()
    {
        var withNull = SupportSizer.Size(50f, 100f, 10, true, ov: null);
        var withDefault = SupportSizer.Size(50f, 100f, 10, true);

        withNull.TipRadius.Should().Be(withDefault.TipRadius);
        withNull.PillarRadius.Should().Be(withDefault.PillarRadius);
        withNull.BaseRadius.Should().Be(withDefault.BaseRadius);
    }
}
