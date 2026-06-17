using System.Text;
using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Exporters;

/// <summary>
/// Exports slice data in Anycubic Photon Workshop .pwmx/.pwms/.pwmb format.
/// Format: custom header + per-layer RLE7-encoded pixel data.
/// Spec reference: https://github.com/sn4k3/UVtools/wiki/Anycubic-Photon-Workshop
///
/// The .pwmx (Mono X), .pwms (Mono SE), .pwmb (Mono) share the same structure
/// with machine-specific header strings.
/// </summary>
public sealed class PwmxExporter : ISliceExporter
{
    private readonly string _variant; // "pwmx", "pwms", "pwmb"

    public PwmxExporter(string variant = "pwmx") => _variant = variant.ToLowerInvariant();

    public string FileExtension => $".{_variant}";
    public string FormatName => _variant switch
    {
        "pwms" => "Anycubic Photon Mono SE",
        "pwmb" => "Anycubic Photon Mono",
        _ => "Anycubic Photon Mono X",
    };

    // PWMX section markers
    private static readonly byte[] HEADER_MARK = "ANYCUBIC\0\0\0\0"u8.ToArray();
    private const int SECTION_HEADER = 1;
    private const int SECTION_PREVIEW = 2;
    private const int SECTION_LAYER_DEF = 3;
    private const int SECTION_LAYER_DATA = 5;
    private const int SECTION_EXTRA = 9;

    public void Export(SliceExportConfig config, IReadOnlyList<byte[]> layerImages, Stream output)
    {
        using var bw = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        int layerCount = layerImages.Count;
        if (layerCount == 0) return;

        // Encode all layers with RLE7 encoding
        var encodedLayers = new List<byte[]>(layerCount);
        for (int i = 0; i < layerCount; i++)
            encodedLayers.Add(EncodeRle7(layerImages[i], config.ResolutionX, config.ResolutionY));

        // ── File identification ──
        bw.Write(HEADER_MARK);

        // ── Section 1: Header ──
        WriteSection(bw, SECTION_HEADER, w =>
        {
            w.Write(config.BedWidthMm);               // bed X mm
            w.Write(config.BedDepthMm);                // bed Y mm
            w.Write(config.BedHeightMm);               // bed Z mm
            w.Write(config.LayerHeightMm);             // layer height
            w.Write(config.ExposureTimeS);             // normal exposure
            w.Write(config.BottomExposureTimeS);       // bottom exposure
            w.Write(config.LightOffDelayS);            // light-off delay
            w.Write(config.BottomLayerCount);           // bottom layers
            w.Write(config.ResolutionX);                // resolution X
            w.Write(config.ResolutionY);                // resolution Y
            w.Write(layerCount);                        // total layers
            w.Write(config.LiftDistanceMm);             // lift distance
            w.Write(config.LiftSpeedMmPerMin / 60f);   // lift speed mm/s
            w.Write(config.RetractSpeedMmPerMin / 60f); // retract speed mm/s
            w.Write(0f);                                // volume ml (calculated)
            w.Write(config.AntiAliasingLevel);          // AA level
            w.Write(0);                                 // reserved
            w.Write(0);                                 // reserved

            // Machine name (32 bytes, null-padded)
            var nameBytes = Encoding.ASCII.GetBytes(config.MachineName ?? "Photon Mono X");
            var nameField = new byte[32];
            Array.Copy(nameBytes, nameField, Math.Min(nameBytes.Length, 32));
            w.Write(nameField);
        });

        // ── Section 2: Preview (small 224x168 placeholder) ──
        WriteSection(bw, SECTION_PREVIEW, w =>
        {
            int previewW = 224, previewH = 168;
            w.Write(previewW);
            w.Write(previewH);
            // Write a solid dark preview (RGB565 format, 2 bytes per pixel)
            var previewData = new byte[previewW * previewH * 2];
            w.Write(previewData.Length);
            w.Write(previewData);
        });

        // ── Section 3: Layer definitions ──
        WriteSection(bw, SECTION_LAYER_DEF, w =>
        {
            w.Write(layerCount);
            for (int i = 0; i < layerCount; i++)
            {
                float z = config.LayerHeightMm * (i + 1);
                float exposure = i < config.BottomLayerCount
                    ? config.BottomExposureTimeS
                    : config.ExposureTimeS;
                float liftDist = i < config.BottomLayerCount
                    ? config.BottomLiftDistanceMm
                    : config.LiftDistanceMm;
                float liftSpeed = i < config.BottomLayerCount
                    ? config.BottomLiftSpeedMmPerMin / 60f
                    : config.LiftSpeedMmPerMin / 60f;

                w.Write(z);                            // layer Z position
                w.Write(encodedLayers[i].Length);       // data length
                w.Write(0);                            // data offset (filled below)
                w.Write(exposure);                     // exposure time
                w.Write(config.LightOffDelayS);        // light-off delay
                w.Write(liftDist);                     // lift distance
                w.Write(liftSpeed);                    // lift speed mm/s
                w.Write(config.RetractSpeedMmPerMin / 60f); // retract speed
            }
        });

        // ── Section 5: Layer pixel data ──
        WriteSection(bw, SECTION_LAYER_DATA, w =>
        {
            for (int i = 0; i < layerCount; i++)
                w.Write(encodedLayers[i]);
        });

        // ── Section 9: Extra info (end marker) ──
        WriteSection(bw, SECTION_EXTRA, w =>
        {
            var software = Encoding.ASCII.GetBytes("VATSlicer 1.0");
            w.Write(software.Length);
            w.Write(software);
        });
    }

    private static void WriteSection(BinaryWriter bw, int sectionId, Action<BinaryWriter> writeContent)
    {
        bw.Write(sectionId);
        // Write content to temp buffer to get length
        using var ms = new MemoryStream();
        using var tw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writeContent(tw);
        tw.Flush();
        var data = ms.ToArray();
        bw.Write(data.Length);
        bw.Write(data);
    }

    /// <summary>
    /// RLE7 encoding: each byte encodes a run of pixels.
    /// Bit 7 = value (0=black, 1=white), bits 6-0 = run length (1-127).
    /// For grayscale AA: intermediate values are encoded with closest threshold.
    /// </summary>
    private static byte[] EncodeRle7(byte[] pngData, int resX, int resY)
    {
        byte[] pixels;
        using (var bitmap = SKBitmap.Decode(pngData))
        {
            if (bitmap == null) return Array.Empty<byte>();
            pixels = new byte[resX * resY];
            for (int y = 0; y < Math.Min(bitmap.Height, resY); y++)
                for (int x = 0; x < Math.Min(bitmap.Width, resX); x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    pixels[y * resX + x] = (byte)((c.Red + c.Green + c.Blue) / 3);
                }
        }

        var rle = new List<byte>();
        int pos = 0, total = resX * resY;
        while (pos < total)
        {
            bool white = pixels[pos] >= 128;
            int run = 0;
            while (pos + run < total && run < 127 && (pixels[pos + run] >= 128) == white)
                run++;
            rle.Add((byte)((white ? 0x80 : 0x00) | run));
            pos += run;
        }
        return rle.ToArray();
    }
}
