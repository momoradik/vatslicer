using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class OptimalLayerHeightTests
{
    [Fact]
    public void Standard_ReturnsThreeOptions()
    {
        var options = OptimalLayerHeightCalculator.Calculate(20f);
        options.Should().HaveCount(3);
        options.Should().Contain(o => o.Recommended);
    }

    [Fact]
    public void Precision_HasFinerLayers()
    {
        var precision = OptimalLayerHeightCalculator.Calculate(20f, quality: "precision");
        var draft = OptimalLayerHeightCalculator.Calculate(20f, quality: "draft");
        precision.Min(o => o.HeightMm).Should().BeLessThan(draft.Min(o => o.HeightMm));
    }

    [Fact]
    public void ThinnerLayers_MoreLayers()
    {
        var options = OptimalLayerHeightCalculator.Calculate(10f);
        var sorted = options.OrderBy(o => o.HeightMm).ToList();
        sorted[0].EstimatedLayers.Should().BeGreaterThan(sorted[^1].EstimatedLayers);
    }

    [Fact]
    public void ThinnerLayers_HigherQuality()
    {
        var options = OptimalLayerHeightCalculator.Calculate(10f);
        var sorted = options.OrderBy(o => o.HeightMm).ToList();
        sorted[0].QualityScore.Should().BeGreaterThan(sorted[^1].QualityScore);
    }
}
