using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Merges nearby support pillars into tree-like structures with shared trunks.
///
/// PrusaSlicer-style tree supports reduce material usage and improve print stability
/// by combining multiple thin pillars into thicker shared trunks:
///
/// Algorithm:
/// 1. Build a spatial grid of pillar base positions
/// 2. Group pillars by proximity using union-find clustering
/// 3. For each group of 2+ pillars:
///    a. Compute merge point (centroid of bases, at minHeight * mergeHeightRatio)
///    b. Replace individual pillars below merge point with branches to the merge point
///    c. Create a single trunk from the merge point down to the build plate
///    d. Trunk radius = sqrt(sum of branch radii^2) for cross-section area equivalence
/// 4. Return modified route list with shared trunks
///
/// Benefits:
/// - Reduced resin consumption (shared trunk vs. N individual pillars)
/// - Increased structural rigidity (thicker trunk resists buckling)
/// - Better bed adhesion (single larger base footprint)
/// - Fewer isolated contact points on the build plate
/// </summary>
public static class TreeSupportBuilder
{
    /// <summary>
    /// Configuration for tree support merging.
    /// </summary>
    public sealed record TreeConfig
    {
        /// <summary>
        /// Maximum XY distance between pillar bases to consider merging (mm).
        /// Pillars whose bases are farther apart than this will not be merged.
        /// </summary>
        public float MaxMergeDistMm { get; init; } = 15f;

        /// <summary>
        /// Merge point height as a ratio of the minimum pillar height in the group.
        /// 0.3 = merge at 30% of the shortest pillar's height from the base.
        /// Lower values produce longer trunks (more material saving).
        /// </summary>
        public float MinMergeHeightRatio { get; init; } = 0.3f;

        /// <summary>
        /// Trunk radius multiplier relative to the largest branch radius.
        /// Applied after area-equivalence calculation as a minimum.
        /// 1.5 = trunk is at least 1.5x the largest branch radius.
        /// </summary>
        public float TrunkRadiusScale { get; init; } = 1.5f;

        /// <summary>
        /// Maximum angle (degrees) from vertical for branches connecting to the trunk.
        /// Branches exceeding this angle will not be merged into the tree.
        /// </summary>
        public float BranchAngleMaxDeg { get; init; } = 35f;
    }

