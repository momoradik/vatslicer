using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>Reinforcement mode for support-to-support connections.</summary>
public enum ReinforcementMode
{
    /// <summary>No interconnections.</summary>
    None,
    /// <summary>Existing pairwise bracing (ladder on every pair within distance).</summary>
    Pairwise,
    /// <summary>Triangulated bracing — Delaunay edges only, closed triangles for rigidity.</summary>
    Triangular,
    /// <summary>Full triangulated network — all Delaunay edges across the entire pillar set.</summary>
    Global,
}

/// <summary>
/// Builds cross-connections between support pillars for structural rigidity.
///
/// Rules (configurable):
/// - Pillars above maxSoloHeight must have at least 1 connection
/// - Pillars above maxDualHeight must have at least 2 connections
/// - Lonely pillars (no neighbor within maxDist) get auxiliary reinforcement pillars
///
/// Connection pattern: alternating horizontal bridges and diagonal zigzag struts,
/// collision-checked via BVH beam-cast before placement.
/// </summary>
public static class InterconnectBuilder
{
    public sealed class Interconnection
    {
        public required int PillarA { get; init; }
        public required int PillarB { get; init; }
        public required Vector3 PointA { get; init; }
        public required Vector3 PointB { get; init; }
        public required float Radius { get; init; }
        /// <summary>"horizontal" or "diagonal"</summary>
        public required string Type { get; init; }
    }

    public sealed class InterconnectConfig
    {
        /// <summary>Max XY distance between pillars to consider connecting.</summary>
        public float MaxConnectionDistMm { get; init; } = 10f;
        /// <summary>Pillar height above which at least 1 connection is required.</summary>
        public float MaxSoloHeightMm { get; init; } = 10f;
        /// <summary>Pillar height above which at least 2 connections are required.</summary>
        public float MaxDualHeightMm { get; init; } = 25f;
        /// <summary>Z interval between cross-connections.</summary>
        public float ConnectionIntervalMm { get; init; } = 5f;
        /// <summary>Radius of cross-connection struts.</summary>
        public float StrutRadiusMm { get; init; } = 0.3f;
        /// <summary>Max connections per pillar pair.</summary>
        public int MaxConnectionsPerPair { get; init; } = 6;
        /// <summary>Reinforcement mode.</summary>
        public ReinforcementMode Mode { get; init; } = ReinforcementMode.Pairwise;
        /// <summary>Only brace pillars taller than this (mm).</summary>
        public float ReinforcementStartHeightMm { get; init; } = 5f;
    }

    /// <summary>
    /// Build interconnections between pillars.
    /// </summary>
    /// <param name="pillarBases">Base positions (X, Y) of each pillar.</param>
    /// <param name="pillarTops">Top positions (Z) of each pillar (junction point Z).</param>
    /// <param name="pillarRadii">Radius of each pillar at its base.</param>
    /// <param name="bvh">Mesh BVH for collision checking.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="pillarPaths">Optional: full waypoint paths per pillar for centerline interpolation.</param>
    public static List<Interconnection> Build(
        List<Vector3> pillarBases,
        List<float> pillarTops,
        List<float> pillarRadii,
        AabbBvh? bvh,
        InterconnectConfig config,
        List<List<PillarRouter.Waypoint>>? pillarPaths = null)
    {
        var connections = new List<Interconnection>();
        int n = pillarBases.Count;
        if (n < 2) return connections;

        // Track connection count per pillar for structural requirements
        var connectionCount = new int[n];

        // Find neighbor pairs sorted by distance
        var pairs = new List<(int a, int b, float dist)>();
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            float dist = Vector2.Distance(
                new Vector2(pillarBases[i].X, pillarBases[i].Y),
                new Vector2(pillarBases[j].X, pillarBases[j].Y));
            if (dist > 0.5f && dist <= config.MaxConnectionDistMm)
                pairs.Add((i, j, dist));
        }
        pairs.Sort((a, b) => a.dist.CompareTo(b.dist));

        // Total brace cap: prevent explosion on dense support arrays
        int maxTotalBraces = Math.Max(n * 3, 50);

