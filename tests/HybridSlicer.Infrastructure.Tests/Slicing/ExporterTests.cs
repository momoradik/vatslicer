using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Exporters;
using SkiaSharp;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// Tests for printer file format exporters.
/// </summary>
public class ExporterTests
{
    private static SliceExportConfig CreateTestConfig(int layers = 10) => new()
    {
        ResolutionX = 192,
        ResolutionY = 108,
        BedWidthMm = 192f,
        BedDepthMm = 108f,
        BedHeightMm = 250f,
        MachineName = "TestPrinter",
        LayerHeightMm = 0.05f,
        BottomLayerCount = 3,
        ExposureTimeS = 2.0f,
        BottomExposureTimeS = 30.0f,
        LiftDistanceMm = 5f,
        LiftSpeedMmPerMin = 120f,
        RetractSpeedMmPerMin = 240f,
        LightOffDelayS = 1.0f,
        BottomLiftDistanceMm = 8f,
        BottomLiftSpeedMmPerMin = 60f,
        TotalLayers = layers,
    };

    private static byte[] CreateTestLayerPng(int resX, int resY)
    {
        var info = new SKImageInfo(resX, resY, SKColorType.Gray8);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        // Draw a white circle in the center
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(resX / 2f, resY / 2f, Math.Min(resX, resY) / 4f, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    [Fact]
    public void CtbExporter_ProducesValidFile()
    {
        var config = CreateTestConfig(5);
        var layers = Enumerable.Range(0, 5)
            .Select(_ => CreateTestLayerPng(config.ResolutionX, config.ResolutionY))
            .ToList();

        var exporter = new CtbExporter();
        using var ms = new MemoryStream();
        exporter.Export(config, layers, ms);

        ms.Length.Should().BeGreaterThan(96 + 48 + 80, "CTB file should have header + params + data");

        // Verify magic number
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        br.ReadUInt32().Should().Be(0x12FD_0086, "CTB magic");
        br.ReadUInt32().Should().Be(3u, "CTB version 3");
    }

    [Fact]
    public void ZipPngExporter_ProducesValidZip()
    {
        var config = CreateTestConfig(3);
        var layers = Enumerable.Range(0, 3)
            .Select(_ => CreateTestLayerPng(config.ResolutionX, config.ResolutionY))
            .ToList();

        var exporter = new ZipPngExporter();
        using var ms = new MemoryStream();
        exporter.Export(config, layers, ms);

        ms.Length.Should().BeGreaterThan(100, "ZIP should have content");

        // Verify it's a valid ZIP
        ms.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        archive.Entries.Count.Should().BeGreaterThan(3, "should have config.ini + 3 layer PNGs");

        var configEntry = archive.GetEntry("config.ini");
        configEntry.Should().NotBeNull("should have config.ini");
    }

    [Fact]
    public void PhotonExporter_ProducesValidFile()
    {
        var config = CreateTestConfig(3);
        var layers = Enumerable.Range(0, 3)
            .Select(_ => CreateTestLayerPng(config.ResolutionX, config.ResolutionY))
            .ToList();

        var exporter = new PhotonExporter(cbddlp: true);
        using var ms = new MemoryStream();
        exporter.Export(config, layers, ms);

        ms.Length.Should().BeGreaterThan(96, "CBDDLP file should have header + data");

        ms.Position = 0;
        using var br = new BinaryReader(ms);
        br.ReadUInt32().Should().Be(0x12FD_0066, "CBDDLP magic");
    }

    [Fact]
    public void PwmxExporter_ProducesValidFile()
    {
        var config = CreateTestConfig(3);
        var layers = Enumerable.Range(0, 3)
            .Select(_ => CreateTestLayerPng(config.ResolutionX, config.ResolutionY))
            .ToList();

        var exporter = new PwmxExporter("pwmx");
        using var ms = new MemoryStream();
        exporter.Export(config, layers, ms);

        ms.Length.Should().BeGreaterThan(100, "PWMX file should have header + sections + data");

        // Verify ANYCUBIC header marker
        ms.Position = 0;
        var headerBytes = new byte[12];
        ms.Read(headerBytes, 0, 12);
        System.Text.Encoding.ASCII.GetString(headerBytes, 0, 8).Should().Be("ANYCUBIC", "PWMX header mark");
    }

    [Theory]
    [InlineData("pwmx")]
    [InlineData("pwms")]
    [InlineData("pwmb")]
    public void PwmxVariants_AllProduce(string variant)
    {
        var config = CreateTestConfig(2);
        var layers = Enumerable.Range(0, 2)
            .Select(_ => CreateTestLayerPng(config.ResolutionX, config.ResolutionY))
            .ToList();

        var exporter = new PwmxExporter(variant);
        exporter.FileExtension.Should().Be($".{variant}");

        using var ms = new MemoryStream();
        exporter.Export(config, layers, ms);
        ms.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Factory_ReturnsCorrectExporters()
    {
        SliceExporterFactory.GetExporter("ctb").Should().BeOfType<CtbExporter>();
        SliceExporterFactory.GetExporter("sl1").Should().BeOfType<ZipPngExporter>();
        SliceExporterFactory.GetExporter("cbddlp").Should().BeOfType<PhotonExporter>();
        SliceExporterFactory.GetExporter("photon").Should().BeOfType<PhotonExporter>();
        SliceExporterFactory.GetExporter("pwmx").Should().BeOfType<PwmxExporter>();
        SliceExporterFactory.GetExporter("pwms").Should().BeOfType<PwmxExporter>();
        SliceExporterFactory.GetExporter("pwmb").Should().BeOfType<PwmxExporter>();
        SliceExporterFactory.GetExporter("zip").Should().BeOfType<ZipPngExporter>();
        SliceExporterFactory.GetExporter("unknown").Should().BeNull();
    }
}
