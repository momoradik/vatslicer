using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates support points based on structural analysis of overhang regions.
///
/// Unlike the old approach (triangle-centroid + uniform spacing), this:
/// - Uses layer-based overhang analysis to understand structural context
/// - Places supports based on overhang type (island=dense, peninsula=edge, bulk=grid)
/// - Estimates forces to auto-select support weight (light/medium/heavy)
/// - Uses spatial grid for O(1) deduplication and spacing checks
/// - Prioritizes by structural importance (islands first, then peninsulas, then bulk)
/// - Verifies coverage: every overhang region has adequate support
/// </summary>
public sealed class SupportPointGenerator
{
    /// <summary>
    /// A generated support point with structural metadata.
    /// </summary>
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
        /// <summary>Minimum spacing between supports (mm).</summary>
        public float MinSpacingMm { get; init; } = 2.0f;
        /// <summary>Maximum spacing between supports (mm).</summary>
        public float MaxSpacingMm { get; init; } = 8.0f;
        /// <summary>Density factor: 0=sparse, 1=dense.</summary>
        public float DensityFactor { get; init; } = 0.5f;
        /// <summary>Printer orientation.</summary>
        public PrinterOrientation Orientation { get; init; } = PrinterOrientation.BottomUp;
        /// <summary>Recoater speed (mm/s), 0 if no recoater.</summary>
        public float RecoaterSpeedMmS { get; init; } = 0;
        /// <summary>Layer height for overhang analysis (mm). Coarser = faster.</summary>
        public float LayerHeightMm { get; init; } = 1.0f;
        /// <summary>Drain hole positions to avoid. Supports won't be placed within clearance of drain holes.</summary>
        public List<(Vector3 position, float radiusMm)>? DrainHoleExclusions { get; init; }
        /// <summary>Clearance distance around drain holes (mm).</summary>
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
    /// Generate support points for the given mesh.
    /// </summary>
    public static GenerationResult Generate(StlMesh mesh, GenerationConfig config, AabbBvh? prebuiltBvh = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Analyze overhangs layer by layer
        var analysis = OverhangAnalyzer.Analyze(mesh, config.LayerHeightMm);

        // Step 2: Build spatial grid for deduplication
        float baseSpacing = config.MinSpacingMm + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);
        var grid = new SpatialGrid<string>(baseSpacing);
        var points = new List<SupportPoint>();
        int idCounter = 0;

        // Step 3: Use pre-built BVH or build one (skip for very large meshes)
        AabbBvh? bvh = prebuiltBvh ?? (mesh.TriangleCount <= 50000 ? AabbBvh.Build(mesh) : null);

        // Pre-compute overhang triangle sets per Z layer (cached for reuse across regions at same Z)
        var overhangTriCache = new Dictionary<int, HashSet<int>>();
        HashSet<int>? GetOverhangTris(float z)
        {
            int zKey = (int)(z * 10); // 0.1mm precision cache key
            if (!overhangTriCache.TryGetValue(zKey, out var tris))
            {
                tris = mesh.FindOverhangTrianglesAtZ(z);
                overhangTriCache[zKey] = tris;
            }
            return tris.Count > 0 ? tris : null;
        }