        // Build connections: nearest pairs first
        foreach (var (a, b, dist) in pairs)
        {
            if (connections.Count >= maxTotalBraces) break;

            // Z range where both pillars overlap
            float minZ = Math.Max(pillarBases[a].Z, pillarBases[b].Z) + 0.5f;
            float maxZ = Math.Min(pillarTops[a], pillarTops[b]) - 0.5f;

            // If overlap is too small for interval-based placement, still place ONE brace at midpoint
            if (maxZ <= minZ)
            {
                float midZ = (pillarTops[a] + pillarTops[b]) / 2f;
                if (midZ > 1f && connections.Count < maxTotalBraces)
                {
                    var (ptA2, ptB2, _, _) = ComputeBraceEndpoints(
                        a, midZ, b, midZ, pillarBases, pillarRadii, pillarPaths);
                    if (IsStrutClear(ptA2, ptB2, config.StrutRadiusMm, bvh))
                    {
                        connections.Add(new Interconnection { PillarA = a, PillarB = b, PointA = ptA2, PointB = ptB2, Radius = config.StrutRadiusMm, Type = "horizontal" });
                        connectionCount[a]++; connectionCount[b]++;
                    }
                }
                continue;
            }

            // Limit per-pair based on overlap height (1 per interval, max 4)
            float overlap = maxZ - minZ;
            int pairMax = Math.Min(config.MaxConnectionsPerPair,
                Math.Max(1, (int)(overlap / config.ConnectionIntervalMm)));
            int pairConnections = 0;
            bool alternate = false;

            for (float z = minZ + config.ConnectionIntervalMm; z < maxZ && pairConnections < pairMax;
                 z += config.ConnectionIntervalMm)
            {
                float zB = alternate
                    ? Math.Min(z + config.ConnectionIntervalMm * 0.4f, maxZ)
                    : z;

                var (ptA, ptB, jrA, jrB) = ComputeBraceEndpoints(
                    a, z, b, zB, pillarBases, pillarRadii, pillarPaths);

                if (IsStrutClear(ptA, ptB, config.StrutRadiusMm, bvh))
                {
                    connections.Add(new Interconnection
                    {
                        PillarA = a, PillarB = b,
                        PointA = ptA, PointB = ptB,
                        Radius = config.StrutRadiusMm,
                        Type = alternate ? "diagonal" : "horizontal",
                    });
                    pairConnections++;
                    connectionCount[a]++;
                    connectionCount[b]++;
                }

                alternate = !alternate;
            }
        }

