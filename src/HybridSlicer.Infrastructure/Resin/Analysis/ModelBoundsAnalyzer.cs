using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Analyzes model bounds relative to the build plate.
/// Reports: fits on plate, orientation suggestions, scale recommendations.
/// </summary>
public static class ModelBoundsAnalyzer
{
    public sealed record BoundsReport
    {
        public required float ModelWidthMm { get; init; }
        public required float ModelDepthMm { get; init; }
        public required float ModelHeightMm { get; init; }
        public required float PlateWidthMm { get; init; }
        public required float PlateDepthMm { get; init; }
        public required float PlateHeightMm { get; init; }
        public required bool FitsOnPlate { get; init; }
        public required bool FitsInHeight { get; init; }
        public required float MaxScaleToFit { get; init; }
        public required string Suggestion { get; init; }
    }

    public static BoundsReport Analyze(
        StlMesh mesh,
        float plateWidthMm, float plateDepthMm, float plateHeightMm,
        float marginMm = 3f)
    {
        float w = mesh.Max.X - mesh.Min.X;
        float d = mesh.Max.Y - mesh.Min.Y;
        float h = mesh.Max.Z - mesh.Min.Z;

        float usableW = plateWidthMm - 2 * marginMm;
        float usableD = plateDepthMm - 2 * marginMm;

        bool fitsXY = w <= usableW && d <= usableD;
        bool fitsZ = h <= plateHeightMm;

        // Also check rotated 90 degrees
        bool fitsXYRotated = d <= usableW && w <= usableD;
        if (!fitsXY && fitsXYRotated) fitsXY = true;

        float scaleW = usableW / Math.Max(w, 0.1f);
        float scaleD = usableD / Math.Max(d, 0.1f);
        float scaleH = plateHeightMm / Math.Max(h, 0.1f);
        float maxScale = Math.Min(scaleW, Math.Min(scaleD, scaleH));

        string suggestion;
        if (fitsXY && fitsZ)
            suggestion = "Model fits on build plate";
        else if (!fitsZ)
            suggestion = $"Model too tall ({h:F1}mm > {plateHeightMm:F0}mm). Scale to {maxScale:F2}x or split.";
        else
            suggestion = $"Model too wide. Scale to {maxScale:F2}x or rotate 90 degrees.";

        return new BoundsReport
        {
            ModelWidthMm = w, ModelDepthMm = d, ModelHeightMm = h,
            PlateWidthMm = plateWidthMm, PlateDepthMm = plateDepthMm, PlateHeightMm = plateHeightMm,
            FitsOnPlate = fitsXY, FitsInHeight = fitsZ,
            MaxScaleToFit = Math.Min(maxScale, 1f),
            Suggestion = suggestion,
        };
    }
}
