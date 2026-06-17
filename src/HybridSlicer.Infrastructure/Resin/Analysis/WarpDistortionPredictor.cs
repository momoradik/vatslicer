using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Predicts warping/distortion risk based on model geometry.
/// Large flat surfaces and thin tall features are prone to warping
/// due to internal stress from UV curing shrinkage.
/// </summary>
public static class WarpDistortionPredictor
{
    public sealed record WarpRisk
    {
        public required float RiskScore { get; init; } // 0-10
        public required string Level { get; init; } // "low", "medium", "high"
        public required float MaxFlatAreaMm2 { get; init; }
        public required float AspectRatio { get; init; }
        public required List<string> RiskFactors { get; init; }
        public required List<string> Mitigations { get; init; }
    }

    public static WarpRisk Predict(StlMesh mesh, float shrinkagePct = 2f)
    {
        float w = mesh.Max.X - mesh.Min.X;
        float d = mesh.Max.Y - mesh.Min.Y;
        float h = mesh.Max.Z - mesh.Min.Z;

        float maxFlat = w * d;
        float aspectRatio = h > 0 ? Math.Max(w, d) / h : 1;
        float thinness = Math.Min(w, d) > 0 ? h / Math.Min(w, d) : 1;

        var factors = new List<string>();
        var mitigations = new List<string>();
        float score = 0;

        // Large flat area → high internal stress
        if (maxFlat > 5000)
        {
            score += 3;
            factors.Add($"Large flat area ({maxFlat:F0}mm2) — high internal stress during cure");
            mitigations.Add("Orient model at 10-30 degrees to reduce flat cross-section");
        }
        else if (maxFlat > 2000)
        {
            score += 1.5f;
            factors.Add($"Moderate flat area ({maxFlat:F0}mm2)");
        }

        // Tall thin features → buckling risk
        if (thinness > 5)
        {
            score += 3;
            factors.Add($"Very tall and thin (aspect ratio {thinness:F1}:1) — buckling risk");
            mitigations.Add("Add supports along thin walls or increase wall thickness");
        }
        else if (thinness > 3)
        {
            score += 1.5f;
            factors.Add($"Tall feature (aspect ratio {thinness:F1}:1)");
        }

        // High shrinkage resin
        if (shrinkagePct > 3)
        {
            score += 2;
            factors.Add($"High shrinkage resin ({shrinkagePct}%)");
            mitigations.Add("Use low-shrinkage resin or apply shrinkage compensation");
        }

        // Wide flat model
        if (aspectRatio > 4)
        {
            score += 1.5f;
            factors.Add($"Wide flat model (width:height = {aspectRatio:F1}:1)");
            mitigations.Add("Tilt model 15-45 degrees for more gradual cross-section growth");
        }

        score = Math.Clamp(score, 0, 10);
        string level = score < 3 ? "low" : score < 6 ? "medium" : "high";

        if (factors.Count == 0) factors.Add("No significant warp risk factors detected");
        if (mitigations.Count == 0) mitigations.Add("No mitigation needed");

        return new WarpRisk
        {
            RiskScore = score,
            Level = level,
            MaxFlatAreaMm2 = maxFlat,
            AspectRatio = aspectRatio,
            RiskFactors = factors,
            Mitigations = mitigations,
        };
    }
}
