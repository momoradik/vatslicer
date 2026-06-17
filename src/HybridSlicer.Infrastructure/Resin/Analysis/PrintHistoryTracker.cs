using System.Text.Json;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Tracks resin usage across print jobs for cost accounting.
/// Persists to a JSON file in the application data directory.
/// </summary>
public sealed class PrintHistoryTracker
{
    private readonly string _filePath;
    private PrintHistory _history;

    public sealed class PrintRecord
    {
        public string JobId { get; set; } = "";
        public string ModelName { get; set; } = "";
        public DateTimeOffset PrintedAt { get; set; }
        public float ResinVolumeMl { get; set; }
        public float ResinCostUsd { get; set; }
        public float PrintTimeMinutes { get; set; }
        public int LayerCount { get; set; }
        public string ExportFormat { get; set; } = "";
        public string PrinterName { get; set; } = "";
    }

    public sealed class PrintHistory
    {
        public List<PrintRecord> Records { get; set; } = new();
        public float TotalResinMl { get; set; }
        public float TotalCostUsd { get; set; }
        public float TotalPrintTimeMinutes { get; set; }
        public int TotalPrints { get; set; }
    }

    public PrintHistoryTracker(string? dataDir = null)
    {
        var dir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VATSlicer");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "print-history.json");
        _history = Load();
    }

    public PrintHistory GetHistory() => _history;

    public void AddRecord(PrintRecord record)
    {
        _history.Records.Add(record);
        _history.TotalResinMl += record.ResinVolumeMl;
        _history.TotalCostUsd += record.ResinCostUsd;
        _history.TotalPrintTimeMinutes += record.PrintTimeMinutes;
        _history.TotalPrints++;
        Save();
    }

    private PrintHistory Load()
    {
        try
        {
            if (File.Exists(_filePath))
                return JsonSerializer.Deserialize<PrintHistory>(File.ReadAllText(_filePath)) ?? new();
        }
        catch { }
        return new();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_filePath,
                JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
