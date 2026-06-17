namespace HybridSlicer.Infrastructure.Resin.Exporters;

/// <summary>
/// Common interface for all printer file format exporters.
/// Each implementation packs layer images + metadata into a printer-specific binary format.
/// </summary>
public interface ISliceExporter
{
    /// <summary>File extension including dot (e.g. ".ctb", ".sl1").</summary>
    string FileExtension { get; }

    /// <summary>Human-readable format name.</summary>
    string FormatName { get; }

    /// <summary>
    /// Export a sliced job to a printer file.
    /// </summary>
    /// <param name="config">Print configuration (resolution, exposure, lift, etc.)</param>
    /// <param name="layerImages">Layer PNG images in order (bottom to top).</param>
    /// <param name="output">Stream to write the output file to.</param>
    void Export(SliceExportConfig config, IReadOnlyList<byte[]> layerImages, Stream output);
}

/// <summary>
/// Configuration for slice export — maps from MachineProfile + ResinPrintProfile fields.
/// </summary>
public sealed record SliceExportConfig
{
    // Machine
    public required int ResolutionX { get; init; }
    public required int ResolutionY { get; init; }
    public required float BedWidthMm { get; init; }
    public required float BedDepthMm { get; init; }
    public required float BedHeightMm { get; init; }
    public required string MachineName { get; init; }

    // Print profile
    public required float LayerHeightMm { get; init; }
    public required int BottomLayerCount { get; init; }
    public required float ExposureTimeS { get; init; }
    public required float BottomExposureTimeS { get; init; }
    public required float LiftDistanceMm { get; init; }
    public required float LiftSpeedMmPerMin { get; init; }
    public required float RetractSpeedMmPerMin { get; init; }
    public required float LightOffDelayS { get; init; }
    public required float BottomLiftDistanceMm { get; init; }
    public required float BottomLiftSpeedMmPerMin { get; init; }

    // Computed
    public float ModelHeightMm => LayerHeightMm * TotalLayers;
    public int TotalLayers { get; init; }

    // Anti-aliasing
    public int AntiAliasingLevel { get; init; } = 1; // 1 = off, 2/4/8 = grayscale levels
}
