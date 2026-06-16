using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Merges nearby support TIPS into forked supports — one trunk with N short struts
/// fanning to multiple contact points.
///
/// Algorithm (region-growing, not greedy first-pair):
/// 1. Build spatial grid over valid pinhead CONTACT points.
/// 2. For each unused tip (seed), region-grow: add nearest unused tip within
///    ForkClusterRadiusMm, repeat until MaxTipsPerFork reached or no more neighbors.
/// 3. For each cluster of 2+, compute fork node Z from critical angle constraint.
/// 4. Collision-check each strut; reject entire cluster if any strut collides.
/// </summary>
public static class ForkBuilder
{
    public sealed record ForkConfig
    {
        public float ForkClusterRadiusMm { get; init; } = 4f;
        public int MaxTipsPerFork { get; init; } = 4;
        public float CriticalAngleDeg { get; init; } = 45f;
    }

    public sealed class ForkResult
    {
        public required int[] ClusterAssignment { get; init; }
        public required Dictionary<int, Vector3> ForkNodes { get; init; }
        public required Dictionary<int, List<int>> ClusterMembers { get; init; }
        public required float MaxStrutAngleDeg { get; init; }
        public required int CollisionRejections { get; init; }
        /// <summary>Histogram: index = tips-per-fork (2,3,4,...), value = count of clusters with that many tips.</summary>
        public required int[] TipsPerForkHistogram { get; init; }
    }

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
        var histogram = new int[config.MaxTipsPerFork + 1]; // index = tip count

        float maxAngleRad = config.CriticalAngleDeg * MathF.PI / 180f;
        float maxDist = config.ForkClusterRadiusMm;
        float maxDist2 = maxDist * maxDist;

        // Collect valid pinheads — use CONTACT points for clustering (not junctions)
        var validIndices = new List<int>();
        var contactPoints = new Vector3[n]; // XY of contact point per pinhead
        for (int i = 0; i < n; i++)
        {
            if (pinheads[i].pinhead.IsValid)
            {
                validIndices.Add(i);
                contactPoints[i] = pinheads[i].pinhead.ContactPoint;
            }
        }

        // Build spatial grid over contact points for fast neighbor queries
        var grid = new SpatialGrid<int>(maxDist);
        foreach (int i in validIndices)
            grid.Insert(contactPoints[i], i);

        var used = new bool[n];
        int clusterId = 0;

        // Sort by Z descending so we seed from the highest tips (best fork candidates)
        var sortedValid = validIndices.OrderByDescending(i => contactPoints[i].Z).ToList();

        foreach (int seed in sortedValid)
        {
            if (used[seed]) continue;

            // Region-grow: start with seed, add nearest unused neighbor within radius
            var cluster = new List<int> { seed };
            var clusterCentroid = new Vector2(contactPoints[seed].X, contactPoints[seed].Y);

            while (cluster.Count < config.MaxTipsPerFork)
            {
                // Find nearest unused tip within maxDist of cluster centroid
                int bestIdx = -1;
                float bestDist = float.MaxValue;

                var neighbors = grid.FindInRadius(
                    new Vector3(clusterCentroid.X, clusterCentroid.Y, contactPoints[seed].Z),
                    maxDist * 1.5f); // search slightly wider, filter by actual distance

                foreach (var (idx, _) in neighbors)
                {
                    if (used[idx] || cluster.Contains(idx)) continue;
                    // Distance from this tip to the cluster centroid
                    float dx = contactPoints[idx].X - clusterCentroid.X;
                    float dy = contactPoints[idx].Y - clusterCentroid.Y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 <= maxDist2 && d2 < bestDist)
                    {
                        bestDist = d2;
                        bestIdx = idx;
                    }
                }

                if (bestIdx < 0) break; // no more neighbors within radius

                cluster.Add(bestIdx);
                // Update centroid
                float sumX = 0, sumY = 0;
                foreach (int ci in cluster) { sumX += contactPoints[ci].X; sumY += contactPoints[ci].Y; }
                clusterCentroid = new Vector2(sumX / cluster.Count, sumY / cluster.Count);
            }

            if (cluster.Count < 2) continue;

            // Compute fork node position
            float cx = 0, cy = 0;
            float minJunctionZ = float.MaxValue;
            foreach (int idx in cluster)
            {
                var jp = pinheads[idx].pinhead.JunctionPoint;
                cx += jp.X; cy += jp.Y;
                if (jp.Z < minJunctionZ) minJunctionZ = jp.Z;
            }
            cx /= cluster.Count; cy /= cluster.Count;

            // Fork Z: highest point where all struts stay within critical angle
            // requiredDrop = maxXYDist / tan(criticalAngle)
            float tanMax = MathF.Tan(maxAngleRad);
            float requiredDrop = 0;
            foreach (int idx in cluster)
            {
                var jp = pinheads[idx].pinhead.JunctionPoint;
                float xyDist = MathF.Sqrt((jp.X - cx) * (jp.X - cx) + (jp.Y - cy) * (jp.Y - cy));
                float drop = tanMax > 1e-6f ? xyDist / tanMax : 0;
                if (drop > requiredDrop) requiredDrop = drop;
            }

            float forkZ = minJunctionZ - Math.Max(requiredDrop, 0.5f);
            if (forkZ < 1.0f) continue;

            var forkNode = new Vector3(cx, cy, forkZ);

            // Trunk radius = sqrt(sum of tip radii squared)
            float sumR2 = 0;
            foreach (int idx in cluster)
                sumR2 += pinheads[idx].pinhead.BackRadius * pinheads[idx].pinhead.BackRadius;
            float trunkRadius = MathF.Sqrt(sumR2);

            // Validate strut angles + collisions
            bool allValid = true;
            float clusterMaxAngle = 0;
            foreach (int idx in cluster)
            {
                var jp = pinheads[idx].pinhead.JunctionPoint;
                var strutDir = jp - forkNode;
                float strutLen = strutDir.Length();
                if (strutLen < 0.1f) continue;

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

                var dir = Vector3.Normalize(strutDir);
                float clearance = bvh.BeamCast(forkNode, dir, trunkRadius * 0.5f, 8, strutLen);
                if (clearance < strutLen - 0.1f)
                {
                    collisionRejections++;
                    allValid = false;
                    break;
                }
            }

            if (!allValid) continue;

            // Accept this fork
            foreach (int idx in cluster)
            {
                assignment[idx] = clusterId;
                used[idx] = true;
            }
            forkNodes[clusterId] = forkNode;
            clusterMembers[clusterId] = cluster;
            if (clusterMaxAngle > maxAngle)
                maxAngle = clusterMaxAngle;
            if (cluster.Count <= config.MaxTipsPerFork)
                histogram[cluster.Count]++;
            clusterId++;
        }

        // Log histogram
        var histParts = new List<string>();
        for (int i = 2; i < histogram.Length; i++)
            if (histogram[i] > 0) histParts.Add($"{i}-tip:{histogram[i]}");
        if (histParts.Count > 0)
            Serilog.Log.Information("V2 ForkBuilder histogram: {Hist}", string.Join(" ", histParts));

        return new ForkResult
        {
            ClusterAssignment = assignment,
            ForkNodes = forkNodes,
            ClusterMembers = clusterMembers,
            MaxStrutAngleDeg = maxAngle,
            CollisionRejections = collisionRejections,
            TipsPerForkHistogram = histogram,
        };
    }
}
