using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates difference images between two layer renders.
/// Useful for visualizing what supports add to each layer:
/// - Green = support-only pixels (support adds material)
/// - Red = model-only pixels (should not happen in normal use)
/// - White = both layers have pixels (overlap)
/// </summary>
public static class LayerDiffGenerator
{
    public static byte[] GenerateDiff(byte[] layerAPng, byte[] layerBPng, int resX, int resY)
    {
        byte[] pixA, pixB;
        using (var bmpA = SKBitmap.Decode(layerAPng))
        using (var bmpB = SKBitmap.Decode(layerBPng))
        {
            pixA = new byte[resX * resY];
            pixB = new byte[resX * resY];
            for (int y = 0; y < Math.Min(bmpA?.Height ?? 0, resY); y++)
                for (int x = 0; x < Math.Min(bmpA?.Width ?? 0, resX); x++)
                    pixA[y * resX + x] = bmpA!.GetPixel(x, y).Red;
            for (int y = 0; y < Math.Min(bmpB?.Height ?? 0, resY); y++)
                for (int x = 0; x < Math.Min(bmpB?.Width ?? 0, resX); x++)
                    pixB[y * resX + x] = bmpB!.GetPixel(x, y).Red;
        }

        var info = new SKImageInfo(resX, resY, SKColorType.Rgba8888);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        using var bitmap = new SKBitmap(resX, resY, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < resY; y++)
        for (int x = 0; x < resX; x++)
        {
            bool a = pixA[y * resX + x] > 128;
            bool b = pixB[y * resX + x] > 128;
            SKColor color;
            if (a && b) color = SKColors.White;       // both
            else if (b && !a) color = new SKColor(0, 200, 100); // B only (green = support added)
            else if (a && !b) color = new SKColor(220, 50, 50); // A only (red = removed)
            else color = SKColors.Black;
            bitmap.SetPixel(x, y, color);
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
