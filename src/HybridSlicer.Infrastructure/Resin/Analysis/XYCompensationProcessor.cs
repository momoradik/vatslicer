namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Applies XY dimensional compensation to layer images by eroding or dilating
/// the white pixels. Used to correct for resin bleed (overcure) or undercure.
///
/// Positive offset = dilate (make features larger, compensate for undercure)
/// Negative offset = erode (make features smaller, compensate for overcure/bleed)
/// </summary>
public static class XYCompensationProcessor
{
    /// <summary>
    /// Apply XY compensation to a grayscale layer image.
    /// </summary>
    /// <param name="pixels">Grayscale pixel data (row-major, 0=black, 255=white).</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="offsetPixels">Offset in pixels. Negative=erode, Positive=dilate.</param>
    public static byte[] Apply(byte[] pixels, int width, int height, int offsetPixels)
    {
        if (offsetPixels == 0 || pixels.Length != width * height) return pixels;

        var result = new byte[pixels.Length];
        int absOffset = Math.Abs(offsetPixels);
        bool dilate = offsetPixels > 0;

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            byte current = pixels[y * width + x];
            bool isWhite = current >= 128;

            if (dilate)
            {
                // Dilate: if any neighbor within radius is white, output white
                bool found = false;
                for (int dy = -absOffset; dy <= absOffset && !found; dy++)
                for (int dx = -absOffset; dx <= absOffset && !found; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                    {
                        if (pixels[ny * width + nx] >= 128) found = true;
                    }
                }
                result[y * width + x] = found ? (byte)255 : (byte)0;
            }
            else
            {
                // Erode: if any neighbor within radius is black, output black
                bool allWhite = isWhite;
                if (allWhite)
                {
                    for (int dy = -absOffset; dy <= absOffset && allWhite; dy++)
                    for (int dx = -absOffset; dx <= absOffset && allWhite; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                        {
                            if (pixels[ny * width + nx] < 128) allWhite = false;
                        }
                        else allWhite = false; // edge = black
                    }
                }
                result[y * width + x] = allWhite ? (byte)255 : (byte)0;
            }
        }

        return result;
    }
}
