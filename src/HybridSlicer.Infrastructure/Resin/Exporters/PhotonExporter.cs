using System.Text;
using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Exporters;

/// <summary>
/// Exports slice data in Anycubic .photon/.cbddlp format.
/// These formats share the same structure with minor version differences.
/// Format spec: https://github.com/sn4k3/UVtools/wiki/Photon-File-Format
/// </summary>
public sealed class PhotonExporter : ISliceExporter
{
    private readonly bool _isCbddlp;

    public PhotonExporter(bool cbddlp = false)
    {
        _isCbddlp = cbddlp;
    }

    public string FileExtension => _isCbddlp ? ".cbddlp" : ".photon";
    public string FormatName => _isCbddlp ? "Anycubic CBDDLP" : "Anycubic Photon";

    private const uint MAGIC_CBDDLP = 0x12FD_0066;
    private const uint MAGIC_PHOTON = 0x12FD_0066; // same magic, version differs

    public void Export(SliceExportConfig config, IReadOnlyList<byte[]> layerImages, Stream output)
    {
        using var bw = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        int layerCount = layerImages.Count;
        if (layerCount == 0) return;

        // Encode layers to RLE
        var encodedLayers = new List<byte[]>(layerCount);
        for (int i = 0; i < layerCount; i++)
            encodedLayers.Add(EncodeLayerRle(layerImages[i], config.ResolutionX, config.ResolutionY));

        // Header (same structure as CTB but with photon magic)
        int headerSize = 96;
        int layerTableOffset = headerSize;
        int layerDefSize = 36;
        int layerTableSize = layerDefSize * layerCount;
        int layerDataOffset = layerTableOffset + layerTableSize;

        // File header
        bw.Write(MAGIC_CBDDLP);
        bw.Write(_isCbddlp ? 3u : 2u);          // version
        bw.Write(config.BedWidthMm);
        bw.Write(config.BedDepthMm);
        bw.Write(config.BedHeightMm);
        bw.Write(0u); bw.Write(0u);
        bw.Write(config.ModelHeightMm);
        bw.Write(config.LayerHeightMm);
        bw.Write(config.ExposureTimeS);
        bw.Write(config.BottomExposureTimeS);
        bw.Write(config.LightOffDelayS);
        bw.Write(config.BottomLayerCount);
        bw.Write(config.ResolutionX);
        bw.Write(config.ResolutionY);
        bw.Write(0u);                             // preview offset
        bw.Write((uint)layerTableOffset);
        bw.Write((uint)layerCount);
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(config.AntiAliasingLevel);
        bw.Write(0u);

        // Layer table + data
        int currentDataOffset = layerDataOffset;
        for (int i = 0; i < layerCount; i++)
        {
            float z = config.LayerHeightMm * (i + 1);
            float exposure = i < config.BottomLayerCount ? config.BottomExposureTimeS : config.ExposureTimeS;

            bw.Write(z);
            bw.Write((uint)currentDataOffset);
            bw.Write((uint)encodedLayers[i].Length);
            bw.Write(0u); bw.Write(0u); bw.Write(0u);
            bw.Write(exposure);
            bw.Write(config.LightOffDelayS);
            bw.Write(0u);

            currentDataOffset += encodedLayers[i].Length;
        }

        for (int i = 0; i < layerCount; i++)
            bw.Write(encodedLayers[i]);
    }

    private static byte[] EncodeLayerRle(byte[] pngData, int resX, int resY)
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
            while (pos + run < total && run < 125 && (pixels[pos + run] >= 128) == white)
                run++;
            rle.Add((byte)((white ? 0x80 : 0x00) | run));
            pos += run;
        }
        return rle.ToArray();
    }
}
