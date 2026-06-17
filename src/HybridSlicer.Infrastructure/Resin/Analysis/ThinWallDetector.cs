using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Detects thin walls in a mesh that may not print reliably.
/// Uses a ray-based approach: for each surface point, cast a ray inward
/// and measure the distance to the opposite wall. If less than the minimum
/// printable wall thickness, flag as a warning.
/// </summary>
public static class ThinWallDetector
{
    public sealed record ThinWallWarning
    {
        public required Vector3 Position { get; init; }
        public required float WallThicknessMm { get; init; }
        public required float MinRequiredMm { get; init; }
    }

    public sealed record DetectionResult
    {
        public required int ThinWallCount { get; init; }
        public required float MinWallThicknessMm { get; init; }
        public required List<ThinWallWarning> Warnings { get; init; }
    }

    /// <summary>
    /// Sample mesh surface and check wall thickness.
    /// </summary>
    /// <param name="mesh">Model mesh.</param>
    /// <param name="minThicknessMm">Minimum printable wall thickness (mm). Typical: 0.4-0.8mm.</param>
    /// <param name="sampleCount">Number of surface points to sample.</param>
    public static DetectionResult Detect(
        Spatial.AabbBvh bvh,
        StlMesh mesh,
        float minThicknessMm = 0.5f,
        int sampleCount = 200)
    {
        var warnings = new List<ThinWallWarning>();
        float minFound = float.MaxValue;
        var rng = new Random(42);

        for (int s = 0; s < sampleCount; s++)
        {
            // Pick a random triangle and point on its surface
            int triIdx = rng.Next(mesh.TriangleCount);
            var v0 = mesh.Vertices[triIdx * 3];
            var v1 = mesh.Vertices[triIdx * 3 + 1];
            var v2 = mesh.Vertices[triIdx * 3 + 2];

            // Random barycentric coordinates
            float u = (float)rng.NextDouble();
            float v = (float)rng.NextDouble();
            if (u + v > 1) { u = 1 - u; v = 1 - v; }
            var point = v0 + u * (v1 - v0) + v * (v2 - v0);

            // Face normal (inward direction)
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            float len = normal.Length();
            if (len < 1e-6f) continue;
            normal = -normal / len; // inward

            // Ray cast inward to find opposite wall
            float maxDist = 20f; // mm — don't search beyond this
            float hit = bvh.BeamCast(point + normal * 0.01f, normal, 0.01f, 4, maxDist);

            if (hit < maxDist - 0.1f)
            {
                float thickness = hit;
                if (thickness < minFound) minFound = thickness;

                if (thickness < minThicknessMm)
                {
                    warnings.Add(new ThinWallWarning
                    {
                        Position = point,
                        WallThicknessMm = thickness,
                        MinRequiredMm = minThicknessMm,
                    });
                }
            }
        }

        return new DetectionResult
        {
            ThinWallCount = warnings.Count,
            MinWallThicknessMm = minFound == float.MaxValue ? 0 : minFound,
            Warnings = warnings,
        };
    }
}