        // Step 4: Process overhang regions in priority order (highest priority first)
        var allRegions = analysis.Layers
            .SelectMany(l => l.Regions)
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.Area)
            .ToList();

        foreach (var region in allRegions)
        {
            // ── Variable density zoning ──────────────────────────────────
            // Spacing is determined by overhang type and structural context:
            //   Islands:            minSpacing (densest — critical unsupported regions)
            //   Bridge endpoints:   minSpacing * 1.2 (high stress at connection points)
            //   Peninsula edges:    minSpacing * 1.5 (moderate — partially supported)
            //   Bulk overhangs:     maxSpacing (sparsest — mostly supported)
            //   Large flat (>100mm²): dense boundary + sparse interior
            //   Near model edges:   30% density increase
            float spacing = baseSpacing;
            switch (region.Type)
            {
                case OverhangAnalyzer.OverhangType.NewIsland:
                    spacing = config.MinSpacingMm; // densest for islands
                    break;
                case OverhangAnalyzer.OverhangType.Bridge:
                    spacing = config.MinSpacingMm * 1.2f; // bridge endpoints need dense support
                    break;
                case OverhangAnalyzer.OverhangType.Peninsula:
                    spacing = config.MinSpacingMm * 1.5f; // peninsula edges
                    break;
                case OverhangAnalyzer.OverhangType.BulkOverhang:
                    spacing = config.MaxSpacingMm; // sparsest for bulk overhangs
                    break;
            }

            // Near-edge density boost: regions with high priority (structural importance)
            // get 30% denser spacing — these tend to be at model boundaries
            if (region.Priority > 0.7f && region.Type != OverhangAnalyzer.OverhangType.NewIsland)
            {
                spacing *= 0.7f; // 30% increase in density
            }

            // Generate candidate points within the overhang region
            // For large flat overhangs (>100mm²), use dual-zone strategy:
            // dense boundary sampling + sparse interior grid
            List<(Vector2 pos, string type)> candidates;
            if (region.Type == OverhangAnalyzer.OverhangType.BulkOverhang && region.Area > 100f)
            {
                candidates = GenerateDualZoneCandidates(region, spacing, config.MinSpacingMm);
            }
            else
            {
                candidates = GenerateCandidates(region, spacing);
            }

            foreach (var (pos2d, candidateType) in candidates)
            {
                var pos3d = new Vector3(pos2d.X, pos2d.Y, region.Z);

                // Check spacing against existing points using spatial grid
                if (grid.ExistsInRadius(pos3d, spacing))
                    continue;

                // Check drain hole exclusion zones
                if (config.DrainHoleExclusions is { Count: > 0 })
                {
                    bool tooCloseToHole = false;
                    foreach (var (holePos, holeR) in config.DrainHoleExclusions)
                    {
                        if (Vector3.Distance(pos3d, holePos) < holeR + config.DrainHoleClearanceMm)
                        { tooCloseToHole = true; break; }
                    }
                    if (tooCloseToHole) continue;
                }

                // Use filtered BVH ClosestPoint — only search overhang triangles at this Z.
                // This prevents the BVH from redirecting to a nearby wall face.
                Vector3 surfaceNormal = new Vector3(0, 0, -1);
                Vector3 surfacePoint = pos3d;

                if (bvh != null)
                {
                    // Get the overhang triangles for this layer
                    var overhangTris = GetOverhangTris(region.Z);

                    // First try: filtered search (only overhang triangles)
                    var closest = overhangTris != null
                        ? bvh.ClosestPoint(pos3d, overhangTris)
                        : bvh.ClosestPoint(pos3d);

                    if (closest.HasValue)
                    {
                        surfacePoint = closest.Value.Point;
                        surfaceNormal = closest.Value.Normal;
                    }

                    // For BulkOverhang: verify the found surface is actually an overhang
                    if (region.Type == OverhangAnalyzer.OverhangType.BulkOverhang
                        && surfaceNormal.Z > -0.1f)
                        continue;

                    // Reject candidates inside the mesh
                    if (bvh.IsInside(pos3d))
                        continue;

                    // Interior surfaces are already filtered at the OverhangAnalyzer level
                    // by checking contour winding direction (CCW = outer, CW = inner/hole).
                }

                // Force estimation
                int supportsInRegion = Math.Max(1, (int)(region.Area / (spacing * spacing)));
                var force = ForceEstimator.Estimate(
                    surfacePoint.Z, region.Area, supportsInRegion,
                    region.Area, spacing,
                    config.Orientation, config.RecoaterSpeedMmS);

                string id = $"sp-{++idCounter}";
                grid.Insert(surfacePoint, id);
                points.Add(new SupportPoint
                {
                    Id = id,
                    Position = surfacePoint,
                    Normal = surfaceNormal,
                    OverhangArea = region.Area,
                    OverhangType = region.Type,
                    Priority = region.Priority,
                    RecommendedWeight = force.Weight,
                    SafetyFactor = force.SafetyFactor,
                });
            }
        }

        // Step 5: Coverage verification — fill uncovered overhang regions
        // For large regions, add multiple supports in a grid pattern, not just one at centroid
        foreach (var region in allRegions)
        {
            if (region.Area < 1.0f) continue;

            var regionCenter3d = new Vector3(region.Centroid.X, region.Centroid.Y, region.Z);
            // Use a tighter radius than the validator to ensure fill is always
            // sufficient. Validator uses min(8, sqrt(effectiveArea/π)*2) where
            // effectiveArea can be smaller than region.Area after intersection.
            // Use 80% of the naive coverage radius to guarantee overlap.
            float regionRadius = MathF.Sqrt(region.Area / MathF.PI);
            float fillCoverageRadius = Math.Min(8f, regionRadius * 2f) * 0.8f;
            if (grid.ExistsInRadius(regionCenter3d, fillCoverageRadius))
                continue;

            // Generate fill points for uncovered region
            var fillCandidates = new List<Vector3> { regionCenter3d };

            // For large regions, add grid fill points
            if (region.Area > baseSpacing * baseSpacing && region.Contour.Count >= 3)
            {
                float minX = region.Contour.Min(p => p.X), maxX = region.Contour.Max(p => p.X);
                float minY = region.Contour.Min(p => p.Y), maxY = region.Contour.Max(p => p.Y);
                for (float x = minX + baseSpacing * 0.5f; x <= maxX; x += baseSpacing)
                for (float y = minY + baseSpacing * 0.5f; y <= maxY; y += baseSpacing)
                {
                    var pt2d = new Vector2(x, y);
                    if (PointInPolygon(pt2d, region.Contour))
                        fillCandidates.Add(new Vector3(x, y, region.Z));
                }
            }

            foreach (var candidate in fillCandidates)
            {
                if (grid.ExistsInRadius(candidate, baseSpacing * 0.8f))
                    continue;

                // Respect drain hole exclusion zones in coverage fill too
                if (config.DrainHoleExclusions is { Count: > 0 })
                {
                    bool tooClose = false;
                    foreach (var (holePos, holeR) in config.DrainHoleExclusions)
                    {
                        if (Vector3.Distance(candidate, holePos) < holeR + config.DrainHoleClearanceMm)
                        { tooClose = true; break; }
                    }
                    if (tooClose) continue;
                }

                Vector3 surfacePoint = candidate;
                Vector3 surfaceNormal = new Vector3(0, 0, -1);
                if (bvh != null)
                {
                    // Use filtered ClosestPoint to find overhang surface, not walls
                    var overhangTris = GetOverhangTris(region.Z);
                    var closest = overhangTris != null
                        ? bvh.ClosestPoint(candidate, overhangTris)
                        : null;

                    if (closest.HasValue)
                    {
                        surfacePoint = closest.Value.Point;
                        surfaceNormal = closest.Value.Normal;
                    }
                    // else: no overhang triangle found — use candidate position directly

                    // Reject supports trapped inside hollow geometry
                    if (surfacePoint.Z > 2f)
                    {
                        var downHit = bvh.RayCast(surfacePoint - new Vector3(0, 0, 0.5f), -Vector3.UnitZ);
                        if (downHit.HasValue && downHit.Value.Distance < surfacePoint.Z - 1f)
                            continue;
                    }
                }

                int supportsInRegion = Math.Max(1, (int)(region.Area / (baseSpacing * baseSpacing)));
                var force = ForceEstimator.Estimate(
                    surfacePoint.Z, region.Area, supportsInRegion,
                    region.Area, baseSpacing,
                    config.Orientation, config.RecoaterSpeedMmS);

                string id = $"sp-{++idCounter}";
                grid.Insert(surfacePoint, id);
                points.Add(new SupportPoint
                {
                    Id = id,
                    Position = surfacePoint,
                    Normal = surfaceNormal,
                    OverhangArea = region.Area,
                    OverhangType = region.Type,
                    Priority = region.Priority,
                    RecommendedWeight = force.Weight,
                    SafetyFactor = force.SafetyFactor,
                });
            }
        }

        sw.Stop();
        return new GenerationResult
        {
            Points = points,
            OverhangRegions = allRegions,
            OverhangRegionsAnalyzed = allRegions.Count,
            IslandsDetected = analysis.TotalIslands,
            TotalOverhangArea = analysis.TotalOverhangArea,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // ── Candidate generation ─────────────────────────────────────────────

    private static List<(Vector2 pos, string type)> GenerateCandidates(
        OverhangAnalyzer.OverhangRegion region, float spacing)
    {
        var candidates = new List<(Vector2, string)>();
        var contour = region.Contour;
        if (contour.Count < 3) return candidates;

        // Compute bounding box of the contour
        float minX = contour.Min(p => p.X), maxX = contour.Max(p => p.X);
        float minY = contour.Min(p => p.Y), maxY = contour.Max(p => p.Y);

        switch (region.Type)
        {
            case OverhangAnalyzer.OverhangType.NewIsland:
                // Dense grid + edges + centroid for new islands
                GridSample(candidates, contour, minX, minY, maxX, maxY, spacing, "grid");
                EdgeSample(candidates, contour, spacing, "edge");
                candidates.Add((region.Centroid, "centroid"));
                break;

            case OverhangAnalyzer.OverhangType.Peninsula:
                // Edge samples along the unsupported boundary
                EdgeSample(candidates, contour, spacing, "edge");
                candidates.Add((region.Centroid, "centroid"));
                break;

            case OverhangAnalyzer.OverhangType.Bridge:
                // Line samples along the bridge direction + edges
                EdgeSample(candidates, contour, spacing * 0.7f, "edge");
                candidates.Add((region.Centroid, "centroid"));
                break;

            case OverhangAnalyzer.OverhangType.BulkOverhang:
                // Regular grid sampling
                GridSample(candidates, contour, minX, minY, maxX, maxY, spacing, "grid");
                break;
        }

        return candidates;
    }

    /// <summary>
    /// Sample points on a regular grid within the contour polygon.
    /// </summary>
    private static void GridSample(List<(Vector2, string)> candidates,
        List<Vector2> contour, float minX, float minY, float maxX, float maxY,
        float spacing, string type)
    {
        for (float x = minX + spacing * 0.5f; x <= maxX; x += spacing)
        for (float y = minY + spacing * 0.5f; y <= maxY; y += spacing)
        {
            var pt = new Vector2(x, y);
            if (PointInPolygon(pt, contour))
                candidates.Add((pt, type));
        }
    }

    /// <summary>
    /// Sample points along the edges of the contour polygon.
    /// </summary>
    private static void EdgeSample(List<(Vector2, string)> candidates,
        List<Vector2> contour, float spacing, string type)
    {
        for (int i = 0; i < contour.Count; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % contour.Count];
            float edgeLen = Vector2.Distance(a, b);
            if (edgeLen < spacing * 0.5f) continue;

            int samples = Math.Max(1, (int)(edgeLen / spacing));
            for (int s = 0; s <= samples; s++)
            {
                float t = (float)s / (samples + 1);
                candidates.Add((Vector2.Lerp(a, b, t), type));
            }
        }
    }

    /// <summary>
    /// Generate candidates for large flat overhangs (>100mm²) using a dual-zone strategy:
    /// dense boundary samples along the contour edges + sparse interior grid.
    /// This ensures adequate edge support while minimizing interior material usage.
    /// </summary>
    private static List<(Vector2 pos, string type)> GenerateDualZoneCandidates(
        OverhangAnalyzer.OverhangRegion region, float interiorSpacing, float boundarySpacing)
    {
        var candidates = new List<(Vector2, string)>();
        var contour = region.Contour;
        if (contour.Count < 3) return candidates;

        // Boundary zone: dense edge samples along the contour perimeter
        EdgeSample(candidates, contour, boundarySpacing, "edge");

        // Interior zone: sparse grid sampling inside the polygon
        float minX = contour.Min(p => p.X), maxX = contour.Max(p => p.X);
        float minY = contour.Min(p => p.Y), maxY = contour.Max(p => p.Y);
        GridSample(candidates, contour, minX, minY, maxX, maxY, interiorSpacing, "grid");

        // Centroid for structural center
        candidates.Add((region.Centroid, "centroid"));

        return candidates;
    }

    private static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y) &&
                point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        }
        return inside;
    }
}
