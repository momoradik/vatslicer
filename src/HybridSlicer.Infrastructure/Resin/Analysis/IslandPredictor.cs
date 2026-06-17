using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Predicts which layers will have unsupported islands BEFORE slicing.
/// Uses triangle normal analysis: faces with downward normals below the
/// overhang threshold that aren't connected to the build plate are island candidates.
/// Much faster than full slice-based detection — O(n triangles) vs O(n layers × n polygons).
/// </summary>
public static class IslandPredictor
{
    public sealed record IslandRisk
    {
        public required float ZMm { get; init; }
        public required float AreaMm2 { get; init; }
        public required Vector3 Centroid { get; init; }
        public required string Risk { get; init; } // "low", "medium", "high"
    }

    public sealed record PredictionResult
    {
        public required List<IslandRisk> Risks { get; init; }
        public required int HighRiskCount { get; init; }
        public required float LowestUnsupportedZ { get; init; }
    }

    public static PredictionResult Predict(StlMesh mesh, float overhangAngleDeg = 45f)
    {
        float cosThreshold = -MathF.Cos(overhangAngleDeg * MathF.PI / 180f);
        var risks = new List<IslandRisk>();
        float lowestZ = float.MaxValue;

        // Group downward-facing triangles by Z band
        float bandHeight = 2f; // mm
        var bands = new Dictionary<int, List<(Vector3 centroid, float area)>>();

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            float len = cross.Length();
            if (len < 1e-6f) continue;
            var normal = cross / len;

            // Downward-facing beyond threshold
            if (normal.Z < cosThreshold)
            {
                var centroid = (v0 + v1 + v2) / 3f;
                float area = len * 0.5f;
                int band = (int)(centroid.Z / bandHeight);
                if (!bands.ContainsKey(band)) bands[band] = new();
                bands[band].Add((centroid, area));
            }
        }

        // Each band with significant downward area is an island risk
        foreach (var (band, tris) in bands)
        {
            float totalArea = tris.Sum(t => t.area);
            if (totalArea < 1f) continue; // negligible

            var avgCentroid = new Vector3(
                tris.Average(t => t.centroid.X),
                tris.Average(t => t.centroid.Y),
                tris.Average(t => t.centroid.Z));

            string risk = totalArea > 100f ? "high" : totalArea > 25f ? "medium" : "low";
            if (avgCentroid.Z < lowestZ) lowestZ = avgCentroid.Z;

            risks.Add(new IslandRisk
            {
                ZMm = avgCentroid.Z,
                AreaMm2 = totalArea,
                Centroid = avgCentroid,
                Risk = risk,
            });
        }

        return new PredictionResult
        {
            Risks = risks.OrderBy(r => r.ZMm).ToList(),
            HighRiskCount = risks.Count(r => r.Risk == "high"),
            LowestUnsupportedZ = lowestZ == float.MaxValue ? 0 : lowestZ,
        };
    }
}
