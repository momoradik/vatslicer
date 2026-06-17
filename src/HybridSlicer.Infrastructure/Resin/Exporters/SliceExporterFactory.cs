namespace HybridSlicer.Infrastructure.Resin.Exporters;

/// <summary>
/// Factory that maps a format string to the correct exporter implementation.
/// </summary>
public static class SliceExporterFactory
{
    /// <summary>
    /// Get an exporter for the specified format string.
    /// Supported: "ctb", "cbddlp", "photon", "sl1", "zip", "pwmx"
    /// </summary>
    public static ISliceExporter? GetExporter(string format)
    {
        return format?.ToLowerInvariant() switch
        {
            "ctb" => new CtbExporter(),
            "cbddlp" => new PhotonExporter(cbddlp: true),
            "photon" => new PhotonExporter(cbddlp: false),
            "sl1" or "sl1s" => new ZipPngExporter(),
            "zip" or "png" => new ZipPngExporter(),
            _ => null,
        };
    }

    /// <summary>
    /// Get all supported format names for UI display.
    /// </summary>
    public static IReadOnlyList<(string format, string name, string extension)> SupportedFormats => new[]
    {
        ("ctb", "ChiTuBox CTB v3", ".ctb"),
        ("cbddlp", "Anycubic CBDDLP", ".cbddlp"),
        ("photon", "Anycubic Photon", ".photon"),
        ("sl1", "Prusa SL1 (ZIP+PNG)", ".sl1"),
        ("zip", "Generic ZIP+PNG", ".zip"),
    };
}
