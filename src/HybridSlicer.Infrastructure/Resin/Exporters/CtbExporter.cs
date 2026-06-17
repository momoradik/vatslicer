using System.Text;
using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Exporters;

/// <summary>
/// Exports slice data in ChiTuBox .ctb v3 format.
/// Format spec: https://github.com/sn4k3/UVtools/wiki/ChiTuBox-CTB-File-Format
///
/// Structure: FileHeader → PrintParams → SlicerInfo → LayerTable → LayerData (RLE encoded)
/// </summary>
public sealed class CtbExporter : ISliceExporter
{
    public string FileExtension => ".ctb";
    public string FormatName => "ChiTuBox CTB v3";

    // CTB magic numbers
    private const uint MAGIC_CTB = 0x12FD_0086;
    private const uint VERSION = 3;

    public void Export(SliceExportConfig config, IReadOnlyList<byte[]> layerImages, Stream output)
    {
        using var bw = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        int layerCount = layerImages.Count;
        if (layerCount == 0) return;

        // Encode all layers to RLE first (need sizes for offset table)
        var encodedLayers = new List<byte[]>(layerCount);
        for (int i = 0; i < layerCount; i++)
        {
            var rle = EncodeLayerRle(layerImages[i], config.ResolutionX, config.ResolutionY);
            encodedLayers.Add(rle);
        }

        // ── Compute offsets ──
        int headerSize = 96;          // FileHeader (24 fields × 4 bytes)
        int printParamsOffset = headerSize;
        int printParamsSize = 48;     // PrintParams (12 fields × 4 bytes)
        int slicerInfoOffset = printParamsOffset + printParamsSize;
        int slicerInfoSize = 80;      // SlicerInfo (20 fields × 4 bytes)
        // Machine name string sits between slicer info and layer table
        var earlyMachineNameBytes = Encoding.UTF8.GetBytes(config.MachineName ?? "VATSlicer");
        int machineNameSize = earlyMachineNameBytes.Length;
        int layerTableOffset = slicerInfoOffset + slicerInfoSize + machineNameSize;
        int layerDefSize = 36;        // per-layer definition (9 fields × 4 bytes)
        int layerTableSize = layerDefSize * layerCount;
        int layerDataOffset = layerTableOffset + layerTableSize;

        // ── FileHeader (96 bytes) ──
        bw.Write(MAGIC_CTB);                              // 0: magic
        bw.Write(VERSION);                                // 4: version
        bw.Write(config.BedWidthMm);                      // 8: bed X mm
        bw.Write(config.BedDepthMm);                      // 12: bed Y mm
        bw.Write(config.BedHeightMm);                     // 16: bed Z mm
        bw.Write(0u);                                     // 20: padding
        bw.Write(0u);                                     // 24: padding
        bw.Write(config.ModelHeightMm);                   // 28: overall height mm
        bw.Write(config.LayerHeightMm);                   // 32: layer height mm
        bw.Write(config.ExposureTimeS);                   // 36: exposure time s
        bw.Write(config.BottomExposureTimeS);             // 40: bottom exposure s
        bw.Write(config.LightOffDelayS);                  // 44: light-off delay s
        bw.Write(config.BottomLayerCount);                // 48: bottom layer count
        bw.Write(config.ResolutionX);                     // 52: resolution X
        bw.Write(config.ResolutionY);                     // 56: resolution Y
        bw.Write(0u);                                     // 60: high-res preview offset (unused)
        bw.Write((uint)layerTableOffset);                 // 64: layer table offset
        bw.Write((uint)layerCount);                       // 68: layer count
        bw.Write(0u);                                     // 72: low-res preview offset (unused)
        bw.Write((uint)printParamsOffset);                // 76: print params offset
        bw.Write((uint)slicerInfoOffset);                 // 80: slicer info offset
        bw.Write(0u);                                     // 84: padding
        bw.Write(config.AntiAliasingLevel);               // 88: AA level
        bw.Write(0u);                                     // 92: padding

        // Estimate volume from total RLE data size (rough: white pixels × pixel area × layer height)
        float pixelAreaMm2 = (config.BedWidthMm / config.ResolutionX) * (config.BedDepthMm / config.ResolutionY);
        float estVolumeMl = 0;
        foreach (var rle in encodedLayers)
        {
            int whitePixels = 0;
            foreach (byte b in rle) { if ((b & 0x80) != 0) whitePixels += b & 0x7F; }
            estVolumeMl += whitePixels * pixelAreaMm2 * config.LayerHeightMm;
        }
        estVolumeMl /= 1000f; // mm3 → ml

        // ── PrintParams (48 bytes) ──
        bw.Write(config.BottomLiftDistanceMm);            // 0: bottom lift distance
        bw.Write(config.BottomLiftSpeedMmPerMin / 60f);   // 4: bottom lift speed mm/s
        bw.Write(config.LiftDistanceMm);                  // 8: lift distance
        bw.Write(config.LiftSpeedMmPerMin / 60f);         // 12: lift speed mm/s
        bw.Write(config.RetractSpeedMmPerMin / 60f);      // 16: retract speed mm/s
        bw.Write(estVolumeMl);                             // 20: volume ml
        bw.Write(config.AntiAliasingLevel);               // 24: AA level
        bw.Write(estVolumeMl * 1.1f);                     // 28: weight g (~1.1g/ml resin)
        bw.Write(estVolumeMl * 0.05f);                    // 32: cost (~$0.05/ml)
        bw.Write(config.RetractSpeedMmPerMin / 60f);      // 36: bottom retract speed
        bw.Write(0u);                                     // 40: padding
        bw.Write(0u);                                     // 44: padding

        // Machine name string (appended after slicer info)
        var machineNameBytes = Encoding.UTF8.GetBytes(config.MachineName ?? "VATSlicer");
        int machineNameOffset = slicerInfoOffset + 80; // right after slicer info

        // ── SlicerInfo (80 bytes) ──
        bw.Write(config.BottomLiftDistanceMm);            // 0
        bw.Write(config.BottomLiftSpeedMmPerMin / 60f);   // 4
        bw.Write(config.LiftDistanceMm);                  // 8
        bw.Write(config.LiftSpeedMmPerMin / 60f);         // 12
        bw.Write(config.RetractSpeedMmPerMin / 60f);      // 16
        bw.Write(0f);                                     // 20: rest time after retract
        bw.Write(0f);                                     // 24: rest time after lift
        bw.Write(0f);                                     // 28: rest time before lift
        bw.Write(0f);                                     // 32: bottom rest time
        bw.Write(0f);                                     // 36: bottom rest time after lift
        bw.Write((uint)machineNameOffset);                // 40: machine name offset
        bw.Write((uint)machineNameBytes.Length);           // 44: machine name size
        bw.Write(0u);                                     // 48: encryption key (0 = no encryption)
        bw.Write(0u);                                     // 52: per-layer settings offset
        bw.Write(0u);                                     // 56: mystery id
        bw.Write(0u);                                     // 60: anti-alias level
        bw.Write(0u);                                     // 64: software version
        bw.Write(0u);                                     // 68: rest time after retract
        bw.Write(0u);                                     // 72: rest time after lift
        bw.Write(0u);                                     // 76: transition layer count

        // ── Machine Name String ──
        bw.Write(machineNameBytes);

        // ── Layer Table + Data ──
        int currentDataOffset = layerDataOffset;
        for (int i = 0; i < layerCount; i++)
        {
            float z = config.LayerHeightMm * (i + 1);
            float exposure = i < config.BottomLayerCount ? config.BottomExposureTimeS : config.ExposureTimeS;
            float lightOff = config.LightOffDelayS;

            // Layer definition (36 bytes)
            bw.Write(z);                                   // 0: layer Z mm
            bw.Write((uint)currentDataOffset);             // 4: data offset
            bw.Write((uint)encodedLayers[i].Length);       // 8: data length
            bw.Write(0u);                                  // 12: padding
            bw.Write(0u);                                  // 16: table size (0 for v3)
            bw.Write(0u);                                  // 20: padding
            bw.Write(exposure);                            // 24: exposure time
            bw.Write(lightOff);                            // 28: light-off delay
            bw.Write(0u);                                  // 32: padding

            currentDataOffset += encodedLayers[i].Length;
        }

        // ── Layer Data (RLE encoded) ──
        for (int i = 0; i < layerCount; i++)
        {
            bw.Write(encodedLayers[i]);
        }
    }

