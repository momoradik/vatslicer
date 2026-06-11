using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates support points for resin DLP/LCD printing.
///
/// Pipeline:
/// 1. Shell thickening: offset single-wall surfaces inward to create a virtual
///    solid. This gives the half-edge mesh a proper inside/outside boundary.
/// 2. Half-edge mesh: vertex welding + edge adjacency + consistent winding +
///    ray-vote global orientation. After this, outward normals are topologically correct.
/// 3. Centroid convexity: for each overhang triangle, check if its outward normal
///    points toward or away from the model centroid. Concave (interior) → skip.
/// 4. Poisson-disk-like sampling: collect ALL overhang triangles into a single surface,
///    then sample with global minimum-distance enforcement. No per-triangle grid artifacts.
/// 5. Force estimation + priority ordering for structural weight classification.
/// </summary>
public sealed class SupportPointGenerator
{
    public sealed class SupportPoint
    {
        public required string Id { get; init; }
        public required Vector3 Position { get; init; }
        public required Vector3 Normal { get; init; }
        public required float OverhangArea { get; init; }
        public required OverhangAnalyzer.OverhangType OverhangType { get; init; }
        public required float Priority { get; init; }
        public required ForceEstimator.SupportWeight RecommendedWeight { get; init; }
        public required float SafetyFactor { get; init; }
        // Per-support diameter overrides (from manual placement). null = use engine defaults.
        public float? ManualTipRadiusMm { get; init; }
        public float? ManualPillarRadiusMm { get; init; }
        public float? ManualBaseRadiusMm { get; init; }
    }

    public sealed class GenerationConfig
    {
        public float MinSpacingMm { get; init; } = 2.0f;
        public float MaxSpacingMm { get; init; } = 8.0f;
        public float DensityFactor { get; init; } = 0.5f;
        public float OverhangAngleDeg { get; init; } = 45f;
        public PrinterOrientation Orientation { get; init; } = PrinterOrientation.BottomUp;
        public float RecoaterSpeedMmS { get; init; } = 0;
        public float LayerHeightMm { get; init; } = 1.0f;
        public List<(Vector3 position, float radiusMm)>? DrainHoleExclusions { get; init; }
        public float DrainHoleClearanceMm { get; init; } = 2.0f;
    }

    public sealed class GenerationResult
    {
        public required List<SupportPoint> Points { get; init; }
        public required List<OverhangAnalyzer.OverhangRegion> OverhangRegions { get; init; }
        public required int OverhangRegionsAnalyzed { get; init; }
        public required int IslandsDetected { get; init; }
        public required float TotalOverhangArea { get; init; }
        public required long ElapsedMs { get; init; }
    }

    public static GenerationResult Generate(StlMesh mesh, GenerationConfig config, AabbBvh? prebuiltBvh = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        float baseSpacing = config.MinSpacingMm
            + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);
        float normalZThreshold = -MathF.Cos(config.OverhangAngleDeg * MathF.PI / 180f);

        // ── Step 1: Identify overhang triangles ────────────────────────────
        // For large meshes (>50K tris), skip the expensive HalfEdgeMesh build and
        // use STL file normals directly. For small meshes, still use HalfEdgeMesh
        // for topologically correct normals.
        var overhangTris = new List<(Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 normal, float area, Vector3 centroid)>();
        float totalOverhangArea = 0;

