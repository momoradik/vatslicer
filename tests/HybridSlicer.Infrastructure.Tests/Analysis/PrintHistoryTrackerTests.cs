using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintHistoryTrackerTests
{
    [Fact]
    public void AddRecord_UpdatesTotals()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"vatslicer-test-{Guid.NewGuid():N}");
        try
        {
            var tracker = new PrintHistoryTracker(tmpDir);
            tracker.AddRecord(new PrintHistoryTracker.PrintRecord
            {
                JobId = "j1", ModelName = "test.stl", PrintedAt = DateTimeOffset.UtcNow,
                ResinVolumeMl = 5f, ResinCostUsd = 0.25f, PrintTimeMinutes = 30f,
                LayerCount = 100, ExportFormat = "ctb", PrinterName = "TestPrinter",
            });
            var h = tracker.GetHistory();
            h.TotalPrints.Should().Be(1);
            h.TotalResinMl.Should().Be(5f);
            h.TotalCostUsd.Should().Be(0.25f);
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"vatslicer-test-{Guid.NewGuid():N}");
        try
        {
            var tracker1 = new PrintHistoryTracker(tmpDir);
            tracker1.AddRecord(new PrintHistoryTracker.PrintRecord
            {
                JobId = "j2", ModelName = "part.stl", ResinVolumeMl = 10f,
                ResinCostUsd = 0.5f, PrintTimeMinutes = 60f, LayerCount = 200,
            });

            // Reload from same directory
            var tracker2 = new PrintHistoryTracker(tmpDir);
            tracker2.GetHistory().TotalPrints.Should().Be(1);
            tracker2.GetHistory().TotalResinMl.Should().Be(10f);
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }
}
