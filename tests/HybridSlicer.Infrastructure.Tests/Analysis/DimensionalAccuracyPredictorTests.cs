using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class DimensionalAccuracyPredictorTests
{
    [Fact]
    public void FineSettings_BetterThanCoarse()
    {
        var fine = DimensionalAccuracyPredictor.Predict(0.03f, 0.025f, 1f, 0.01f);
        var coarse = DimensionalAccuracyPredictor.Predict(0.08f, 0.1f, 4f, 0.05f);
        fine.OverallDeviationMm.Should().BeLessThan(coarse.OverallDeviationMm);
    }

    [Fact]
    public void CoarseSettings_DraftGrade()
    {
        var p = DimensionalAccuracyPredictor.Predict(0.08f, 0.1f, 4f, 0.05f);
        p.Grade.Should().Be("draft");
        p.OverallDeviationMm.Should().BeGreaterThan(0.1f);
    }

    [Fact]
    public void Description_ContainsValues()
    {
        var p = DimensionalAccuracyPredictor.Predict(0.05f, 0.05f);
        p.Description.Should().Contain("XY").And.Contain("Z");
    }

    [Fact]
    public void HigherShrinkage_WorseAccuracy()
    {
        var low = DimensionalAccuracyPredictor.Predict(0.05f, 0.05f, 1f);
        var high = DimensionalAccuracyPredictor.Predict(0.05f, 0.05f, 5f);
        high.OverallDeviationMm.Should().BeGreaterThan(low.OverallDeviationMm);
    }
}
