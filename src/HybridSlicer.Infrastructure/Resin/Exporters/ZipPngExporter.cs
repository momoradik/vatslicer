using System.IO.Compression;
using System.Text;

namespace HybridSlicer.Infrastructure.Resin.Exporters;

/// <summary>
/// Generic ZIP+PNG exporter — packages layer PNGs into a ZIP archive with metadata.
/// Compatible with Prusa SL1/SL1S format (ZIP containing config.ini + layer PNGs).
/// Also serves as a universal exchange format readable by UVtools and most slicers.
/// </summary>
public sealed class ZipPngExporter : ISliceExporter
{
    public string FileExtension => ".sl1";
    public string FormatName => "ZIP+PNG (SL1 compatible)";

    public void Export(SliceExportConfig config, IReadOnlyList<byte[]> layerImages, Stream output)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // config.ini — Prusa SL1 metadata format
        var configIni = new StringBuilder();
        configIni.AppendLine("[printer]");
        configIni.AppendLine($"displayWidth = {config.BedWidthMm:F2}");
        configIni.AppendLine($"displayHeight = {config.BedDepthMm:F2}");
        configIni.AppendLine($"maxPrintHeight = {config.BedHeightMm:F2}");
        configIni.AppendLine($"resolutionX = {config.ResolutionX}");
        configIni.AppendLine($"resolutionY = {config.ResolutionY}");
        configIni.AppendLine($"machineName = {config.MachineName}");
        configIni.AppendLine();
        configIni.AppendLine("[print]");
        configIni.AppendLine($"layerHeight = {config.LayerHeightMm:F3}");
        configIni.AppendLine($"expTime = {config.ExposureTimeS:F1}");
        configIni.AppendLine($"expTimeFirst = {config.BottomExposureTimeS:F1}");
        configIni.AppendLine($"numFade = {config.BottomLayerCount}");
        configIni.AppendLine($"numFast = {layerImages.Count - config.BottomLayerCount}");
        configIni.AppendLine($"numSlow = {config.BottomLayerCount}");
        configIni.AppendLine($"usedMaterial = 0");
        configIni.AppendLine($"printTime = 0");
        configIni.AppendLine();
        configIni.AppendLine("[output]");
        configIni.AppendLine($"numLayers = {layerImages.Count}");
        configIni.AppendLine($"outputDir = .");

        var configEntry = archive.CreateEntry("config.ini");
        using (var writer = new StreamWriter(configEntry.Open()))
            writer.Write(configIni.ToString());

        // Layer PNGs
        for (int i = 0; i < layerImages.Count; i++)
        {
            var layerEntry = archive.CreateEntry($"{config.MachineName}/{i:D5}.png");
            using var entryStream = layerEntry.Open();
            entryStream.Write(layerImages[i]);
        }
    }
}
