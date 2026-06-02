using System.Numerics;
using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Slicing;

/// <summary>
/// Renders a top-down heatmap showing support density across the build plate.
/// Useful for visual verification: bright areas = well-supported, dark = sparse.
/// Red spots = uncovered overhangs that may fail during printing.
/// </summary>
public static class SupportHeatmapRenderer
{
    /// <summary>
    /// Render a top-down support density heatmap.
    /// </summary>
    /// <param name="supportPositions">XY positions of all support contact points.</param>
    /// <param name="overhangCentroids">XY positions of overhang region centroids (for gap detection).</param>
    /// <param name="resX">Image width in pixels.</param>
    /// <param name="resY">Image height in pixels.</param>
    /// <param name="buildWidthMm">Build plate width (mm).</param>
    /// <param name="buildDepthMm">Build plate depth (mm).</param>
    /// <param name="influenceRadiusMm">Radius of each support's influence zone (mm).</param>
    public static byte[] Render(
        List<Vector2> supportPositions,
        List<Vector2>? overhangCentroids,
        int resX, int resY,
        float buildWidthMm, float buildDepthMm,
        float influenceRadiusMm = 5f)
    {
        var info = new SKImageInfo(resX, resY, SKColorType.Rgba8888);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(20, 20, 30)); // dark background

        float scaleX = resX / buildWidthMm;
        float scaleY = resY / buildDepthMm;
        float offsetX = resX / 2f;
        float offsetY = resY / 2f;

        // Draw support influence zones (green, additive)
        using var supportPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        foreach (var pos in supportPositions)
        {
            float px = pos.X * scaleX + offsetX;
            float py = pos.Y * scaleY + offsetY;
            float pr = influenceRadiusMm * Math.Max(scaleX, scaleY);

            // Gradient: bright center, fading edge
            using var shader = SKShader.CreateRadialGradient(
                new SKPoint(px, py), pr,
                new[] { new SKColor(0, 200, 100, 40), new SKColor(0, 100, 50, 0) },
                SKShaderTileMode.Clamp);
            supportPaint.Shader = shader;
            canvas.DrawCircle(px, py, pr, supportPaint);
        }

        // Draw support points as bright dots
        using var dotPaint = new SKPaint
        {
            Color = new SKColor(45, 212, 191), // teal
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        foreach (var pos in supportPositions)
        {
            float px = pos.X * scaleX + offsetX;
            float py = pos.Y * scaleY + offsetY;
            canvas.DrawCircle(px, py, 2, dotPaint);
        }

        // Draw uncovered overhang centroids as red warning dots
        if (overhangCentroids is { Count: > 0 })
        {
            using var warnPaint = new SKPaint
            {
                Color = new SKColor(239, 68, 68), // red
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            foreach (var pos in overhangCentroids)
            {
                float px = pos.X * scaleX + offsetX;
                float py = pos.Y * scaleY + offsetY;
                canvas.DrawCircle(px, py, 4, warnPaint);

                // Red ring around uncovered area
                using var ringPaint = new SKPaint
                {
                    Color = new SKColor(239, 68, 68, 100),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1,
                };
                canvas.DrawCircle(px, py, influenceRadiusMm * Math.Max(scaleX, scaleY), ringPaint);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
