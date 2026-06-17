using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintQueueOptimizerTests
{
    [Fact]
    public void SameResin_OneBatch()
    {
        var items = new[]
        {
            new PrintQueueOptimizer.QueueItem { ModelName = "a", ResinType = "Standard", HeightMm = 30, VolumeMl = 5, EstimatedTimeMinutes = 60 },
            new PrintQueueOptimizer.QueueItem { ModelName = "b", ResinType = "Standard", HeightMm = 20, VolumeMl = 3, EstimatedTimeMinutes = 40 },
        };
        var q = PrintQueueOptimizer.Optimize(items);
        q.BatchCount.Should().Be(1);
        q.TotalVolumeMl.Should().Be(8);
    }

    [Fact]
    public void DifferentResins_MultipleBatches()
    {
        var items = new[]
        {
            new PrintQueueOptimizer.QueueItem { ModelName = "a", ResinType = "Standard", HeightMm = 30, VolumeMl = 5, EstimatedTimeMinutes = 60 },
            new PrintQueueOptimizer.QueueItem { ModelName = "b", ResinType = "Flexible", HeightMm = 20, VolumeMl = 3, EstimatedTimeMinutes = 40 },
        };
        var q = PrintQueueOptimizer.Optimize(items);
        q.BatchCount.Should().Be(2);
    }

    [Fact]
    public void BatchTime_IsTallestModel()
    {
        var items = new[]
        {
            new PrintQueueOptimizer.QueueItem { ModelName = "tall", ResinType = "Standard", HeightMm = 50, VolumeMl = 10, EstimatedTimeMinutes = 120 },
            new PrintQueueOptimizer.QueueItem { ModelName = "short", ResinType = "Standard", HeightMm = 10, VolumeMl = 2, EstimatedTimeMinutes = 30 },
        };
        var q = PrintQueueOptimizer.Optimize(items);
        q.TotalTimeMinutes.Should().Be(120, "batch time = tallest model since they print concurrently");
    }

    [Fact]
    public void Empty_NoBatches()
    {
        var q = PrintQueueOptimizer.Optimize(Array.Empty<PrintQueueOptimizer.QueueItem>());
        q.BatchCount.Should().Be(0);
    }
}
