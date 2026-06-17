using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates exposure test pattern images for resin calibration.
/// Creates a grid of squares at different grayscale intensities
/// to help users find the optimal exposure time for their resin.
/// </summary>
public static class ExposureTestPatternGenerator
{
    public sealed record TestPattern
    {
        public required byte[] PngData { get; init; }
        public required int SquareCount { get; init; }
        public required float MinExposurePct { get; init; }
        public required float MaxExposurePct { get; init; }
    }

    /// <summary>
    /// Generate an exposure test pattern with squares at varying intensity.
    /// </summary>
    public static TestPattern Generate(
        int resX = 1920, int resY = 1080,
        int columns = 5, int rows = 4,
        float minPct = 50f, float maxPct = 100f,
        float marginPct = 5f)
    {
        var info = new SKImageInfo(resX, resY, SKColorType.Gray8);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        float marginX = resX * marginPct / 100f;
        float marginY = resY * marginPct / 100f;
        float usableW = resX - 2 * marginX;
        float usableH = resY - 2 * marginY;
        float cellW = usableW / columns;
        float cellH = usableH / rows;
        float gap = Math.Min(cellW, cellH) * 0.1f;

        int totalSquares = columns * rows;
        int idx = 0;

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
        {
            float pct = minPct + (maxPct - minPct) * idx / Math.Max(1, totalSquares - 1);
            byte intensity = (byte)(pct / 100f * 255f);

            float x = marginX + c * cellW + gap;
            float y = marginY + r * cellH + gap;
            float w = cellW - 2 * gap;
            float h = cellH - 2 * gap;

            using var paint = new SKPaint { Color = new SKColor(intensity, intensity, intensity) };
            canvas.DrawRect(x, y, w, h, paint);

            // Label
            using var textPaint = new SKPaint
            {
                Color = intensity > 128 ? SKColors.Black : SKColors.White,
                
                IsAntialias = true,
            };
            using var font = new SKFont { Size = Math.Min(w, h) * 0.15f }; canvas.DrawText($"{pct:F0}%", x + w * 0.3f, y + h * 0.55f, SKTextAlign.Left, font, textPaint);
            idx++;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);

        return new TestPattern
        {
            PngData = data.ToArray(),
            SquareCount = totalSquares,
            MinExposurePct = minPct,
            MaxExposurePct = maxPct,
        };
    }
}
