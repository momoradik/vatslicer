using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ResinUsageForecasterTests
{
    [Fact]
    public void EnoughResin_ReturnsTrue()
    {
        var f = ResinUsageForecaster.Predict(500f, 100f, 50f);
        f.EnoughForJob.Should().BeTrue();
        f.BottleRemainingMl.Should().Be(400f);
        f.RemainingAfterJobMl.Should().Be(350f);
    }

    [Fact]
    public void NotEnoughResin_ReturnsFalse()
    {
        var f = ResinUsageForecaster.Predict(500f, 490f, 20f);
        f.EnoughForJob.Should().BeFalse();
        f.Message.Should().Contain("Not enough");
    }

    [Fact]
    public void EstimatesPrintsRemaining()
    {
        var f = ResinUsageForecaster.Predict(1000f, 200f, 50f, avgJobVolumeMl: 50f);
        f.EstimatedPrintsRemaining.Should().Be(16); // 800ml / 50ml per job
    }

    [Fact]
    public void LastPrint_WarnsUser()
    {
        var f = ResinUsageForecaster.Predict(500f, 440f, 50f, avgJobVolumeMl: 50f);
        f.Message.Should().Contain("last print");
    }
}
