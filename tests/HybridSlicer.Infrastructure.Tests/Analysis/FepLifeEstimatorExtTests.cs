using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class FepLifeEstimatorExtTests
{
    [Fact]
    public void NewFilm_StatusNew()
    {
        FepLifeEstimator.Estimate(0, 1f).Status.Should().Be("new");
    }

    [Fact]
    public void VeryUsedFilm_StatusReplace()
    {
        FepLifeEstimator.Estimate(200, 2f, "FEP").Status.Should().Be("replace");
    }

    [Fact]
    public void Status_IsOneOfValidValues()
    {
        var validStatuses = new[] { "new", "good", "worn", "replace" };
        for (float h = 0; h <= 100; h += 10)
        {
            var e = FepLifeEstimator.Estimate(h, 1f);
            validStatuses.Should().Contain(e.Status);
        }
    }

    [Fact]
    public void HighForce_ReducesLife()
    {
        var lowForce = FepLifeEstimator.Estimate(30, 0.5f);
        var highForce = FepLifeEstimator.Estimate(30, 3f);
        highForce.RemainingLifePct.Should().BeLessThan(lowForce.RemainingLifePct);
    }
}
