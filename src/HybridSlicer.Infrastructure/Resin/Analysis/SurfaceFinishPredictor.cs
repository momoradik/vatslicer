using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Predicts surface finish quality based on face angle relative to the build direction.
/// Near-vertical faces show visible layer lines. Near-horizontal faces are smooth.
/// Faces at ~45 degrees show stairstepping. Reports per-face quality classification.
/// </summary>
public static class SurfaceFinishPredictor
{
    public sealed record FinishReport
    {
        public required float SmoothPct { get; init; }
        public required float StairstepPct { get; init; }
        public required float LayerLinePct { get; init; }
        public required float AvgQualityScore { get; init; } // 0-1, higher = smoother
    }

    public static FinishReport Predict(StlMesh mesh, float layerHeightMm = 0.05f)
    {
        int smooth = 0, stairstep = 0, layerLine = 0;
        float totalQuality = 0;

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            float len = cross.Length();
            if (len < 1e-6f) continue;
            var normal = cross / len;

            float absZ = Math.Abs(normal.Z);
            // Near-horizontal (abs Z > 0.9): smooth
            // Near-vertical (abs Z < 0.3): visible layer lines
            // Mid-angle (0.3-0.9): stairstepping
            float quality;
            if (absZ > 0.9f) { smooth++; quality = 1f; }
            else if (absZ < 0.3f) { layerLine++; quality = 0.3f; }
            else { stairstep++; quality = 0.5f + (absZ - 0.3f); }

            totalQuality += quality;
        }

        int total = smooth + stairstep + layerLine;
        if (total == 0) return new FinishReport { SmoothPct = 0, StairstepPct = 0, LayerLinePct = 0, AvgQualityScore = 0 };

        return new FinishReport
        {
            SmoothPct = 100f * smooth / total,
            StairstepPct = 100f * stairstep / total,
            LayerLinePct = 100f * layerLine / total,
            AvgQualityScore = totalQuality / total,
        };
    }
}
