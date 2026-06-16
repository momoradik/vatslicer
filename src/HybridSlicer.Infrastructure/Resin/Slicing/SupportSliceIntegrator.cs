using System.Numerics;
using SkiaSharp;

namespace HybridSlicer.Infrastructure.Resin.Slicing;

/// <summary>
/// Integrates support cross-sections into layer images.
///
/// For each layer:
/// 1. The model polygon is already rasterized by LayerRasterizer
/// 2. This class adds support circles on top, with optional adaptive exposure marking
///
/// Supports can be rendered in two modes:
/// - Full exposure: support pixels get same exposure as model (default)
/// - Adaptive exposure: support pixels are marked at reduced intensity
///   (e.g., 70%) so they cure enough to hold but are easier to remove
///
/// Also provides a method to render support-only layer images for
/// visual preview in the frontend.
/// </summary>
public static class SupportSliceIntegrator
{
    /// <summary>
    /// Render support circles onto an existing layer image.
    /// </summary>
    /// <param name="canvas">SkiaSharp canvas of the layer image</param>
    /// <param name="circles">Support cross-section circles for this layer</param>
    /// <param name="scaleX">Pixels per mm in X</param>
    /// <param name="scaleY">Pixels per mm in Y</param>
    /// <param name="offsetX">X offset in pixels (for centering)</param>
    /// <param name="offsetY">Y offset in pixels (for centering)</param>
    /// <param name="intensity">Pixel intensity 0-255. 255=full exposure, 178=70% for adaptive</param>
    public static void RenderSupportsOnLayer(
        SKCanvas canvas,
        List<AnalyticalSupportSlicer.SupportCircle> circles,
        float scaleX, float scaleY,
        float offsetX, float offsetY,
        byte intensity = 255)
    {
        if (circles.Count == 0) return;

        using var paint = new SKPaint
        {
            Color = new SKColor(intensity, intensity, intensity),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        foreach (var c in circles)
        {
            float px = c.CenterX * scaleX + offsetX;
            float py = c.CenterY * scaleY + offsetY;
            float pr = c.Radius * Math.Max(scaleX, scaleY); // use larger scale for radius

            if (pr < 0.5f) continue; // sub-pixel, skip

            canvas.DrawCircle(px, py, pr, paint);
        }
    }

    /// <summary>
    /// Render support circles as polygon contours (for 2D polygon union approach).
    /// Each circle is approximated as an N-sided polygon.
    /// </summary>
    public static List<List<Vector2>> CirclesToPolygons(
        List<AnalyticalSupportSlicer.SupportCircle> circles, int sides = 16)
    {
        var polygons = new List<List<Vector2>>();

        foreach (var c in circles)
        {
            if (c.Radius < 0.01f) continue;

            var poly = new List<Vector2>(sides);
            for (int i = 0; i < sides; i++)
            {
                float angle = 2f * MathF.PI * i / sides;
                poly.Add(new Vector2(
                    c.CenterX + MathF.Cos(angle) * c.Radius,
                    c.CenterY + MathF.Sin(angle) * c.Radius));
            }
            polygons.Add(poly);
        }

        return polygons;
    }

    /// <summary>
    /// Create a standalone support-only layer image (for preview/debugging).
    /// </summary>
    public static byte[] RenderSupportOnlyLayer(
        List<AnalyticalSupportSlicer.SupportCircle> circles,
        int resX, int resY,
        float buildWidthMm, float buildDepthMm,
        float meshCenterX = 0, float meshCenterY = 0)
    {
        float scaleX = resX / buildWidthMm;
        float scaleY = resY / buildDepthMm;
        float offsetX = resX / 2f - meshCenterX * scaleX;
        float offsetY = resY / 2f - meshCenterY * scaleY;

        var info = new SKImageInfo(resX, resY, SKColorType.Gray8);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        RenderSupportsOnLayer(canvas, circles, scaleX, scaleY, offsetX, offsetY);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Render polygon cross-sections (non-circular shapes: cube/cross/pyramid) onto a layer.
    /// </summary>
    public static void RenderPolygonsOnLayer(
        SKCanvas canvas,
        List<AnalyticalSupportSlicer.SupportPolygon> polygons,
        float scaleX, float scaleY,
        float offsetX, float offsetY,
        byte intensity = 255)
    {
        if (polygons.Count == 0) return;

        using var paint = new SKPaint
        {
            Color = new SKColor(intensity, intensity, intensity),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        foreach (var poly in polygons)
        {
            if (poly.Vertices.Length < 3) continue;
            using var path = new SKPath();
            var v0 = poly.Vertices[0];
            path.MoveTo(v0.X * scaleX + offsetX, v0.Y * scaleY + offsetY);
            for (int i = 1; i < poly.Vertices.Length; i++)
            {
                var v = poly.Vertices[i];
                path.LineTo(v.X * scaleX + offsetX, v.Y * scaleY + offsetY);
            }
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>
    /// Compute how many layer images will contain support geometry.
    /// Useful for print time estimation.
    /// </summary>
    public static (int supportLayers, float totalSupportAreaMm2) ComputeSupportStats(
        List<AnalyticalSupportSlicer.SupportElement> elements, float layerHeight, float minZ, float maxZ)
    {
        int supportLayers = 0;
        float totalArea = 0;

        for (float z = minZ + layerHeight * 0.5f; z <= maxZ; z += layerHeight)
        {
            var circles = AnalyticalSupportSlicer.SliceAtZ(elements, z);
            if (circles.Count > 0)
            {
                supportLayers++;
                foreach (var c in circles)
                    totalArea += MathF.PI * c.Radius * c.Radius;
            }
        }

        return (supportLayers, totalArea);
    }
}
