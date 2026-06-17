using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Cone-merge: merges nearby support pillars into shared trunks using
/// exact cone-intersection formulas (Vanek 2014 / CuraEngine).
///
/// θ = max branch angle from vertical (default 40°), m = tan(θ).
///
/// TWO-POINT MERGE closed form (P1 higher by Δh, horizontal sep D):
///   merge height: z_M = (z1 + z2)/2 − D/(2·m)
///   merge XY:     on the P1→P2 line at offset s_M = (D + m·Δh)/2 from P1
///   (equal height: z_M = z1 − D/(2m), s_M = D/2 — on the bisector)
///
/// CONTAINMENT: if D ≤ m·Δh, P2 lies inside P1's cone → attach directly,
/// no merge node needed.
///
/// Trunk radius after merge: R = sqrt(Σ branchR_i²).
/// </summary>
public static class AreaSupportMerger
{
    public sealed record MergeResult
    {
        public required int OriginalCount { get; init; }
        public required int MergedCount { get; init; }
        public required float VolumeReductionPct { get; init; }
        public required List<MergeInfo> Merges { get; init; }
    }

    public sealed record MergeInfo
    {
        public required int IndexA { get; init; }
        public required int IndexB { get; init; }
        public required Vector3 BranchPoint { get; init; }
        public required float TrunkRadius { get; init; }
        /// <summary>True if P2 is contained within P1's cone (no Steiner node needed).</summary>
        public required bool IsContainment { get; init; }
    }

    /// <summary>
    /// Find pairs of pillars that can be merged into shared trunks.
    /// </summary>
    /// <param name="pillarTops">Z height of each pillar's junction point.</param>
    /// <param name="pillarBases">XYZ of each pillar's base.</param>
    /// <param name="pillarRadii">Radius of each pillar.</param>
    /// <param name="mergeRadiusMm">Max horizontal distance to consider merging (snapping radius).</param>
    /// <param name="maxBranchAngleDeg">Max branch angle from vertical. Default 40°.</param>
    public static MergeResult FindMerges(
        List<Vector3> pillarBases,
        List<float> pillarRadii,
        List<float> pillarTops,
        float mergeRadiusMm = 8f,
        float maxBranchAngleDeg = 40f)
    {
        int n = pillarBases.Count;
        var merges = new List<MergeInfo>();
        var merged = new bool[n];

        // Guard: θ must be < 90° else tan → ∞
        float thetaDeg = Math.Clamp(maxBranchAngleDeg, 1f, 89f);
        float m = MathF.Tan(thetaDeg * MathF.PI / 180f); // m = tan(θ)

        for (int i = 0; i < n; i++)
        {
            if (merged[i]) continue;

            float bestDist = mergeRadiusMm;
            int bestJ = -1;

            for (int j = i + 1; j < n; j++)
            {
                if (merged[j]) continue;
                float dx = pillarBases[i].X - pillarBases[j].X;
                float dy = pillarBases[i].Y - pillarBases[j].Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist) { bestDist = dist; bestJ = j; }
            }

            if (bestJ < 0) continue;

            float D = bestDist; // horizontal separation
            float z1 = pillarTops[i], z2 = pillarTops[bestJ];

            // Ensure P1 is higher (or equal)
            int hi = z1 >= z2 ? i : bestJ;
            int lo = z1 >= z2 ? bestJ : i;
            float zHi = Math.Max(z1, z2);
            float zLo = Math.Min(z1, z2);
            float deltaH = zHi - zLo;

            // CONTAINMENT check: if D ≤ m·Δh, lower point is inside higher's cone
            bool isContainment = D <= m * deltaH;

            Vector3 branchPoint;
            if (isContainment)
            {
                // No Steiner node — attach lo directly onto hi's branch
                branchPoint = new Vector3(pillarBases[lo].X, pillarBases[lo].Y, zLo);
            }
            else
            {
                // TWO-POINT MERGE closed form
                float zM = (zHi + zLo) / 2f - D / (2f * m);

                // Clamp zM above the lower base
                float lowestBase = Math.Min(pillarBases[i].Z, pillarBases[bestJ].Z);
                zM = Math.Max(zM, lowestBase);

                // Merge XY: on the hi→lo line at offset sM from hi
                float sM = (D + m * deltaH) / 2f;
                sM = Math.Clamp(sM, 0, D); // safety clamp

                // Direction from hi to lo in XY
                float dirX = pillarBases[lo].X - pillarBases[hi].X;
                float dirY = pillarBases[lo].Y - pillarBases[hi].Y;
                float dirLen = MathF.Sqrt(dirX * dirX + dirY * dirY);
                if (dirLen > 0.001f) { dirX /= dirLen; dirY /= dirLen; }

                float bx = pillarBases[hi].X + dirX * sM;
                float by = pillarBases[hi].Y + dirY * sM;
                branchPoint = new Vector3(bx, by, zM);
            }

            // Trunk radius: R = sqrt(Σ branchR²)
            float r1 = pillarRadii[i], r2 = pillarRadii[bestJ];
            float trunkR = MathF.Sqrt(r1 * r1 + r2 * r2);

            merges.Add(new MergeInfo
            {
                IndexA = i, IndexB = bestJ,
                BranchPoint = branchPoint,
                TrunkRadius = trunkR,
                IsContainment = isContainment,
            });
            merged[i] = true;
            merged[bestJ] = true;
        }

        int mergedCount = n - merges.Count;
        float volumeReduction = n > 0 ? (float)merges.Count / n * 100f : 0;

        return new MergeResult
        {
            OriginalCount = n,
            MergedCount = mergedCount,
            VolumeReductionPct = volumeReduction,
            Merges = merges,
        };
    }
}
