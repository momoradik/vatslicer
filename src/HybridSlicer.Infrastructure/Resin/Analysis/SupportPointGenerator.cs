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

        // Step 4: Process overhang regions in priority order (highest priority first)
        var allRegions = analysis.Layers
            .SelectMany(l => l.Regions)
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.Area)
            .ToList();

        foreach (var region in allRegions)
        {
            // Determine spacing for this region based on type and priority
            float spacing = baseSpacing;
            switch (region.Type)
            {
                case OverhangAnalyzer.OverhangType.NewIsland:
                    spacing = config.MinSpacingMm; // dense for islands
                    break;
                case OverhangAnalyzer.OverhangType.Bridge:
                    spacing = config.MinSpacingMm * 1.5f;
                    break;
                case OverhangAnalyzer.OverhangType.Peninsula:
                    spacing = baseSpacing * 0.8f;
                    break;
                case OverhangAnalyzer.OverhangType.BulkOverhang:
                    spacing = baseSpacing;
                    break;
            }

            // Generate candidate points within the overhang region
            var candidates = GenerateCandidates(region, spacing);

            foreach (var (pos2d, candidateType) in candidates)
            {
                var pos3d = new Vector3(pos2d.X, pos2d.Y, region.Z);

                // Check spacing against existing points using spatial grid
                if (grid.ExistsInRadius(pos3d, spacing * 0.8f))
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

                // Use BVH for precise normal if available, otherwise use overhang face normal
                Vector3 surfaceNormal;
                Vector3 surfacePoint;
                if (bvh != null)
                {
                    var closest = bvh.ClosestPoint(pos3d);
                    surfaceNormal = closest?.Normal ?? new Vector3(0, 0, -1);
                    surfacePoint = closest?.Point ?? pos3d;
                }
                else
                {
                    surfaceNormal = new Vector3(0, 0, -1); // overhang faces point down
                    surfacePoint = pos3d;
                }

                // Only place if the surface is actually an overhang (normal points downward)
                if (surfaceNormal.Z > -0.3f && bvh != null) continue; // skip if BVH says not overhang

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

        // Step 5: Coverage verification — ensure no large overhang region is left uncovered
        foreach (var region in allRegions)
        {
            if (region.Area < 1.0f) continue; // skip tiny overhangs

            var regionCenter3d = new Vector3(region.Centroid.X, region.Centroid.Y, region.Z);
            if (!grid.ExistsInRadius(regionCenter3d, baseSpacing * 2f))
            {
                // This region has no nearby support — add one at centroid
                Vector3 surfacePoint = regionCenter3d;
                Vector3 surfaceNormal = new Vector3(0, 0, -1);
                if (bvh != null)
                {
                    var closest = bvh.ClosestPoint(regionCenter3d);
                    surfacePoint = closest?.Point ?? regionCenter3d;
                    surfaceNormal = closest?.Normal ?? new Vector3(0, 0, -1);
                }

                var force = ForceEstimator.Estimate(
                    surfacePoint.Z, region.Area, 1,
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
