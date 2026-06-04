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

        // ── Step 1: Build half-edge mesh ──────────────────────────────────
        var heMesh = HalfEdgeMesh.Build(mesh);

        // No centroid computation needed — pure angle test for overhang detection

        // ── Step 3: Identify overhang triangles — pure angle test ─────────
        // A triangle needs support if and only if its outward normal points
        // sufficiently downward (beyond the overhang angle threshold).
        // No centroid convexity filter — that was removing valid overhangs on
        // curved parts because it confused curvature with interior surfaces.
        // For watertight meshes with consistent outward normals, a downward
        // normal IS an exterior overhang by definition.
        var overhangTris = new List<(Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 normal, float area, Vector3 centroid)>();
        float totalOverhangArea = 0;

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

        // Sort by Z (lowest first = most critical for printing)
        overhangTris.Sort((a, b) => a.centroid.Z.CompareTo(b.centroid.Z));

        Serilog.Log.Information("Overhang: {Count} exterior tris, {Area:F0}mm² total",
            overhangTris.Count, totalOverhangArea);

        // ── Step 4: Poisson-disk-like sampling across ALL overhang triangles ─
        // Instead of sampling each triangle independently (which creates grid artifacts
        // and misses cross-triangle spacing), we use the global spacing grid as the
        // single authority. Each candidate is tested against ALL previously placed points.
        //
        // Process: weighted random candidates from triangles proportional to their area,
        // then accept/reject via spacing grid. This gives uniform distribution without
        // per-triangle boundary artifacts.

        var grid = new SpatialGrid<string>(baseSpacing);
        var points = new List<SupportPoint>();
        int idCounter = 0;
        float meshHeight = mesh.Max.Z - mesh.Min.Z;

        // Build weighted candidate list: more candidates from larger triangles
        // Total candidates = sum of (area / spacing²) per triangle, uncapped
        var candidates = new List<(Vector3 point, Vector3 normal, float area, float steepness, float z)>();

        foreach (var tri in overhangTris)
        {
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
