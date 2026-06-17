using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintJobSummaryTests
{
    [Fact]
    public void Generate_FormatsCorrectly()
    {
        var summary = PrintJobSummaryGenerator.Generate(
            "test.stl", "Mono X", "Standard", 500,
            90f, 12.5f, 13.75f, 0.63f, 85, "B",
            new() { "2 thin walls" }, "review");

        summary.PrintTime.Should().Contain("hour");
        summary.Volume.Should().Contain("ml");
        summary.Cost.Should().Contain("$");
        summary.Grade.Should().Be("B");
        summary.IssueCount.Should().Be(1);
    }

    [Fact]
    public void ShortPrint_FormatsAsMinutes()
    {
        var summary = PrintJobSummaryGenerator.Generate(
            "small.stl", "Printer", "Standard", 50,
            30f, 2f, 2.2f, 0.1f, 95, "A", new(), "ready");

        summary.PrintTime.Should().Contain("min");
    }

    [Fact]
    public void LongPrint_FormatsAsDays()
    {
        var summary = PrintJobSummaryGenerator.Generate(
            "huge.stl", "Printer", "Standard", 5000,
            2000f, 200f, 220f, 10f, 70, "C", new() { "a", "b" }, "risky");

        summary.PrintTime.Should().Contain("day");
    }
}
