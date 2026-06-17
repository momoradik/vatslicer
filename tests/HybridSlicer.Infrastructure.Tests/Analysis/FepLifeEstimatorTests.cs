using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class FepLifeEstimatorTests
{
    [Fact]
    public void NewFilm_HighLife()
    {
        var e = FepLifeEstimator.Estimate(0, 1f, "FEP");
        e.RemainingLifePct.Should().Be(100);
        e.Status.Should().Be("new");
    }

    [Fact]
    public void WornFilm_LowLife()
    {
        var e = FepLifeEstimator.Estimate(60, 2f, "FEP");
        e.RemainingLifePct.Should().BeLessThan(30);
        e.Status.Should().BeOneOf("worn", "replace");
    }

    [Fact]
    public void NFep_LongerLife()
    {
        var fep = FepLifeEstimator.Estimate(30, 1f, "FEP");
        var nfep = FepLifeEstimator.Estimate(30, 1f, "nFEP");
        nfep.RemainingLifePct.Should().BeGreaterThan(fep.RemainingLifePct);
    }
}
