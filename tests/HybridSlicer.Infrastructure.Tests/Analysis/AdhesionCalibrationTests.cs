using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class AdhesionCalibrationTests
{
    [Fact]
    public void Presets_ContainStandardResin()
    {
        AdhesionCalibration.Presets.Should().Contain(p => p.ResinCategory == "Standard");
    }

    [Fact]
    public void GetPAdh_ReturnsPresetForKnownResin()
    {
        var pAdh = AdhesionCalibration.GetPAdh("Standard", "FEP");
        pAdh.Should().BeApproximately(0.015f, 0.001f);
    }

    [Fact]
    public void GetPAdh_ReturnsDefaultForUnknownResin()
    {
        var pAdh = AdhesionCalibration.GetPAdh("UnknownResin", "FEP");
        pAdh.Should().Be(SupportSizer.P_ADH_DEFAULT);
    }

    [Fact]
    public void GetPAdh_ReturnsDefaultForNull()
    {
        var pAdh = AdhesionCalibration.GetPAdh(null, null);
        pAdh.Should().Be(SupportSizer.P_ADH_DEFAULT);
    }

    [Fact]
    public void GetPAdh_NFep_LowerThanFep()
    {
        var fep = AdhesionCalibration.GetPAdh("Standard", "FEP");
        var nfep = AdhesionCalibration.GetPAdh("Standard", "nFEP");
        nfep.Should().BeLessThan(fep, "nFEP has lower adhesion than FEP");
    }

    [Fact]
    public void AllPresets_HavePositivePAdh()
    {
        foreach (var preset in AdhesionCalibration.Presets)
        {
            preset.PAdhNPerMm2.Should().BeGreaterThan(0);
            preset.Confidence.Should().BeInRange(0, 1);
            preset.Name.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void SupportSizer_UsesCalibrationPAdh()
    {
        float ceramicPAdh = AdhesionCalibration.GetPAdh("Ceramic", "FEP");
        float standardPAdh = AdhesionCalibration.GetPAdh("Standard", "FEP");

        var ceramicSizing = SupportSizer.Size(20f, 100f, 5, true, ceramicPAdh);
        var standardSizing = SupportSizer.Size(20f, 100f, 5, true, standardPAdh);

        // Ceramic has higher adhesion → needs thicker supports
        ceramicSizing.TipRadius.Should().BeGreaterOrEqualTo(standardSizing.TipRadius,
            "ceramic resin needs thicker supports due to higher peel force");
    }
}
