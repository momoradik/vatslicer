using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PostProcessingAdvisorTests
{
    [Fact]
    public void Standard_HasIPAWash()
    {
        var plan = PostProcessingAdvisor.Generate("standard");
        plan.Steps.Should().Contain(s => s.Name.Contains("IPA"));
    }

    [Fact]
    public void WaterWashable_HasWaterRinse()
    {
        var plan = PostProcessingAdvisor.Generate("water-washable");
        plan.Steps.Should().Contain(s => s.Name.Contains("Water"));
    }

    [Fact]
    public void WithSupports_HasRemovalStep()
    {
        var plan = PostProcessingAdvisor.Generate(hasSupports: true);
        plan.Steps.Should().Contain(s => s.Name.Contains("Support"));
    }

    [Fact]
    public void Hollow_HasDrainStep()
    {
        var plan = PostProcessingAdvisor.Generate(isHollow: true);
        plan.Steps.Should().Contain(s => s.Name.Contains("Drain"));
    }

    [Fact]
    public void AlwaysHasUVCure()
    {
        var plan = PostProcessingAdvisor.Generate();
        plan.Steps.Should().Contain(s => s.Name.Contains("UV"));
        plan.TotalTimeMinutes.Should().BeGreaterThan(0);
    }
}
