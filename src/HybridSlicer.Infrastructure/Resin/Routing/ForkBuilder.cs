using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Merges nearby support TIPS into forked supports — one trunk with N short struts
/// fanning to multiple contact points. The inverse of tree merging: forking merges
/// tips at the top, tree merging merges trunks at the bottom.
///
/// Algorithm:
/// 1. Cluster valid pinheads by XY proximity (ForkClusterRadiusMm).
/// 2. For each cluster of 2..MaxTipsPerFork, compute a fork node: XY = centroid,
///    Z = highest point where ALL struts stay within the self-supporting cone angle.
/// 3. Collision-check each strut against the mesh.
/// 4. Replace N individual pinhead→route entries with N tip-struts + 1 shared junction.
///    Tip positions DO NOT MOVE.
/// </summary>
public static class ForkBuilder
{
    public sealed record ForkConfig
    {
        /// <summary>Max XY distance between tips to consider forking (mm).</summary>
        public float ForkClusterRadiusMm { get; init; } = 4f;
        /// <summary>Max number of tips per fork (2..N).</summary>
        public int MaxTipsPerFork { get; init; } = 4;
        /// <summary>Critical self-supporting angle from vertical (degrees).</summary>
        public float CriticalAngleDeg { get; init; } = 45f;
    }

    public sealed class ForkResult
    {
        /// <summary>Cluster index for each pinhead (-1 = not forked, 0+ = cluster id).</summary>
        public required int[] ClusterAssignment { get; init; }
        /// <summary>For each cluster, the fork node position.</summary>
        public required Dictionary<int, Vector3> ForkNodes { get; init; }
        /// <summary>For each cluster, the tip indices in the original pinhead list.</summary>
        public required Dictionary<int, List<int>> ClusterMembers { get; init; }
        /// <summary>Max strut angle from vertical across all forks (degrees).</summary>
        public required float MaxStrutAngleDeg { get; init; }
        /// <summary>Number of struts rejected by collision.</summary>
        public required int CollisionRejections { get; init; }
    }

    /// <summary>
    /// Analyze pinheads and find feasible fork clusters.
    /// </summary>
    public static ForkResult FindForks(
        List<(string id, PinheadOptimizer.Pinhead pinhead)> pinheads,
        AabbBvh bvh,
        ForkConfig config)
    {
        int n = pinheads.Count;
        var assignment = new int[n];
        Array.Fill(assignment, -1);
        var forkNodes = new Dictionary<int, Vector3>();
        var clusterMembers = new Dictionary<int, List<int>>();
        float maxAngle = 0;
        int collisionRejections = 0;

        float maxAngleRad = config.CriticalAngleDeg * MathF.PI / 180f;
        float maxDist2 = config.ForkClusterRadiusMm * config.ForkClusterRadiusMm;

        // Collect valid pinheads with their junction points
        var validIndices = new List<int>();
        for (int i = 0; i < n; i++)
            if (pinheads[i].pinhead.IsValid)
                validIndices.Add(i);

        // Greedy nearest-neighbor clustering
        var used = new bool[n];
        int clusterId = 0;

        for (int vi = 0; vi < validIndices.Count; vi++)
        {
            int i = validIndices[vi];
            if (used[i]) continue;

            var jp_i = pinheads[i].pinhead.JunctionPoint;

            // Find nearby tips within cluster radius
            var candidates = new List<int> { i };
            for (int vj = vi + 1; vj < validIndices.Count; vj++)
            {
                int j = validIndices[vj];
                if (used[j]) continue;
                if (candidates.Count >= config.MaxTipsPerFork) break;

                var jp_j = pinheads[j].pinhead.JunctionPoint;
                float dx = jp_i.X - jp_j.X;
                float dy = jp_i.Y - jp_j.Y;
                if (dx * dx + dy * dy <= maxDist2)
                    candidates.Add(j);
            }

            if (candidates.Count < 2) continue; // need at least 2 tips to fork

            // Compute fork node: XY = centroid of junction points
            float cx = 0, cy = 0;
            float minJunctionZ = float.MaxValue;
            foreach (int idx in candidates)
            {
                var jp = pinheads[idx].pinhead.JunctionPoint;
                cx += jp.X;
                cy += jp.Y;
                if (jp.Z < minJunctionZ) minJunctionZ = jp.Z;
            }
            cx /= candidates.Count;
            cy /= candidates.Count;

            // Compute fork node Z: highest Z where all struts stay within critical angle.
            // For each tip, the strut from fork node to junction must have:
            //   angle_from_vertical = atan2(xy_dist, z_drop) ≤ maxAngleRad
            // So z_drop ≥ xy_dist / tan(maxAngleRad)
            float tanMax = MathF.Tan(maxAngleRad);
            float requiredDrop = 0;
            foreach (int idx in candidates)
            {
                var jp = pinheads[idx].pinhead.JunctionPoint;
                float xyDist = MathF.Sqrt((jp.X - cx) * (jp.X - cx) + (jp.Y - cy) * (jp.Y - cy));
                float drop = tanMax > 1e-6f ? xyDist / tanMax : 0;
                if (drop > requiredDrop) requiredDrop = drop;
            }

            float forkZ = minJunctionZ - requiredDrop;
            if (forkZ < 1.0f) continue; // fork node too close to plate

            var forkNode = new Vector3(cx, cy, forkZ);

            // Validate: check each strut angle and collision
            bool allValid = true;
            float clusterMaxAngle = 0;
            foreach (int idx in candidates)
            {
                var jp = pinheads[idx].pinhead.JunctionPoint;
                var strutDir = jp - forkNode;
                float strutLen = strutDir.Length();
                if (strutLen < 0.1f) continue;

                // Angle from vertical (+Z)
                float cosAngle = strutDir.Z / strutLen;
                float angleDeg = MathF.Acos(MathF.Abs(cosAngle)) * 180f / MathF.PI;
                // The strut goes UP from fork to junction, so angle from +Z
                float angleFromVertical = MathF.Atan2(
                    MathF.Sqrt(strutDir.X * strutDir.X + strutDir.Y * strutDir.Y),
                    MathF.Abs(strutDir.Z)) * 180f / MathF.PI;

                if (angleFromVertical > config.CriticalAngleDeg + 0.1f)
                {
                    allValid = false;
                    break;
                }
                if (angleFromVertical > clusterMaxAngle)
                    clusterMaxAngle = angleFromVertical;

                // Collision check: beam-cast from fork node to junction
                var dir = Vector3.Normalize(strutDir);
                float clearance = bvh.BeamCast(forkNode, dir, 0.3f, 8, strutLen);
                if (clearance < strutLen - 0.1f)
                {
                    collisionRejections++;
                    allValid = false;
                    break;
                }
            }

            if (!allValid) continue;

            // Accept this fork
            foreach (int idx in candidates)
            {
                assignment[idx] = clusterId;
                used[idx] = true;
            }
            forkNodes[clusterId] = forkNode;
            clusterMembers[clusterId] = candidates;
            if (clusterMaxAngle > maxAngle)
                maxAngle = clusterMaxAngle;
            clusterId++;
        }

        return new ForkResult
        {
            ClusterAssignment = assignment,
            ForkNodes = forkNodes,
            ClusterMembers = clusterMembers,
            MaxStrutAngleDeg = maxAngle,
            CollisionRejections = collisionRejections,
        };
    }
}
