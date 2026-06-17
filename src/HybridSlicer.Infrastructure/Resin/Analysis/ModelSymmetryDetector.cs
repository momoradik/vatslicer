using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Detects approximate symmetry planes in a mesh.
/// Useful for: auto-orientation (print along symmetry axis for even peel),
/// support placement (mirror supports across symmetry plane).
/// Uses vertex distribution comparison across candidate planes.
/// </summary>
public static class ModelSymmetryDetector
{
    public sealed record SymmetryResult
    {
        public required bool HasXYSymmetry { get; init; }
        public required bool HasXZSymmetry { get; init; }
        public required bool HasYZSymmetry { get; init; }
        public required float XYSymmetryScore { get; init; }
        public required float XZSymmetryScore { get; init; }
        public required float YZSymmetryScore { get; init; }
        public required string BestSymmetryPlane { get; init; }
    }

    public static SymmetryResult Detect(StlMesh mesh, float toleranceMm = 0.5f)
    {
        var center = (mesh.Min + mesh.Max) * 0.5f;

        float xyScore = CheckPlaneSymmetry(mesh, center, 2, toleranceMm); // Z=center plane
        float xzScore = CheckPlaneSymmetry(mesh, center, 1, toleranceMm); // Y=center plane
        float yzScore = CheckPlaneSymmetry(mesh, center, 0, toleranceMm); // X=center plane

        float threshold = 0.7f;
        string best = "none";
        float bestScore = 0;
        if (xyScore > bestScore) { bestScore = xyScore; best = "XY (horizontal)"; }
        if (xzScore > bestScore) { bestScore = xzScore; best = "XZ (front-back)"; }
        if (yzScore > bestScore) { bestScore = yzScore; best = "YZ (left-right)"; }

        return new SymmetryResult
        {
            HasXYSymmetry = xyScore >= threshold,
            HasXZSymmetry = xzScore >= threshold,
            HasYZSymmetry = yzScore >= threshold,
            XYSymmetryScore = xyScore,
            XZSymmetryScore = xzScore,
            YZSymmetryScore = yzScore,
            BestSymmetryPlane = bestScore >= threshold ? best : "none",
        };
    }

    private static float CheckPlaneSymmetry(StlMesh mesh, Vector3 center, int axis, float tolerance)
    {
        int matches = 0, total = 0;
        int sampleStep = Math.Max(1, mesh.Vertices.Length / 500);

        for (int i = 0; i < mesh.Vertices.Length; i += sampleStep)
        {
            var v = mesh.Vertices[i];
            var reflected = v;
            switch (axis)
            {
                case 0: reflected.X = 2 * center.X - v.X; break;
                case 1: reflected.Y = 2 * center.Y - v.Y; break;
                case 2: reflected.Z = 2 * center.Z - v.Z; break;
            }

            // Check if reflected point is near any mesh vertex
            float minDist = float.MaxValue;
            for (int j = 0; j < mesh.Vertices.Length; j += sampleStep)
            {
                float d = Vector3.Distance(reflected, mesh.Vertices[j]);
                if (d < minDist) minDist = d;
            }

            total++;
            if (minDist <= tolerance) matches++;
        }

        return total > 0 ? (float)matches / total : 0;
    }
}