        if (mesh.TriangleCount > 50_000)
        {
            // Fast path: use STL file normals directly (O(n), no half-edge build)
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var normal = mesh.FileNormals[t];
                // Normalize if needed (STL normals can be zero or non-unit)
                float len = normal.Length();
                if (len < 0.001f)
                {
                    // Compute from vertices
                    var va = mesh.Vertices[t * 3];
                    var vb = mesh.Vertices[t * 3 + 1];
                    var vc = mesh.Vertices[t * 3 + 2];
                    normal = Vector3.Cross(vb - va, vc - va);
                    len = normal.Length();
                    if (len < 0.001f) continue;
                }
                normal /= len;

                if (normal.Z >= normalZThreshold) continue;

                var v0 = mesh.Vertices[t * 3];
                var v1 = mesh.Vertices[t * 3 + 1];
                var v2 = mesh.Vertices[t * 3 + 2];
                float area = Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
                if (area < 0.01f) continue;

                var triCenter = (v0 + v1 + v2) / 3f;
                totalOverhangArea += area;
                overhangTris.Add((v0, v1, v2, normal, area, triCenter));
            }
        }
        else
        {
            // Standard path: use HalfEdgeMesh for topologically correct normals
            var heMesh = HalfEdgeMesh.Build(mesh);
            for (int t = 0; t < heMesh.TriangleCount; t++)
            {
                var normal = heMesh.GetOutwardNormal(t);
                if (normal.Z >= normalZThreshold) continue;

                var (v0, v1, v2) = heMesh.GetTriangleVertices(t);
                float area = Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
                if (area < 0.01f) continue;

                var triCenter = (v0 + v1 + v2) / 3f;
                totalOverhangArea += area;
                overhangTris.Add((v0, v1, v2, normal, area, triCenter));
            }
        }

        // Diagnostic: spatial distribution of detected overhang triangles
        {
            float minZ = overhangTris.Count > 0 ? overhangTris.Min(t => t.centroid.Z) : 0;
            float maxZ = overhangTris.Count > 0 ? overhangTris.Max(t => t.centroid.Z) : 0;
            float midZ = (minZ + maxZ) / 2;
            int lowerHalf = overhangTris.Count(t => t.centroid.Z < midZ);
            int upperHalf = overhangTris.Count - lowerHalf;
            Serilog.Log.Information("Overhang DETECTION: {Count} tris, {Area:F0}mm², Z=[{MinZ:F1},{MaxZ:F1}], lower={Lower} upper={Upper}",
                overhangTris.Count, totalOverhangArea, minZ, maxZ, lowerHalf, upperHalf);
        }

        // DO NOT sort by Z — Z-sort + candidate cap = only bottom half gets sampled.
        // Instead, shuffle for spatial uniformity so the candidate cap doesn't bias by Z.
        // Use a deterministic shuffle (Fisher-Yates with fixed seed) for reproducibility.
        var rng = new Random(42);
        for (int i = overhangTris.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (overhangTris[i], overhangTris[j]) = (overhangTris[j], overhangTris[i]);
        }

        Serilog.Log.Information("Overhang: {Count} exterior tris, {Area:F0}mm² total",
            overhangTris.Count, totalOverhangArea);

        // ── Step 4: Poisson-disk-like sampling across ALL overhang triangles ─
        var grid = new SpatialGrid<string>(baseSpacing);
        var points = new List<SupportPoint>();
        int idCounter = 0;
        float meshHeight = mesh.Max.Z - mesh.Min.Z;

        // Build weighted candidate list: one centroid per triangle, up to cap.
        // Cap is scaled by mesh triangle count to handle large meshes without explosion.
        int maxCandidates = Math.Max(overhangTris.Count, 100_000);
        var candidates = new List<(Vector3 point, Vector3 normal, float area, float steepness, float z)>();

        foreach (var tri in overhangTris)
        {
            if (candidates.Count >= maxCandidates) break;
            float steepness = MathF.Abs(tri.normal.Z);
            float spacing = baseSpacing * (1.5f - steepness * 0.5f);
            spacing = Math.Clamp(spacing, config.MinSpacingMm, config.MaxSpacingMm);

            // Number of candidates proportional to area — NO cap
            int nSamples = Math.Max(1, (int)(tri.area / (spacing * spacing)));

            // Always add centroid as first candidate
            candidates.Add((tri.centroid, tri.normal, tri.area, steepness, tri.centroid.Z));

            // Add additional samples using jittered barycentric grid
            for (int s = 1; s < nSamples; s++)
            {
                int gridSize = (int)MathF.Ceiling(MathF.Sqrt(nSamples));
                int si = s / gridSize, sj = s % gridSize;
                // Jitter: offset by half-cell with slight randomization via index
                float jx = ((s * 7 + 3) % 13) / 13f * 0.3f; // deterministic pseudo-random
                float jy = ((s * 11 + 5) % 13) / 13f * 0.3f;
                float u = (si + 0.35f + jx) / gridSize;
                float v = (sj + 0.35f + jy) / gridSize;
                if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                u = Math.Clamp(u, 0.01f, 0.98f);
                v = Math.Clamp(v, 0.01f, Math.Min(0.98f, 1f - u - 0.01f));
                var point = tri.v0 * (1f - u - v) + tri.v1 * u + tri.v2 * v;
                candidates.Add((point, tri.normal, tri.area, steepness, point.Z));
            }
        }

        // Accept/reject candidates via global spacing grid (Poisson-disk style)
        foreach (var (point, normal, triArea, steepness, z) in candidates)
        {
            float spacing = baseSpacing * (1.5f - steepness * 0.5f);
            spacing = Math.Clamp(spacing, config.MinSpacingMm, config.MaxSpacingMm);

            if (grid.ExistsInRadius(point, spacing))
                continue;

            // Drain hole exclusion
            if (config.DrainHoleExclusions is { Count: > 0 })
            {
                bool tooClose = false;
                foreach (var (holePos, holeR) in config.DrainHoleExclusions)
                {
                    if (Vector3.Distance(point, holePos) < holeR + config.DrainHoleClearanceMm)
                    { tooClose = true; break; }
                }
                if (tooClose) continue;
            }

            // Force estimation
            float overhangArea = triArea;
            int supportsInRegion = Math.Max(1, (int)(overhangArea / (spacing * spacing)));
            var force = ForceEstimator.Estimate(
                point.Z, overhangArea, supportsInRegion,
                overhangArea, spacing,
                config.Orientation, config.RecoaterSpeedMmS);

            float priority = 1f - Math.Clamp(z / (meshHeight + 1f), 0, 1);
            priority += steepness * 0.3f;

            string id = $"sp-{++idCounter}";
            grid.Insert(point, id);
            points.Add(new SupportPoint
            {
                Id = id,
                Position = point,
                Normal = normal,
                OverhangArea = overhangArea,
                OverhangType = z < 2f
                    ? OverhangAnalyzer.OverhangType.NewIsland
                    : steepness > 0.9f
                        ? OverhangAnalyzer.OverhangType.BulkOverhang
                        : OverhangAnalyzer.OverhangType.Peninsula,
                Priority = priority,
                RecommendedWeight = force.Weight,
                SafetyFactor = force.SafetyFactor,
            });
        }

        sw.Stop();
        return new GenerationResult
        {
            Points = points,
            OverhangRegions = new List<OverhangAnalyzer.OverhangRegion>(),
            OverhangRegionsAnalyzed = overhangTris.Count,
            IslandsDetected = overhangTris.Count(t => t.centroid.Z < 2f),
            TotalOverhangArea = totalOverhangArea,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
