using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class BuildPlateUtilizationTests
{
    [Fact]
    public void SingleSmallModel_LowUtilization()
    {
        var r = BuildPlateUtilization.Analyze(
            new[] { (10f, 10f) }, 192f, 120f);
        r.UtilizationPct.Should().BeLessThan(10);
        r.MaxCopies.Should().BeGreaterThan(10);
    }

    [Fact]
    public void FullPlate_HighUtilization()
    {
        var models = Enumerable.Range(0, 50).Select(_ => (15f, 15f)).ToArray();
        var r = BuildPlateUtilization.Analyze(models, 192f, 120f);
        r.UtilizationPct.Should().BeGreaterThan(30);
    }

    [Fact]
    public void Suggestion_NotEmpty()
    {
        var r = BuildPlateUtilization.Analyze(new[] { (20f, 20f) }, 192f, 120f);
        r.Suggestion.Should().NotBeNullOrEmpty();
    }
}