        // Enforce structural requirements: tall pillars must have connections
        int _gridCount = connections.Count; // braces placed by the pair/interval loop
        for (int i = 0; i < n; i++)
        {
            float height = pillarTops[i] - pillarBases[i].Z;
            int required = height > config.MaxDualHeightMm ? 2 :
                          height > config.MaxSoloHeightMm ? 1 : 0;

            if (connectionCount[i] < required)
            {
                // Find nearest unconnected pillar and force a connection
                float bestDist = float.MaxValue;
                int bestJ = -1;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    float d = Vector2.Distance(
                        new Vector2(pillarBases[i].X, pillarBases[i].Y),
                        new Vector2(pillarBases[j].X, pillarBases[j].Y));
                    if (d < bestDist && d > 0.5f)
                    {
                        bestDist = d;
                        bestJ = j;
                    }
                }

                if (bestJ >= 0 && bestDist < config.MaxConnectionDistMm * 2f) // extend range
                {
                    float midZ = (Math.Max(pillarBases[i].Z, pillarBases[bestJ].Z) +
                                  Math.Min(pillarTops[i], pillarTops[bestJ])) / 2f;
                    var (ptA, ptB, _, _) = ComputeBraceEndpoints(
                        i, midZ, bestJ, midZ, pillarBases, pillarRadii, pillarPaths);

                    if (IsStrutClear(ptA, ptB, config.StrutRadiusMm, bvh))
                    {
                        connections.Add(new Interconnection
                        {
                            PillarA = i, PillarB = bestJ,
                            PointA = ptA, PointB = ptB,
                            Radius = config.StrutRadiusMm,
                            Type = "horizontal",
                        });
                        connectionCount[i]++;
                        connectionCount[bestJ]++;
                    }
                }
            }
        }

        // Diagnostics: count grid-driven vs need-driven braces
        int needDriven = connections.Count - _gridCount;

        // Brace distribution
        var bracesPerPair = new Dictionary<(int, int), int>();
        foreach (var c in connections)
        {
            var key = (Math.Min(c.PillarA, c.PillarB), Math.Max(c.PillarA, c.PillarB));
            bracesPerPair[key] = bracesPerPair.GetValueOrDefault(key) + 1;
        }
        var distrib = bracesPerPair.Values.GroupBy(v => v).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}braces×{g.Count()}pairs").ToList();

        Serilog.Log.Information("DIAG-BRACE: total={Total} gridDriven={Grid} needDriven={Need} pairs={Pairs} distrib=[{Distrib}] interval={Interval}mm",
            connections.Count, _gridCount, needDriven, bracesPerPair.Count, string.Join(", ", distrib), config.ConnectionIntervalMm);

        return connections;
    }

    /// <summary>
    /// Triangulated bracing — Delaunay edges only, closed triangles for rigidity.
    /// Fewer braces than pairwise, but geometrically rigid (triangles can't shear).
    /// </summary>
    public static List<Interconnection> BuildTriangulated(
        List<Vector3> pillarBases,
        List<float> pillarTops,
        List<float> pillarRadii,
        AabbBvh? bvh,
        InterconnectConfig config,
        List<List<PillarRouter.Waypoint>>? pillarPaths = null)
    {
        var connections = new List<Interconnection>();
        int n = pillarBases.Count;
        if (n < 2) return connections;

        // Filter to tall pillars only
        var tallIndices = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float height = pillarTops[i] - pillarBases[i].Z;
            if (height >= config.ReinforcementStartHeightMm)
                tallIndices.Add(i);
        }
        if (tallIndices.Count < 2) return connections;

        // Build Delaunay triangulation over XY positions of tall pillars.
        // Using incremental Bowyer-Watson algorithm.
        var points2D = tallIndices.Select(i => new Vector2(pillarBases[i].X, pillarBases[i].Y)).ToList();
        var triangles = DelaunayTriangulate(points2D);

        // Extract unique edges from triangulation
        var edges = new HashSet<(int, int)>();
        foreach (var (a, b, c) in triangles)
        {
            edges.Add((Math.Min(a, b), Math.Max(a, b)));
            edges.Add((Math.Min(b, c), Math.Max(b, c)));
            edges.Add((Math.Min(a, c), Math.Max(a, c)));
        }

        // For Global mode, use all edges. For Triangular mode, filter to edges within MaxConnectionDist.
        var filteredEdges = config.Mode == ReinforcementMode.Global
            ? edges.ToList()
            : edges.Where(e =>
            {
                int ri = tallIndices[e.Item1], rj = tallIndices[e.Item2];
                float dist = Vector2.Distance(
                    new Vector2(pillarBases[ri].X, pillarBases[ri].Y),
                    new Vector2(pillarBases[rj].X, pillarBases[rj].Y));
                return dist <= config.MaxConnectionDistMm;
            }).ToList();

        int maxTotalBraces = Math.Max(n * 3, 50);

        // Place braces along each Delaunay edge at Z intervals
        foreach (var (li, lj) in filteredEdges)
        {
            if (connections.Count >= maxTotalBraces) break;

            int ri = tallIndices[li], rj = tallIndices[lj];
            float minZ = Math.Max(pillarBases[ri].Z, pillarBases[rj].Z) + 0.5f;
            float maxZ = Math.Min(pillarTops[ri], pillarTops[rj]) - 0.5f;

            if (maxZ <= minZ)
            {
                // Try single brace at midpoint
                float midZ = (pillarTops[ri] + pillarTops[rj]) / 2f;
                if (midZ > 1f)
                {
                    var (ptA, ptB, _, _) = ComputeBraceEndpoints(
                        ri, midZ, rj, midZ, pillarBases, pillarRadii, pillarPaths);
                    if (IsStrutClear(ptA, ptB, config.StrutRadiusMm, bvh))
                    {
                        connections.Add(new Interconnection { PillarA = ri, PillarB = rj, PointA = ptA, PointB = ptB, Radius = config.StrutRadiusMm, Type = "horizontal" });
                    }
                }
                continue;
            }

            float overlap = maxZ - minZ;
            int pairMax = Math.Min(config.MaxConnectionsPerPair, Math.Max(1, (int)(overlap / config.ConnectionIntervalMm)));
            int placed = 0;

            for (float z = minZ + config.ConnectionIntervalMm; z < maxZ && placed < pairMax; z += config.ConnectionIntervalMm)
            {
                var (ptA, ptB, _, _) = ComputeBraceEndpoints(
                    ri, z, rj, z, pillarBases, pillarRadii, pillarPaths);

                if (IsStrutClear(ptA, ptB, config.StrutRadiusMm, bvh))
                {
                    connections.Add(new Interconnection { PillarA = ri, PillarB = rj, PointA = ptA, PointB = ptB, Radius = config.StrutRadiusMm, Type = "horizontal" });
                    placed++;
                }
            }
        }

        Serilog.Log.Information("TRUSS: {TallPillars} tall pillars, {Triangles} Delaunay triangles, {Edges} edges, {Braces} braces placed",
            tallIndices.Count, triangles.Count, filteredEdges.Count, connections.Count);

        return connections;
    }

    /// <summary>
    /// Get the pillar centerline position and radius at a given Z height.
    /// If pillar paths are available, interpolates between waypoints.
    /// Otherwise falls back to the base XY position (vertical assumption).
    /// </summary>
    private static (Vector3 center, float radius) GetPillarCenterAndRadiusAtZ(int pillarIdx, float z,
        List<Vector3> pillarBases, List<float> pillarRadii, List<List<PillarRouter.Waypoint>>? pillarPaths)
    {
        float defaultR = pillarIdx < pillarRadii.Count ? pillarRadii[pillarIdx] : 0.5f;

        if (pillarPaths == null || pillarIdx >= pillarPaths.Count || pillarPaths[pillarIdx].Count < 2)
            return (new Vector3(pillarBases[pillarIdx].X, pillarBases[pillarIdx].Y, z), defaultR);

        var path = pillarPaths[pillarIdx];
        for (int i = 0; i < path.Count - 1; i++)
        {
            float z1 = path[i].Position.Z;
            float z2 = path[i + 1].Position.Z;
            float zHi = Math.Max(z1, z2);
            float zLo = Math.Min(z1, z2);

            if (z >= zLo - 0.01f && z <= zHi + 0.01f)
            {
                float range = z1 - z2;
                if (MathF.Abs(range) < 0.001f)
                    return (new Vector3(path[i].Position.X, path[i].Position.Y, z), path[i].Radius);

                float t = (z1 - z) / range;
                t = Math.Clamp(t, 0f, 1f);
                float x = path[i].Position.X + (path[i + 1].Position.X - path[i].Position.X) * t;
                float y = path[i].Position.Y + (path[i + 1].Position.Y - path[i].Position.Y) * t;
                float r = path[i].Radius + (path[i + 1].Radius - path[i].Radius) * t;
                return (new Vector3(x, y, z), r);
            }
        }

        if (z > path[0].Position.Z)
            return (new Vector3(path[0].Position.X, path[0].Position.Y, z), path[0].Radius);
        var last = path[^1];
        return (new Vector3(last.Position.X, last.Position.Y, z), last.Radius);
    }

    /// <summary>
    /// Compute surface-snapped brace endpoints: each end sits on the pillar's outer surface
    /// (not at the centerline), so the brace visually connects flush to the pillar wall.
    /// Also returns a junction sphere radius for smooth blending at each connection.
    /// </summary>
    private static (Vector3 ptA, Vector3 ptB, float junctionRadiusA, float junctionRadiusB)
        ComputeBraceEndpoints(int a, float zA, int b, float zB,
            List<Vector3> pillarBases, List<float> pillarRadii,
            List<List<PillarRouter.Waypoint>>? pillarPaths)
    {
        var (centerA, radiusA) = GetPillarCenterAndRadiusAtZ(a, zA, pillarBases, pillarRadii, pillarPaths);
        var (centerB, radiusB) = GetPillarCenterAndRadiusAtZ(b, zB, pillarBases, pillarRadii, pillarPaths);

        // Direction from A to B (in XY, ignoring Z for surface push)
        float dx = centerB.X - centerA.X;
        float dy = centerB.Y - centerA.Y;
        float xyDist = MathF.Sqrt(dx * dx + dy * dy);

        if (xyDist < 0.01f)
        {
            // Pillars are coaxial — can't push outward, use centers
            return (centerA, centerB, radiusA, radiusB);
        }

        float nx = dx / xyDist;
        float ny = dy / xyDist;

        // Push A outward toward B by A's radius, push B outward toward A by B's radius
        var ptA = new Vector3(centerA.X + nx * radiusA, centerA.Y + ny * radiusA, zA);
        var ptB = new Vector3(centerB.X - nx * radiusB, centerB.Y - ny * radiusB, zB);

        return (ptA, ptB, radiusA, radiusB);
    }

    private static bool IsStrutClear(Vector3 a, Vector3 b, float radius, AabbBvh? bvh)
    {
        if (bvh == null) return true;
        var dir = Vector3.Normalize(b - a);
        float len = Vector3.Distance(a, b);
        if (len < 0.1f) return true;
        float clearance = bvh.BeamCast(a, dir, radius, 8, len);
        return clearance >= len - 0.1f;
    }

    // ── Bowyer-Watson Delaunay triangulation ──────────────────────────────

    private static List<(int a, int b, int c)> DelaunayTriangulate(List<Vector2> points)
    {
        if (points.Count < 3) return new();

        // Super-triangle enclosing all points
        float minX = points.Min(p => p.X) - 1;
        float minY = points.Min(p => p.Y) - 1;
        float maxX = points.Max(p => p.X) + 1;
        float maxY = points.Max(p => p.Y) + 1;
        float dx = maxX - minX, dy = maxY - minY;
        float dmax = Math.Max(dx, dy) * 10;

        var allPts = new List<Vector2>(points);
        int s0 = allPts.Count; allPts.Add(new Vector2(minX - dmax, minY - dmax));
        int s1 = allPts.Count; allPts.Add(new Vector2(minX + dmax * 3, minY - dmax));
        int s2 = allPts.Count; allPts.Add(new Vector2(minX, minY + dmax * 3));

        var tris = new List<(int a, int b, int c)> { (s0, s1, s2) };

        for (int pi = 0; pi < points.Count; pi++)
        {
            var p = allPts[pi];
            var badTris = new List<int>();

            for (int ti = 0; ti < tris.Count; ti++)
            {
                var (a, b, c) = tris[ti];
                if (InCircumcircle(p, allPts[a], allPts[b], allPts[c]))
                    badTris.Add(ti);
            }

            // Find boundary polygon of the hole
            var polygon = new List<(int, int)>();
            foreach (int ti in badTris)
            {
                var (a, b, c) = tris[ti];
                var edges3 = new[] { (a, b), (b, c), (c, a) };
                foreach (var edge in edges3)
                {
                    bool shared = false;
                    foreach (int tj in badTris)
                    {
                        if (tj == ti) continue;
                        var (ta, tb, tc) = tris[tj];
                        var otherEdges = new[] { (ta, tb), (tb, tc), (tc, ta) };
                        foreach (var oe in otherEdges)
                        {
                            if ((edge.Item1 == oe.Item1 && edge.Item2 == oe.Item2) ||
                                (edge.Item1 == oe.Item2 && edge.Item2 == oe.Item1))
                            { shared = true; break; }
                        }
                        if (shared) break;
                    }
                    if (!shared) polygon.Add(edge);
                }
            }

            // Remove bad triangles (reverse order to keep indices valid)
            foreach (int ti in badTris.OrderByDescending(x => x))
                tris.RemoveAt(ti);

            // Re-triangulate hole with new point
            foreach (var (ea, eb) in polygon)
                tris.Add((ea, eb, pi));
        }

        // Remove triangles that share a vertex with the super-triangle
        tris.RemoveAll(t => t.a >= points.Count || t.b >= points.Count || t.c >= points.Count);

        return tris;
    }

    private static bool InCircumcircle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float ax = a.X - p.X, ay = a.Y - p.Y;
        float bx = b.X - p.X, by = b.Y - p.Y;
        float cx = c.X - p.X, cy = c.Y - p.Y;

        float det = ax * (by * (cx * cx + cy * cy) - cy * (bx * bx + by * by))
                  - ay * (bx * (cx * cx + cy * cy) - cx * (bx * bx + by * by))
                  + (ax * ax + ay * ay) * (bx * cy - by * cx);

        // Ensure counter-clockwise orientation
        float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return cross > 0 ? det > 0 : det < 0;
    }
}
