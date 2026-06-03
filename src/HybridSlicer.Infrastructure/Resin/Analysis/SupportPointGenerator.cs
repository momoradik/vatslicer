using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates support points by directly iterating over mesh triangles.
///
/// This is the PrusaSlicer/ChiTuBox approach:
/// 1. For each mesh triangle, check if its outward normal faces downward
///    beyond the overhang angle threshold → it IS an overhang.
/// 2. Sample points on the triangle surface using barycentric coordinates.
///    The surface point, normal, and triangle association are exact — no
///    BVH projection, no 2D contour artifacts, no wall redirection.
/// 3. For hollow shells: cast a ray downward from each candidate. If it
///    hits the mesh before reaching the build plate AND the hit surface
///    faces upward, this candidate is on an interior ceiling → skip it.
/// 4. Spacing via 3D hash grid ensures uniform distribution.
/// 5. Force estimation per-point for auto weight classification.
///
/// This replaces the old contour-based approach which:
/// - Generated 2D contour polygons via layer slicing
/// - Projected candidates back to 3D via BVH (lossy, error-prone)
/// - Failed on hollow shells (interior ceilings indistinguishable from exterior)
/// - Required dozens of heuristic patches that never fully worked
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

    /// <summary>
    /// Generate support points by direct triangle iteration.
    /// </summary>
    public static GenerationResult Generate(StlMesh mesh, GenerationConfig config, AabbBvh? prebuiltBvh = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build BVH for interior detection ray casts
        AabbBvh? bvh = prebuiltBvh ?? (mesh.TriangleCount <= 100000 ? AabbBvh.Build(mesh) : null);

        // Compute spacing from density
        float baseSpacing = config.MinSpacingMm
            + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);

        // Overhang threshold: normal.Z must be below this value for the triangle to be an overhang.
        // At 45°: cos(45°) = 0.707, so threshold = -0.707
        // A triangle with normal (0,0,-1) has normal.Z = -1 < -0.707 → overhang ✓
        // A triangle with normal (0,0,0) (vertical face) has normal.Z = 0 > -0.707 → not overhang ✓
        float normalZThreshold = -MathF.Cos(config.OverhangAngleDeg * MathF.PI / 180f);

        var grid = new SpatialGrid<string>(baseSpacing);
        var points = new List<SupportPoint>();
        int idCounter = 0;
        float totalOverhangArea = 0;

        // ── Phase 1: Identify overhang triangles and collect them ──────────

        var overhangTris = new List<(int triIndex, Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 normal, float area, Vector3 centroid)>();

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            float crossLen = cross.Length();
            if (crossLen < 1e-8f) continue; // degenerate triangle

            var normal = cross / crossLen;
            float area = crossLen * 0.5f;

            // Check overhang: normal must point sufficiently downward
            if (normal.Z >= normalZThreshold) continue;

            var centroid = (v0 + v1 + v2) / 3f;
            totalOverhangArea += area;

            overhangTris.Add((t, v0, v1, v2, normal, area, centroid));
        }

        // ── Phase 2: Filter out interior surfaces (hollow shell detection) ─

        // For each overhang triangle, cast a ray downward from its centroid.
        // If the ray hits another mesh triangle whose normal points UPWARD
        // (it's a floor/bottom surface), then this overhang is an interior
        // ceiling above a floor → skip it.
        //
        // For exterior overhangs: ray goes down, hits nothing (open air to bed) → keep.
        // For interior ceilings: ray goes down, hits the interior floor → skip.
        //
        // This also handles the case where the ray hits a SIDE wall (normal ~horizontal)
        // — that's not a floor, so we keep the overhang.

        var exteriorTris = new List<(int triIndex, Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 normal, float area, Vector3 centroid)>();

        foreach (var tri in overhangTris)
        {
            bool isInterior = false;

            if (bvh != null && tri.centroid.Z > 1f)
            {
                // Cast ray downward from slightly below the triangle surface
                // (offset by 0.3mm to avoid self-intersection)
                var rayOrigin = tri.centroid + tri.normal * 0.3f;
                var hit = bvh.RayCast(rayOrigin, -Vector3.UnitZ, tri.centroid.Z + 1f);

                if (hit.HasValue)
                {
                    // Check the hit triangle's normal — if it faces upward, it's a floor
                    // below this ceiling → interior surface
                    var hitNormal = hit.Value.Normal;
                    if (hitNormal.Z > 0.3f)
                    {
                        // Floor below → this is an interior ceiling
                        isInterior = true;
                    }
                    // If hit normal is horizontal or downward, it's a wall or another
                    // overhang — the overhang is still valid (e.g., shelf above a wall)
                }
            }

            if (!isInterior)
                exteriorTris.Add(tri);
        }

        Serilog.Log.Information("Triangle overhang: {Total} overhang tris, {Exterior} exterior, {Interior} interior filtered",
            overhangTris.Count, exteriorTris.Count, overhangTris.Count - exteriorTris.Count);

        // ── Phase 3: Sort by priority (lowest Z first = most critical) ─────

        exteriorTris.Sort((a, b) => a.centroid.Z.CompareTo(b.centroid.Z));

        // ── Phase 4: Sample support points on each overhang triangle ───────

        foreach (var tri in exteriorTris)
        {
            // Adaptive spacing: steeper overhangs get denser supports
            // normal.Z = -1 (flat bottom) → spacing = minSpacing (densest)
            // normal.Z = -0.7 (45° overhang) → spacing = baseSpacing
            float steepness = MathF.Abs(tri.normal.Z); // 0 = vertical, 1 = horizontal
            float spacing = baseSpacing * (1.5f - steepness * 0.5f);
            spacing = Math.Clamp(spacing, config.MinSpacingMm, config.MaxSpacingMm);

            // Number of samples proportional to triangle area / spacing²
            int samples = Math.Max(1, (int)(tri.area / (spacing * spacing)));
            samples = Math.Min(samples, 20); // cap per triangle

            for (int s = 0; s < samples; s++)
            {
                // Generate point on triangle surface using barycentric coordinates
                Vector3 point;
                if (samples == 1)
                {
                    // Single point → centroid
                    point = tri.centroid;
                }
                else
                {
                    // Deterministic grid sampling on triangle
                    // Use sub-triangle decomposition for even distribution
                    float u, v;
                    int gridSize = (int)MathF.Ceiling(MathF.Sqrt(samples));
                    int si = s / gridSize, sj = s % gridSize;
                    u = (si + 0.5f) / gridSize;
                    v = (sj + 0.5f) / gridSize;
                    // Fold points outside triangle (u+v>1) back inside
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    point = tri.v0 * (1f - u - v) + tri.v1 * u + tri.v2 * v;
                }

                // ── Spacing check ──
                if (grid.ExistsInRadius(point, spacing))
                    continue;

                // ── Drain hole exclusion ──
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

                // ── Force estimation ──
                float overhangArea = tri.area * samples; // approximate region area
                int supportsInRegion = Math.Max(1, (int)(overhangArea / (spacing * spacing)));
                var force = ForceEstimator.Estimate(
                    point.Z, overhangArea, supportsInRegion,
                    overhangArea, spacing,
                    config.Orientation, config.RecoaterSpeedMmS);

                // ── Priority: lower Z = more critical ──
                float priority = 1f - Math.Clamp(point.Z / (mesh.Max.Z - mesh.Min.Z + 1f), 0, 1);
                // Steeper overhangs get higher priority
                priority += steepness * 0.3f;

                string id = $"sp-{++idCounter}";
                grid.Insert(point, id);
                points.Add(new SupportPoint
                {
                    Id = id,
                    Position = point,
                    Normal = tri.normal,
                    OverhangArea = overhangArea,
                    OverhangType = point.Z < 2f
                        ? OverhangAnalyzer.OverhangType.NewIsland
                        : steepness > 0.9f
                            ? OverhangAnalyzer.OverhangType.BulkOverhang
                            : OverhangAnalyzer.OverhangType.Peninsula,
                    Priority = priority,
                    RecommendedWeight = force.Weight,
                    SafetyFactor = force.SafetyFactor,
                });
            }
        }

        sw.Stop();
        return new GenerationResult
        {
            Points = points,
            OverhangRegions = new List<OverhangAnalyzer.OverhangRegion>(), // triangle-based — no contour regions
            OverhangRegionsAnalyzed = exteriorTris.Count,
            IslandsDetected = exteriorTris.Count(t => t.centroid.Z < 2f),
            TotalOverhangArea = totalOverhangArea,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
