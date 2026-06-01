using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Batch layer slicer: processes all Z layers in a single pass.
///
/// Instead of iterating all triangles for each layer (O(N*L) where N=triangles, L=layers),
/// this sorts triangles by Z-span and sweeps through layers, only testing triangles
/// whose Z range overlaps the current layer. Amortized O(N + L*k) where k = avg active triangles.
///
/// Also performs per-layer island detection: identifies contours that have no overlap
/// with the previous layer (floating/disconnected geometry that needs support from its first layer).
/// </summary>
public sealed class BatchSlicer
{
    /// <summary>
    /// Result for a single layer.
    /// </summary>
    public sealed class LayerResult
    {
        public required float Z { get; init; }
        public required List<List<Vector2>> Contours { get; init; }
        public required int IslandCount { get; init; }
        public required float TotalArea { get; init; }
        /// <summary>
        /// Contours in this layer that are "born" — they have no overlap with
        /// any contour in the previous layer. These are the most critical
        /// features that absolutely need support.
        /// </summary>
        public required List<List<Vector2>> BornIslands { get; init; }
    }

    /// <summary>
    /// Slice the mesh at regular Z intervals, tracking islands per layer.
    /// </summary>
    /// <param name="mesh">The mesh to slice</param>
    /// <param name="layerHeight">Distance between layers (mm)</param>
    /// <param name="startZ">First layer Z (default: layerHeight/2 above mesh min Z)</param>
    /// <returns>List of layer results from bottom to top</returns>
    public static List<LayerResult> SliceAll(StlMesh mesh, float layerHeight, float? startZ = null)
    {
        float meshMinZ = mesh.Min.Z;
        float meshMaxZ = mesh.Max.Z;
        float firstZ = startZ ?? (meshMinZ + layerHeight * 0.5f);

        int layerCount = (int)MathF.Ceiling((meshMaxZ - firstZ) / layerHeight) + 1;
        if (layerCount <= 0) return new List<LayerResult>();

        // Pre-compute Z values for all layers
        var zValues = new float[layerCount];
        for (int i = 0; i < layerCount; i++)
            zValues[i] = firstZ + i * layerHeight;

        // Sort triangles by their minimum Z for sweep efficiency
        var triOrder = new int[mesh.TriangleCount];
        var triMinZ = new float[mesh.TriangleCount];
        var triMaxZ = new float[mesh.TriangleCount];
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            triOrder[t] = t;
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            triMinZ[t] = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
            triMaxZ[t] = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));
        }
        Array.Sort(triOrder, (a, b) => triMinZ[a].CompareTo(triMinZ[b]));

        // Sweep through layers
        var results = new List<LayerResult>(layerCount);
        List<List<Vector2>>? prevContours = null;
        int sweepStart = 0; // index into triOrder — triangles below this are fully below current Z

        for (int li = 0; li < layerCount; li++)
        {
            float z = zValues[li];

            // Advance sweepStart past triangles whose maxZ is below this layer
            while (sweepStart < triOrder.Length && triMaxZ[triOrder[sweepStart]] < z)
                sweepStart++;

            // Collect edge-plane intersection segments from active triangles
            var segments = new List<(Vector2 a, Vector2 b)>();
            for (int si = sweepStart; si < triOrder.Length; si++)
            {
                int t = triOrder[si];
                if (triMinZ[t] > z) break; // all remaining triangles are above this layer

                var v0 = mesh.Vertices[t * 3];
                var v1 = mesh.Vertices[t * 3 + 1];
                var v2 = mesh.Vertices[t * 3 + 2];

                var pts = new List<Vector2>(3);
                AddEdgeIntersection(pts, v0, v1, z);
                AddEdgeIntersection(pts, v1, v2, z);
                AddEdgeIntersection(pts, v2, v0, z);

                if (pts.Count >= 2)
                    segments.Add((pts[0], pts[1]));
            }

            // Chain segments into closed contours
            var contours = ChainSegments(segments);

            // Compute total area
            float totalArea = 0;
            foreach (var contour in contours)
                totalArea += Math.Abs(PolygonArea(contour));

            // Island detection: find contours with no overlap to previous layer
            int islandCount = 0;
            var bornIslands = new List<List<Vector2>>();

            if (prevContours != null && prevContours.Count > 0)
            {
                foreach (var contour in contours)
                {
                    if (contour.Count < 3) continue;
                    var centroid = ComputeCentroid(contour);
                    bool hasSupport = false;

                    foreach (var prevContour in prevContours)
                    {
                        if (PointInPolygon(centroid, prevContour))
                        {
                            hasSupport = true;
                            break;
                        }
                    }

                    if (!hasSupport)
                    {
                        islandCount++;
                        bornIslands.Add(contour);
                    }
                }
            }

            results.Add(new LayerResult
            {
                Z = z,
                Contours = contours,
                IslandCount = islandCount,
                TotalArea = totalArea,
                BornIslands = bornIslands,
            });

            prevContours = contours;
        }

        return results;
    }

    /// <summary>
    /// Compute overhang regions between two consecutive layers.
    /// Returns contours in the current layer that extend beyond the previous layer
    /// (areas that have no support from the layer below).
    /// </summary>
    public static List<OverhangRegion> ComputeOverhangs(
        List<List<Vector2>> currentContours,
        List<List<Vector2>> previousContours,
        float z)
    {
        var overhangs = new List<OverhangRegion>();
        if (previousContours.Count == 0)
        {
            // First layer — everything is an overhang (needs bed adhesion)
            foreach (var c in currentContours)
            {
                if (c.Count < 3) continue;
                overhangs.Add(new OverhangRegion
                {
                    Contour = c,
                    Z = z,
                    Area = Math.Abs(PolygonArea(c)),
                    Centroid = ComputeCentroid(c),
                    Type = OverhangType.NewIsland,
                });
            }
            return overhangs;
        }

        // For each point in each current contour, check if it's inside any previous contour.
        // Points that are NOT inside previous contours are overhang regions.
        foreach (var contour in currentContours)
        {
            if (contour.Count < 3) continue;
            var centroid = ComputeCentroid(contour);
            float area = Math.Abs(PolygonArea(contour));

            bool centroidSupported = false;
            foreach (var prev in previousContours)
            {
                if (PointInPolygon(centroid, prev))
                {
                    centroidSupported = true;
                    break;
                }
            }

            if (!centroidSupported)
            {
                overhangs.Add(new OverhangRegion
                {
                    Contour = contour,
                    Z = z,
                    Area = area,
                    Centroid = centroid,
                    Type = OverhangType.NewIsland,
                });
                continue;
            }

            // Check boundary points — if some are outside previous contours,
            // this is a peninsula/overhang extension
            int outsideCount = 0;
            foreach (var pt in contour)
            {
                bool inside = false;
                foreach (var prev in previousContours)
                {
                    if (PointInPolygon(pt, prev)) { inside = true; break; }
                }
                if (!inside) outsideCount++;
            }

            if (outsideCount > 0)
            {
                float overhangFraction = (float)outsideCount / contour.Count;
                if (overhangFraction > 0.1f) // more than 10% of boundary is unsupported
                {
                    var type = overhangFraction > 0.8f ? OverhangType.Bridge :
                               overhangFraction > 0.3f ? OverhangType.Peninsula :
                               OverhangType.BulkOverhang;
                    overhangs.Add(new OverhangRegion
                    {
                        Contour = contour,
                        Z = z,
                        Area = area * overhangFraction,
                        Centroid = centroid,
                        Type = type,
                    });
                }
            }
        }

        return overhangs;
    }

    // ── Overhang types ───────────────────────────────────────────────────

    public enum OverhangType
    {
        NewIsland,      // contour appears for the first time (no previous layer)
        Peninsula,      // narrow protrusion extending beyond previous layer
        Bridge,         // thin connection between supported regions
        BulkOverhang,   // large flat area extending beyond previous layer
    }

    public sealed class OverhangRegion
    {
        public required List<Vector2> Contour { get; init; }
        public required float Z { get; init; }
        public required float Area { get; init; }
        public required Vector2 Centroid { get; init; }
        public required OverhangType Type { get; init; }
    }

    // ── Geometry helpers ─────────────────────────────────────────────────

    private static void AddEdgeIntersection(List<Vector2> pts, Vector3 a, Vector3 b, float z)
    {
        if ((a.Z - z) * (b.Z - z) > 0) return;
        if (Math.Abs(a.Z - b.Z) < 1e-8f) return;
        float t = (z - a.Z) / (b.Z - a.Z);
        if (t < 0 || t > 1) return;
        pts.Add(new Vector2(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
    }

    private static List<List<Vector2>> ChainSegments(List<(Vector2 a, Vector2 b)> segments)
    {
        if (segments.Count == 0) return new List<List<Vector2>>();

        var result = new List<List<Vector2>>();

        // Build hash-based lookup for O(1) endpoint matching instead of O(n²)
        const float EPS = 0.001f;
        float invEps = 1f / (EPS * 10); // grid cell size = 10x epsilon
        var endpointMap = new Dictionary<long, List<(int idx, bool isA)>>();

        long HashPt(Vector2 p) => ((long)(int)MathF.Floor(p.X * invEps) * 73856093L) ^
                                  ((long)(int)MathF.Floor(p.Y * invEps) * 19349669L);

        for (int i = 0; i < segments.Count; i++)
        {
            var keyA = HashPt(segments[i].a);
            var keyB = HashPt(segments[i].b);
            if (!endpointMap.TryGetValue(keyA, out var listA)) { listA = new(); endpointMap[keyA] = listA; }
            listA.Add((i, true));
            if (!endpointMap.TryGetValue(keyB, out var listB)) { listB = new(); endpointMap[keyB] = listB; }
            listB.Add((i, false));
        }

        var used = new bool[segments.Count];

        for (int start = 0; start < segments.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;

            var polygon = new List<Vector2> { segments[start].a, segments[start].b };
            var current = segments[start].b;

            for (int iter = 0; iter < segments.Count; iter++)
            {
                var key = HashPt(current);
                bool found = false;

                // Check current cell and neighbors for matching endpoints
                for (int dx = -1; dx <= 1 && !found; dx++)
                for (int dy = -1; dy <= 1 && !found; dy++)
                {
                    long neighborKey = ((long)((int)MathF.Floor(current.X * invEps) + dx) * 73856093L) ^
                                      ((long)((int)MathF.Floor(current.Y * invEps) + dy) * 19349669L);
                    if (!endpointMap.TryGetValue(neighborKey, out var candidates)) continue;

                    foreach (var (idx, isA) in candidates)
                    {
                        if (used[idx]) continue;
                        var pt = isA ? segments[idx].a : segments[idx].b;
                        if (Vector2.Distance(current, pt) < EPS)
                        {
                            used[idx] = true;
                            var other = isA ? segments[idx].b : segments[idx].a;
                            polygon.Add(other);
                            current = other;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found) break;
            }

            if (polygon.Count >= 3) result.Add(polygon);
        }
        return result;
    }

    private static Vector2 ComputeCentroid(List<Vector2> polygon)
    {
        float cx = 0, cy = 0;
        foreach (var p in polygon) { cx += p.X; cy += p.Y; }
        return new Vector2(cx / polygon.Count, cy / polygon.Count);
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

    /// <summary>
    /// Compute signed area of a polygon using the shoelace formula.
    /// Positive = counterclockwise, negative = clockwise.
    /// </summary>
    public static float PolygonArea(List<Vector2> polygon)
    {
        float area = 0;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
            area += (polygon[j].X + polygon[i].X) * (polygon[j].Y - polygon[i].Y);
        return area * 0.5f;
    }
}