    /// <summary>
    /// Analyze existing pillar routes and merge nearby pillars into tree structures.
    /// Pillars that cannot be merged (isolated or too far apart) are returned unmodified.
    /// </summary>
    /// <param name="routes">Input pillar routes from PillarRouter.</param>
    /// <param name="config">Tree merging configuration.</param>
    /// <returns>Modified route list with merged tree structures.</returns>
    public static List<(string id, PillarRouter.PillarRoute route)> MergeIntoTrees(
        List<(string id, PillarRouter.PillarRoute route)> routes,
        TreeConfig config)
    {
        if (routes.Count < 2) return routes;

        // Step 1: Extract pillar base positions and heights
        var basePositions = new Vector3[routes.Count];
        var topZ = new float[routes.Count];
        var baseZ = new float[routes.Count];
        var pillarRadii = new float[routes.Count];

        for (int i = 0; i < routes.Count; i++)
        {
            var path = routes[i].route.Path;
            basePositions[i] = path[^1].Position;  // last waypoint = base
            topZ[i] = path[0].Position.Z;          // first waypoint = junction (top)
            baseZ[i] = path[^1].Position.Z;

            // Find the representative pillar radius (first pillar-type waypoint)
            pillarRadii[i] = path[0].Radius;
            foreach (var wp in path)
            {
                if (wp.Type == "pillar")
                {
                    pillarRadii[i] = wp.Radius;
                    break;
                }
            }
        }

        // Step 2: Group pillars by proximity using union-find
        var parent = new int[routes.Count];
        var rank = new int[routes.Count];
        for (int i = 0; i < routes.Count; i++) parent[i] = i;

        float maxDist2 = config.MaxMergeDistMm * config.MaxMergeDistMm;
        float maxBranchAngleRad = config.BranchAngleMaxDeg * MathF.PI / 180f;

        for (int i = 0; i < routes.Count; i++)
        {
            if (!routes[i].route.ReachesGround) continue; // skip anchored supports

            for (int j = i + 1; j < routes.Count; j++)
            {
                if (!routes[j].route.ReachesGround) continue;

                // XY distance between bases
                float dx = basePositions[i].X - basePositions[j].X;
                float dy = basePositions[i].Y - basePositions[j].Y;
                float dist2 = dx * dx + dy * dy;

                if (dist2 <= maxDist2)
                {
                    Union(parent, rank, i, j);
                }
            }
        }

        // Step 3: Collect groups
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < routes.Count; i++)
        {
            int root = Find(parent, i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        // Step 4: Process each group
        var result = new List<(string id, PillarRouter.PillarRoute route)>(routes.Count);
        var processed = new HashSet<int>();

        foreach (var (_, groupIndices) in groups)
        {
            if (groupIndices.Count < 2)
            {
                // Single pillar — no merging needed
                foreach (int idx in groupIndices)
                {
                    result.Add(routes[idx]);
                    processed.Add(idx);
                }
                continue;
            }

            // Filter: only merge pillars that reach the ground
            var mergeableIndices = groupIndices
                .Where(i => routes[i].route.ReachesGround)
                .ToList();

            var nonMergeableIndices = groupIndices
                .Where(i => !routes[i].route.ReachesGround)
                .ToList();

            // Add non-mergeable pillars as-is
            foreach (int idx in nonMergeableIndices)
            {
                result.Add(routes[idx]);
                processed.Add(idx);
            }

            if (mergeableIndices.Count < 2)
            {
                // Not enough mergeable pillars
                foreach (int idx in mergeableIndices)
                {
                    result.Add(routes[idx]);
                    processed.Add(idx);
                }
                continue;
            }

            // Compute merge point
            float centroidX = 0, centroidY = 0;
            float minHeight = float.MaxValue;
            float minBaseZ = float.MaxValue;

            foreach (int idx in mergeableIndices)
            {
                centroidX += basePositions[idx].X;
                centroidY += basePositions[idx].Y;
                float h = topZ[idx] - baseZ[idx];
                if (h < minHeight) minHeight = h;
                if (baseZ[idx] < minBaseZ) minBaseZ = baseZ[idx];
            }
            centroidX /= mergeableIndices.Count;
            centroidY /= mergeableIndices.Count;

            float mergeZ = minBaseZ + minHeight * config.MinMergeHeightRatio;

            // Validate branch angles — ensure branches from merge point to each
            // pillar's path at mergeZ don't exceed the max angle
            var validForMerge = new List<int>();
            foreach (int idx in mergeableIndices)
            {
                // Find where the pillar crosses mergeZ
                var pillarXY = GetPillarXYAtZ(routes[idx].route, mergeZ);
                if (!pillarXY.HasValue)
                {
                    // Pillar doesn't extend to mergeZ — can't merge
                    result.Add(routes[idx]);
                    processed.Add(idx);
                    continue;
                }

                float branchDx = pillarXY.Value.X - centroidX;
                float branchDy = pillarXY.Value.Y - centroidY;
                float branchXYDist = MathF.Sqrt(branchDx * branchDx + branchDy * branchDy);

                // Branch goes from merge point (centroidX, centroidY, mergeZ) up to
                // the pillar at mergeZ. But we actually need the angle from vertical
                // for the branch from pillar-at-mergeZ to the merge point (going down).
                // The branch height is 0 (same Z), so we measure the diagonal to a
                // slightly lower point. Since the merge point IS at mergeZ, the branch
                // is horizontal — check if the angle is acceptable by computing angle
                // of the branch segment from the pillar's mergeZ position to the merge point.
                // In practice we measure the angle as atan(XY_distance / some_height).
                // For tree supports, branches angle from the junction above mergeZ down to mergeZ.
                float branchHeight = topZ[idx] - mergeZ;
                if (branchHeight < 0.5f)
                {
                    result.Add(routes[idx]);
                    processed.Add(idx);
                    continue;
                }

                float branchAngle = MathF.Atan2(branchXYDist, 0.1f); // near-horizontal check
                // More meaningful: check angle from the pillar's XY at mergeZ to centroid
                // relative to the vertical trunk below
                float angleFromVertical = MathF.Atan2(branchXYDist, mergeZ - minBaseZ);
                if (angleFromVertical > maxBranchAngleRad && branchXYDist > 1.0f)
                {
                    // Branch too steep — don't merge this pillar
                    result.Add(routes[idx]);
                    processed.Add(idx);
                    continue;
                }

                validForMerge.Add(idx);
            }

            if (validForMerge.Count < 2)
            {
                // Not enough valid pillars for merging
                foreach (int idx in validForMerge)
                {
                    if (!processed.Contains(idx))
                    {
                        result.Add(routes[idx]);
                        processed.Add(idx);
                    }
                }
                continue;
            }

            // Compute trunk radius from cross-section area equivalence:
            // A_trunk = sum(A_branch_i) = sum(PI * r_i^2)
            // r_trunk = sqrt(sum(r_i^2))
            float sumR2 = 0;
            float maxBranchR = 0;
            foreach (int idx in validForMerge)
            {
                float r = pillarRadii[idx];
                sumR2 += r * r;
                if (r > maxBranchR) maxBranchR = r;
            }
            float trunkRadiusArea = MathF.Sqrt(sumR2);
            float trunkRadiusScaled = maxBranchR * config.TrunkRadiusScale;
            float trunkRadius = MathF.Max(trunkRadiusArea, trunkRadiusScaled);

            // Find the base radius for the shared trunk (use largest base radius from group)
            float trunkBaseRadius = 0;
            float trunkBaseHeight = 0;
            foreach (int idx in validForMerge)
            {
                var basePath = routes[idx].route.Path;
                var baseWp = basePath[^1];
                if (baseWp.Radius > trunkBaseRadius) trunkBaseRadius = baseWp.Radius;
                // Look for base height from the routing config
                if (basePath.Count >= 2)
                {
                    var secondToLast = basePath[^2];
                    float bh = secondToLast.Position.Z - baseWp.Position.Z;
                    if (bh > trunkBaseHeight) trunkBaseHeight = bh;
                }
            }
            if (trunkBaseRadius < trunkRadius * 2f)
                trunkBaseRadius = trunkRadius * 2f;
            if (trunkBaseHeight < 1.0f)
                trunkBaseHeight = 1.0f;

            // Build the shared trunk path from merge point down to build plate
            var trunkPath = BuildTrunkPath(
                centroidX, centroidY, mergeZ, minBaseZ,
                trunkRadius, trunkBaseRadius, trunkBaseHeight);

            // Now rebuild each pillar's route: keep everything above mergeZ,
            // add a branch from their mergeZ position to the merge point centroid,
            // then attach the shared trunk
            foreach (int idx in validForMerge)
            {
                var originalRoute = routes[idx].route;
                var newPath = new List<PillarRouter.Waypoint>();

                // Copy waypoints above mergeZ
                foreach (var wp in originalRoute.Path)
                {
                    if (wp.Position.Z > mergeZ)
                    {
                        newPath.Add(wp);
                    }
                    else
                    {
                        break; // stop once we reach/pass mergeZ
                    }
                }

                // If no waypoints were added (pillar starts below mergeZ), use the junction
                if (newPath.Count == 0)
                {
                    newPath.Add(originalRoute.Path[0]);
                }

                // Get the pillar's XY position at mergeZ
                var pillarAtMerge = GetPillarXYAtZ(originalRoute, mergeZ);
                var branchStart = pillarAtMerge ?? new Vector2(
                    newPath[^1].Position.X, newPath[^1].Position.Y);

                // Add branch waypoint at the pillar's mergeZ position
                float branchR = pillarRadii[idx];
                if (MathF.Abs(branchStart.X - newPath[^1].Position.X) > 0.01f ||
                    MathF.Abs(branchStart.Y - newPath[^1].Position.Y) > 0.01f ||
                    MathF.Abs(mergeZ - newPath[^1].Position.Z) > 0.01f)
                {
                    newPath.Add(new PillarRouter.Waypoint
                    {
                        Position = new Vector3(branchStart.X, branchStart.Y, mergeZ),
                        Radius = branchR,
                        Type = "pillar",
                    });
                }

                // Add branch to merge point (horizontal or near-horizontal connection)
                if (MathF.Abs(branchStart.X - centroidX) > 0.1f ||
                    MathF.Abs(branchStart.Y - centroidY) > 0.1f)
                {
                    newPath.Add(new PillarRouter.Waypoint
                    {
                        Position = new Vector3(centroidX, centroidY, mergeZ),
                        Radius = branchR,
                        Type = "bridge",
                    });
                }

                // Add junction sphere at merge point
                newPath.Add(new PillarRouter.Waypoint
                {
                    Position = new Vector3(centroidX, centroidY, mergeZ),
                    Radius = trunkRadius,
                    Type = "junction",
                });

                // Add trunk path
                newPath.AddRange(trunkPath);

                var newRoute = new PillarRouter.PillarRoute
                {
                    Path = newPath,
                    ReachesGround = true,
                    TotalLength = ComputePathLength(newPath),
                };

                result.Add((routes[idx].id, newRoute));
                processed.Add(idx);
            }
        }

        // Safety: add any routes that weren't processed
        for (int i = 0; i < routes.Count; i++)
        {
            if (!processed.Contains(i))
                result.Add(routes[i]);
        }

        return result;
    }

    // ── Trunk path construction ─────────────────────────────────────────

    /// <summary>
    /// Build the vertical trunk from the merge point down to the build plate.
    /// </summary>
    private static List<PillarRouter.Waypoint> BuildTrunkPath(
        float x, float y, float mergeZ, float baseZ,
        float trunkRadius, float baseRadius, float baseHeight)
    {
        var path = new List<PillarRouter.Waypoint>();
        float trunkHeight = mergeZ - baseZ - baseHeight;

        if (trunkHeight < 0.5f)
        {
            // Very short trunk — just the base
            path.Add(new PillarRouter.Waypoint
            {
                Position = new Vector3(x, y, baseZ),
                Radius = baseRadius,
                Type = "base",
            });
            return path;
        }

        // Trunk segments (every 10mm or at least 3)
        int segments = Math.Max(3, (int)(trunkHeight / 10f));
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float z = mergeZ - trunkHeight * t;
            // Slight widening towards base
            float wideningR = trunkRadius + (baseRadius - trunkRadius) * t * 0.3f;
            path.Add(new PillarRouter.Waypoint
            {
                Position = new Vector3(x, y, z),
                Radius = wideningR,
                Type = "pillar",
            });
        }

        // Base pedestal
        path.Add(new PillarRouter.Waypoint
        {
            Position = new Vector3(x, y, baseZ),
            Radius = baseRadius,
            Type = "base",
        });

        return path;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Get the XY position of a pillar at a given Z height by interpolating
    /// between adjacent waypoints.
    /// </summary>
    private static Vector2? GetPillarXYAtZ(PillarRouter.PillarRoute route, float z)
    {
        var path = route.Path;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float z1 = path[i].Position.Z;
            float z2 = path[i + 1].Position.Z;

            // Check if z falls between these two waypoints
            if ((z1 >= z && z2 <= z) || (z2 >= z && z1 <= z))
            {
                float range = z1 - z2;
                if (MathF.Abs(range) < 1e-6f)
                    return new Vector2(path[i].Position.X, path[i].Position.Y);

                float t = (z1 - z) / range;
                float x = path[i].Position.X + (path[i + 1].Position.X - path[i].Position.X) * t;
                float y = path[i].Position.Y + (path[i + 1].Position.Y - path[i].Position.Y) * t;
                return new Vector2(x, y);
            }
        }

        return null;
    }

    /// <summary>
    /// Union-Find: find root with path compression.
    /// </summary>
    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]]; // path compression
            i = parent[i];
        }
        return i;
    }

    /// <summary>
    /// Union-Find: merge two sets by rank.
    /// </summary>
    private static void Union(int[] parent, int[] rank, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra == rb) return;

        if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
        parent[rb] = ra;
        if (rank[ra] == rank[rb]) rank[ra]++;
    }

    private static float ComputePathLength(List<PillarRouter.Waypoint> path)
    {
        float len = 0;
        for (int i = 1; i < path.Count; i++)
            len += Vector3.Distance(path[i - 1].Position, path[i].Position);
        return len;
    }
}
