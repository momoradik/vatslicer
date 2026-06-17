using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Detects suction-cup geometry (inverted concave pockets) that cause print failures.
/// A suction cup forms when a concave surface faces the FEP film — during peel,
/// vacuum pressure builds inside the pocket and can rip the part off supports.
///
/// Detection: at each Z layer, compare contour area with the layer above.
/// If area increases sharply (pocket opening downward), flag as suction risk.
/// Severity scales with pocket depth and area ratio.
/// </summary>
public static class SuctionCupDetector
{
    public sealed record SuctionWarning
    {
        public required Vector3 Position { get; init; }
        public required float DepthMm { get; init; }
        public required float AreaRatio { get; init; }
        public required string Severity { get; init; } // "low", "medium", "high"
        public required string Description { get; init; }
    }

    public sealed record DetectionConfig
    {
        public float LayerHeightMm { get; init; } = 1f;
        public float MinAreaRatio { get; init; } = 1.5f; // area must grow by 50%+ to flag
        public float MinPocketDepthMm { get; init; } = 2f;
    }

    public static List<SuctionWarning> Detect(StlMesh mesh, DetectionConfig? config = null)
    {
        config ??= new DetectionConfig();
        var warnings = new List<SuctionWarning>();

        float meshMinZ = mesh.Min.Z;
        float meshMaxZ = mesh.Max.Z;
        float totalHeight = meshMaxZ - meshMinZ;
        if (totalHeight < config.LayerHeightMm * 2) return warnings;

        int layerCount = (int)Math.Ceiling(totalHeight / config.LayerHeightMm);

        // Compute approximate cross-section area per layer using triangle projection
        var layerAreas = new float[layerCount];
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            float triMinZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
            float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));

            // XY projected area of triangle
            float ax = v1.X - v0.X, ay = v1.Y - v0.Y;
            float bx = v2.X - v0.X, by = v2.Y - v0.Y;
            float projArea = Math.Abs(ax * by - ay * bx) * 0.5f;

            // Add to all layers this triangle spans
            int startLayer = Math.Max(0, (int)((triMinZ - meshMinZ) / config.LayerHeightMm));
            int endLayer = Math.Min(layerCount - 1, (int)((triMaxZ - meshMinZ) / config.LayerHeightMm));
            for (int li = startLayer; li <= endLayer; li++)
                layerAreas[li] += projArea;
        }

        // Scan bottom-up for sudden area increases (suction cup signature)
        float pocketStartZ = 0;
        float pocketStartArea = 0;
        bool inPocket = false;

        for (int i = 1; i < layerCount; i++)
        {
            float prevArea = layerAreas[i - 1];
            float currArea = layerAreas[i];
            if (prevArea < 1f) continue;

            float ratio = currArea / prevArea;

            if (ratio >= config.MinAreaRatio && !inPocket)
            {
                inPocket = true;
                pocketStartZ = meshMinZ + i * config.LayerHeightMm;
                pocketStartArea = prevArea;
            }
            else if (inPocket && ratio < 1.1f)
            {
                float depth = meshMinZ + i * config.LayerHeightMm - pocketStartZ;
                if (depth >= config.MinPocketDepthMm)
                {
                    float maxRatio = currArea / pocketStartArea;
                    string severity = maxRatio > 3f ? "high" : maxRatio > 2f ? "medium" : "low";
                    float cx = (mesh.Min.X + mesh.Max.X) / 2;
                    float cy = (mesh.Min.Y + mesh.Max.Y) / 2;

                    warnings.Add(new SuctionWarning
                    {
                        Position = new Vector3(cx, cy, pocketStartZ),
                        DepthMm = depth,
                        AreaRatio = maxRatio,
                        Severity = severity,
                        Description = $"Suction pocket at Z={pocketStartZ:F1}mm, depth={depth:F1}mm, area ratio={maxRatio:F1}x ({severity})",
                    });
                }
                inPocket = false;
            }
        }

        return warnings;
    }
}
