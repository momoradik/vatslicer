using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Applies sub-pixel anti-aliased edge compensation to layer images.
/// Instead of binary erode/dilate (XYCompensationProcessor), this applies
/// a gradient falloff at polygon edges to control exposure precisely.
/// The edge pixels get reduced intensity proportional to the compensation distance.
/// </summary>
public static class PixelBleedCompensator
{
    /// <summary>
    /// Apply edge intensity compensation. Pixels near white→black boundaries
    /// get their intensity reduced by a falloff curve.
    /// </summary>
    public static byte[] ApplyEdgeFalloff(byte[] pixels, int width, int height, int falloffPixels = 2, float minIntensity = 0.3f)
    {
        if (falloffPixels <= 0 || pixels.Length != width * height) return pixels;
        var result = new byte[pixels.Length];
        Array.Copy(pixels, result, pixels.Length);

        // Find edge pixels (white pixels adjacent to black)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int idx = y * width + x;
            if (pixels[idx] < 128) continue; // skip black pixels

            // Check if this is an edge pixel
            bool isEdge = false;
            for (int dy = -1; dy <= 1 && !isEdge; dy++)
            for (int dx = -1; dx <= 1 && !isEdge; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) { isEdge = true; continue; }
                if (pixels[ny * width + nx] < 128) isEdge = true;
            }

            if (!isEdge) continue;

            // Apply falloff to this pixel and nearby interior pixels
            for (int fy = -falloffPixels; fy <= falloffPixels; fy++)
            for (int fx = -falloffPixels; fx <= falloffPixels; fx++)
            {
                int tx = x + fx, ty = y + fy;
                if (tx < 0 || tx >= width || ty < 0 || ty >= height) continue;
                int tidx = ty * width + tx;
                if (pixels[tidx] < 128) continue; // don't modify black pixels

                float dist = MathF.Sqrt(fx * fx + fy * fy);
                if (dist > falloffPixels) continue;

                float t = dist / falloffPixels; // 0 at edge, 1 at falloff boundary
                float intensity = minIntensity + (1f - minIntensity) * t;
                byte newVal = (byte)(pixels[tidx] * intensity);
                if (newVal < result[tidx]) result[tidx] = newVal;
            }
        }

        return result;
    }
}
