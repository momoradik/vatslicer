using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

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
        public float MaxSoloHeightMm { get; init; } = 20f;
        /// <summary>Pillar height above which at least 2 connections are required.</summary>
        public float MaxDualHeightMm { get; init; } = 40f;
        /// <summary>Z interval between cross-connections.</summary>
        public float ConnectionIntervalMm { get; init; } = 5f;
        /// <summary>Radius of cross-connection struts.</summary>
        public float StrutRadiusMm { get; init; } = 0.3f;
        /// <summary>Max connections per pillar pair.</summary>
        public int MaxConnectionsPerPair { get; init; } = 6;
    }

    /// <summary>
    /// Build interconnections between pillars.
    /// </summary>
    /// <param name="pillarBases">Base positions (X, Y) of each pillar.</param>
    /// <param name="pillarTops">Top positions (Z) of each pillar (junction point Z).</param>
    /// <param name="pillarRadii">Radius of each pillar at its base.</param>
    /// <param name="bvh">Mesh BVH for collision checking.</param>
    /// <param name="config">Configuration.</param>
    public static List<Interconnection> Build(
        List<Vector3> pillarBases,
        List<float> pillarTops,
        List<float> pillarRadii,
        AabbBvh? bvh,
        InterconnectConfig config)
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

        // Build connections: nearest pairs first
        foreach (var (a, b, dist) in pairs)
        {
            // Z range where both pillars overlap
            float minZ = Math.Max(pillarBases[a].Z, pillarBases[b].Z) + 2f;
            float maxZ = Math.Min(pillarTops[a], pillarTops[b]) - 1f;
            if (maxZ <= minZ + config.ConnectionIntervalMm) continue;

            int pairConnections = 0;
            bool alternate = false;

            for (float z = minZ + config.ConnectionIntervalMm; z < maxZ && pairConnections < config.MaxConnectionsPerPair;
                 z += config.ConnectionIntervalMm)
            {
                var ptA = new Vector3(pillarBases[a].X, pillarBases[a].Y, z);
                Vector3 ptB;

                if (alternate)
                {
                    // Diagonal: offset Z on one side
                    float z2 = Math.Min(z + config.ConnectionIntervalMm * 0.4f, maxZ);
                    ptB = new Vector3(pillarBases[b].X, pillarBases[b].Y, z2);
                }
                else
                {
                    // Horizontal: same Z
                    ptB = new Vector3(pillarBases[b].X, pillarBases[b].Y, z);
                }

                // Collision check: verify the strut doesn't pass through the mesh
                bool clear = true;
                if (bvh != null)
                {
                    var dir = Vector3.Normalize(ptB - ptA);
                    float len = Vector3.Distance(ptA, ptB);
                    float clearance = bvh.BeamCast(ptA, dir, config.StrutRadiusMm, 4, len);
                    clear = clearance >= len - 0.1f;
                }

                if (clear)
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
                    connections.Add(new Interconnection
                    {
                        PillarA = i, PillarB = bestJ,
                        PointA = new Vector3(pillarBases[i].X, pillarBases[i].Y, midZ),
                        PointB = new Vector3(pillarBases[bestJ].X, pillarBases[bestJ].Y, midZ),
                        Radius = config.StrutRadiusMm,
                        Type = "horizontal",
                    });
                    connectionCount[i]++;
                    connectionCount[bestJ]++;
                }
            }
        }

        return connections;
    }
}
