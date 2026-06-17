using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ElephantFootCompensatorTests
{
    [Fact]
    public void HighBottomExposure_LargerInset()
    {
        var low = ElephantFootCompensator.Calculate(5, 3000, 2000, 0.05f);
        var high = ElephantFootCompensator.Calculate(5, 10000, 2000, 0.05f);
        high.MaxInsetMm.Should().BeGreaterOrEqualTo(low.MaxInsetMm);
    }

    [Fact]
    public void EqualExposure_MinimalCompensation()
    {
        var plan = ElephantFootCompensator.Calculate(5, 2000, 2000, 0.05f);
        plan.MaxInsetMm.Should().BeLessOrEqualTo(0.01f);
    }

    [Fact]
    public void TransitionLayers_FadeOut()
    {
        var plan = ElephantFootCompensator.Calculate(3, 30000, 2000, 0.05f, transitionLayers: 3);
        plan.AffectedLayers.Should().Be(6);
        // Last transition layer should have less inset than first bottom layer
        plan.InsetPerLayerMm[^1].Should().BeLessThan(plan.InsetPerLayerMm[0]);
    }

    [Fact]
    public void Description_NotEmpty()
    {
        var plan = ElephantFootCompensator.Calculate(5, 30000, 2000, 0.05f);
        plan.Description.Should().Contain("Elephant foot");
    }
}
