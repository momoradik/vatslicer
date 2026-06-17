using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportEfficiencyAnalyzerTests
{
    [Fact]
    public void OptimalRatio_Efficient()
    {
        var r = SupportEfficiencyAnalyzer.Analyze(500, 500, 20, 5000);
        r.Efficiency.Should().Be("optimal");
        r.EstimatedWasteVolumeMm3.Should().Be(0);
    }

    [Fact]
    public void TooMuchSupport_OverSupported()
    {
        var r = SupportEfficiencyAnalyzer.Analyze(5000, 100, 50, 5000);
        r.Efficiency.Should().Be("over-supported");
        r.EstimatedWasteVolumeMm3.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TooLittleSupport_UnderSupported()
    {
        var r = SupportEfficiencyAnalyzer.Analyze(10, 500, 3, 5000);
        r.Efficiency.Should().Be("under-supported");
    }

    [Fact]
    public void Suggestion_AlwaysProvided()
    {
        var r = SupportEfficiencyAnalyzer.Analyze(200, 200, 10, 3000);
        r.Suggestion.Should().NotBeNullOrEmpty();
    }
}