    /// <summary>
    /// Encode a layer PNG to CTB RLE format.
    /// CTB RLE: each byte encodes a run — bit 7 = color (0=black, 1=white),
    /// bits 0-6 = run length (1-127). For runs > 127, chain multiple bytes.
    /// </summary>
    private static byte[] EncodeLayerRle(byte[] pngData, int resX, int resY)
    {
        // Decode PNG to grayscale pixel array
        byte[] pixels;
        using (var bitmap = SKBitmap.Decode(pngData))
        {
            if (bitmap == null)
                return Array.Empty<byte>();

            pixels = new byte[resX * resY];
            for (int y = 0; y < Math.Min(bitmap.Height, resY); y++)
            {
                for (int x = 0; x < Math.Min(bitmap.Width, resX); x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    // Use luminance (grayscale)
                    pixels[y * resX + x] = (byte)((color.Red + color.Green + color.Blue) / 3);
                }
            }
        }

        // CTB RLE: bit 7 = color (0=black, 1=white), bits 0-6 = run length (1-125)
        int totalPixels = resX * resY;
        var rle = new List<byte>(totalPixels / 10);
        int pos = 0;
        while (pos < totalPixels)
        {
            byte val = pixels[pos];
            bool white = val >= 128;
            int run = 0;
            while (pos + run < totalPixels && run < 125 && (pixels[pos + run] >= 128) == white)
                run++;

            rle.Add((byte)((white ? 0x80 : 0x00) | run));
            pos += run;
        }

        return rle.ToArray();
    }
}
