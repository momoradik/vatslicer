using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Layer-based overhang detection.
///
/// Instead of detecting overhangs per-triangle (which misses structural context),
/// this analyzes layers from bottom to top, computing:
/// - Layer polygon contours
/// - Overhang regions (current layer areas not supported by the previous layer)
/// - Island births (contours that appear for the first time)
/// - Overhang classification (island, peninsula, bridge, bulk)
///
/// This is how PrusaSlicer detects overhangs: by layer difference, not face normals.
/// </summary>
public sealed class OverhangAnalyzer
{
    /// <summary>
    /// Analyzed overhang data for the full model.
    /// </summary>
    public sealed class AnalysisResult
    {
        public required List<LayerOverhangs> Layers { get; init; }
        public required int TotalOverhangRegions { get; init; }
        public required int TotalIslands { get; init; }
        public required float TotalOverhangArea { get; init; }
        public required long ElapsedMs { get; init; }
    }

    /// <summary>
    /// Overhang data for a single layer.
    /// </summary>
    public sealed class LayerOverhangs
    {
        public required float Z { get; init; }
        public required List<OverhangRegion> Regions { get; init; }
        public required float LayerArea { get; init; }
        public required float OverhangArea { get; init; }
        public required bool HasNewIslands { get; init; }
    }

    /// <summary>
    /// A single overhang region within a layer.
    /// </summary>
    public sealed class OverhangRegion
    {
        public required List<Vector2> Contour { get; init; }
        public required float Z { get; init; }
        public required float Area { get; init; }
        public required Vector2 Centroid { get; init; }
        public required OverhangType Type { get; init; }
        /// <summary>
        /// Structural priority: 0 = low, 1 = critical.
        /// New islands and large overhangs get higher priority.
        /// </summary>
        public required float Priority { get; init; }
    }

    public enum OverhangType
    {
        /// <summary>Contour appears for the first time — no overlap with previous layer.</summary>
        NewIsland,
        /// <summary>Narrow protrusion extending beyond previous layer boundary.</summary>
        Peninsula,
        /// <summary>Thin connection between two supported regions.</summary>
        Bridge,
        /// <summary>Large flat area extending beyond previous layer.</summary>
        BulkOverhang,
    }

    /// <summary>
    /// Analyze the mesh for overhangs at the given layer height.
    /// Returns per-layer overhang data from bottom to top.
    /// </summary>
    public static AnalysisResult Analyze(StlMesh mesh, float layerHeight)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Use batch slicer to get all layers with island detection
        var slicedLayers = BatchSlicer.SliceAll(mesh, layerHeight);

        var layers = new List<LayerOverhangs>(slicedLayers.Count);
        int totalRegions = 0, totalIslands = 0;
        float totalArea = 0;

        List<List<Vector2>>? prevContours = null;

        foreach (var sl in slicedLayers)
        {
            var regions = new List<OverhangRegion>();
            float overhangArea = 0;
            bool hasIslands = false;

            if (prevContours == null)
            {
                // First layer: everything is on the build plate.
                // Still mark large unsupported areas that need bed adhesion.
                foreach (var contour in sl.Contours)
                {
                    if (contour.Count < 3) continue;
                    float area = Math.Abs(BatchSlicer.PolygonArea(contour));
                    if (area < 0.1f) continue; // skip tiny fragments
                    regions.Add(new OverhangRegion
                    {
                        Contour = contour,
                        Z = sl.Z,
                        Area = area,
                        Centroid = Centroid(contour),
                        Type = OverhangType.NewIsland,
                        Priority = 1.0f, // first layer is always critical
                    });
                    overhangArea += area;
                    hasIslands = true;
                }
            }
            else
            {
                // Compare with previous layer to find overhang regions
                foreach (var contour in sl.Contours)
                {
                    if (contour.Count < 3) continue;
                    float area = Math.Abs(BatchSlicer.PolygonArea(contour));
                    if (area < 0.1f) continue;

                    var centroid = Centroid(contour);

                    // Check how much of this contour overlaps with the previous layer
                    int totalPts = contour.Count;
                    int supportedPts = 0;
                    bool centroidSupported = false;

                    foreach (var pt in contour)
                    {
                        if (IsPointInAnyPolygon(pt, prevContours))
                            supportedPts++;
                    }
                    centroidSupported = IsPointInAnyPolygon(centroid, prevContours);

                    float supportRatio = (float)supportedPts / totalPts;

                    if (!centroidSupported && supportRatio < 0.2f)
                    {
                        // New island: almost no overlap with previous layer
                        regions.Add(new OverhangRegion
                        {
                            Contour = contour, Z = sl.Z, Area = area, Centroid = centroid,
                            Type = OverhangType.NewIsland,
                            Priority = 1.0f, // islands are critical
                        });
                        overhangArea += area;
                        hasIslands = true;
                        totalIslands++;
                    }
                    else if (supportRatio < 0.5f)
                    {
                        // Bridge or peninsula: significant unsupported portion
                        float ovhArea = area * (1f - supportRatio);
                        var type = (1f - supportRatio) > 0.7f ? OverhangType.Bridge : OverhangType.Peninsula;
                        regions.Add(new OverhangRegion
                        {
                            Contour = contour, Z = sl.Z, Area = ovhArea, Centroid = centroid,
                            Type = type,
                            Priority = 0.7f + (1f - supportRatio) * 0.3f,
                        });
                        overhangArea += ovhArea;
                    }
                    else if (supportRatio < 0.9f)
                    {
                        // Bulk overhang: mostly supported but some edge extension
                        float ovhArea = area * (1f - supportRatio);
                        if (ovhArea > 0.5f) // only report significant overhangs
                        {
                            regions.Add(new OverhangRegion
                            {
                                Contour = contour, Z = sl.Z, Area = ovhArea, Centroid = centroid,
                                Type = OverhangType.BulkOverhang,
                                Priority = 0.3f + (1f - supportRatio) * 0.4f,
                            });
                            overhangArea += ovhArea;
                        }
                    }
                    // else: fully supported, no overhang
                }
            }

            if (regions.Count > 0)
            {
                layers.Add(new LayerOverhangs
                {
                    Z = sl.Z,
                    Regions = regions,
                    LayerArea = sl.TotalArea,
                    OverhangArea = overhangArea,
                    HasNewIslands = hasIslands,
                });
                totalRegions += regions.Count;
                totalArea += overhangArea;
            }

            prevContours = sl.Contours;
        }

        sw.Stop();
        return new AnalysisResult
        {
            Layers = layers,
            TotalOverhangRegions = totalRegions,
            TotalIslands = totalIslands,
            TotalOverhangArea = totalArea,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Vector2 Centroid(List<Vector2> polygon)
    {
        float cx = 0, cy = 0;
        foreach (var p in polygon) { cx += p.X; cy += p.Y; }
        return new Vector2(cx / polygon.Count, cy / polygon.Count);
    }

    private static bool IsPointInAnyPolygon(Vector2 point, List<List<Vector2>> polygons)
    {
        foreach (var poly in polygons)
        {
            if (poly.Count < 3) continue;
            if (PointInPolygon(point, poly)) return true;
        }
        return false;
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
