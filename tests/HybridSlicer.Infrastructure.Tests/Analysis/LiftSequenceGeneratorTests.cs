using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class LiftSequenceGeneratorTests
{
    [Fact]
    public void TwoStage_ProducesValidSequence()
    {
        var seq = LiftSequenceGenerator.Generate(6f, 1.5f, 120f, 300f);
        seq.TotalLiftTimeSec.Should().BeGreaterThan(0);
        seq.Stage1DistMm.Should().BeGreaterThan(0);
        seq.Stage2DistMm.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void Stage1_SlowerThanStage2()
    {
        var seq = LiftSequenceGenerator.Generate(6f, 2f, 120f, 300f);
        seq.Stage1SpeedMmPerMin.Should().BeLessThan(seq.Stage2SpeedMmPerMin);
    }

    [Fact]
    public void HighForce_SlowerPeel()
    {
        var low = LiftSequenceGenerator.Generate(6f, 0.5f, 120f);
        var high = LiftSequenceGenerator.Generate(6f, 5f, 120f);
        high.Stage1SpeedMmPerMin.Should().BeLessThan(low.Stage1SpeedMmPerMin);
    }

    [Fact]
    public void TotalDist_EqualsInput()
    {
        var seq = LiftSequenceGenerator.Generate(8f);
        (seq.Stage1DistMm + seq.Stage2DistMm).Should().BeApproximately(8f, 0.01f);
    }
}
