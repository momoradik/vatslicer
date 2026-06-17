namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Validates printer profile settings for common misconfigurations.
/// Catches: zero resolution, impossible lift speeds, missing exposure times,
/// and physically impossible build volumes.
/// </summary>
public static class PrinterProfileValidator
{
    public sealed record ValidationIssue
    {
        public required string Field { get; init; }
        public required string Severity { get; init; } // "error", "warning"
        public required string Message { get; init; }
    }

    public sealed record ValidationResult
    {
        public required bool Valid { get; init; }
        public required List<ValidationIssue> Issues { get; init; }
    }

    public static ValidationResult Validate(
        int resolutionX, int resolutionY,
        float bedWidthMm, float bedDepthMm, float bedHeightMm,
        float normalExposureMs, float bottomExposureMs,
        float liftSpeedMmPerMin, float retractSpeedMmPerMin,
        float liftDistanceMm, int bottomLayerCount)
    {
        var issues = new List<ValidationIssue>();

        if (resolutionX <= 0) issues.Add(new() { Field = "ResolutionX", Severity = "error", Message = "Resolution X must be positive" });
        if (resolutionY <= 0) issues.Add(new() { Field = "ResolutionY", Severity = "error", Message = "Resolution Y must be positive" });
        if (bedWidthMm <= 0) issues.Add(new() { Field = "BedWidth", Severity = "error", Message = "Bed width must be positive" });
        if (bedDepthMm <= 0) issues.Add(new() { Field = "BedDepth", Severity = "error", Message = "Bed depth must be positive" });
        if (bedHeightMm <= 0) issues.Add(new() { Field = "BedHeight", Severity = "error", Message = "Bed height must be positive" });

        if (normalExposureMs <= 0) issues.Add(new() { Field = "NormalExposure", Severity = "error", Message = "Normal exposure must be positive" });
        if (normalExposureMs > 30000) issues.Add(new() { Field = "NormalExposure", Severity = "warning", Message = $"Normal exposure {normalExposureMs}ms seems very high (typical: 1-4s)" });
        if (bottomExposureMs < normalExposureMs) issues.Add(new() { Field = "BottomExposure", Severity = "warning", Message = "Bottom exposure should be higher than normal exposure" });

        if (liftSpeedMmPerMin <= 0) issues.Add(new() { Field = "LiftSpeed", Severity = "error", Message = "Lift speed must be positive" });
        if (liftSpeedMmPerMin > 600) issues.Add(new() { Field = "LiftSpeed", Severity = "warning", Message = $"Lift speed {liftSpeedMmPerMin}mm/min very fast — may cause layer separation" });

        if (liftDistanceMm < 1) issues.Add(new() { Field = "LiftDistance", Severity = "warning", Message = "Lift distance < 1mm may not fully separate layer from FEP" });
        if (liftDistanceMm > 15) issues.Add(new() { Field = "LiftDistance", Severity = "warning", Message = $"Lift distance {liftDistanceMm}mm unusually high — wastes time" });

        if (bottomLayerCount < 1) issues.Add(new() { Field = "BottomLayers", Severity = "error", Message = "Need at least 1 bottom layer for bed adhesion" });
        if (bottomLayerCount > 20) issues.Add(new() { Field = "BottomLayers", Severity = "warning", Message = $"{bottomLayerCount} bottom layers is very high (typical: 3-8)" });

        float pixelPitch = bedWidthMm / Math.Max(resolutionX, 1);
        if (pixelPitch > 0.1f) issues.Add(new() { Field = "PixelPitch", Severity = "warning", Message = $"Pixel pitch {pixelPitch:F3}mm is coarse — may limit detail" });

        return new ValidationResult { Valid = issues.All(i => i.Severity != "error"), Issues = issues };
    }
}
