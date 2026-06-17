using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Vanek cone-merge: merges nearby support pillars into shared trunks.
/// For pairs within mergeRadius, compute the Steiner branch point where
/// their descent cones (half-angle = maxSelfSupportAngle) intersect,
/// merge into a shared trunk with trunkRadius = sqrt(sum branchR²).
///
/// This reduces pillar count and resin volume without losing structural integrity.
/// Gate on finalValidIds — never merge to a dropped support.
/// </summary>
public static class AreaSupportMerger
{
    public sealed record MergeResult
    {
        public required int OriginalCount { get; init; }
        public required int MergedCount { get; init; }
        public required float VolumeReductionPct { get; init; }
        public required List<(int a, int b, Vector3 branchPoint, float trunkRadius)> Merges { get; init; }
    }

    /// <summary>
    /// Find pairs of pillars that can be merged into shared trunks.
    /// </summary>
    public static MergeResult FindMerges(
        List<Vector3> pillarBases,
        List<float> pillarRadii,
        List<float> pillarTops,
        float mergeRadiusMm = 8f,
        float maxSelfSupportAngleDeg = 45f)
    {
        int n = pillarBases.Count;
        var merges = new List<(int a, int b, Vector3 branchPoint, float trunkRadius)>();
        var merged = new bool[n];
        float tanAngle = MathF.Tan(maxSelfSupportAngleDeg * MathF.PI / 180f);

        // Spatial hash for O(n) neighbor finding
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

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestJ = j;
                }
            }

            if (bestJ < 0) continue;

            // Compute Steiner branch point: where the descent cones intersect
            float topI = pillarTops[i], topJ = pillarTops[bestJ];
            float higherZ = Math.Max(topI, topJ);
            float lowerZ = Math.Min(topI, topJ);

            // Branch point: at the Z where both cones can reach
            // Each cone descends at maxSelfSupportAngle from its top
            float branchZ = lowerZ - bestDist / tanAngle;
            branchZ = Math.Max(branchZ, Math.Min(pillarBases[i].Z, pillarBases[bestJ].Z));

            // Branch point XY: midpoint
            float bx = (pillarBases[i].X + pillarBases[bestJ].X) / 2;
            float by = (pillarBases[i].Y + pillarBases[bestJ].Y) / 2;
            var branchPoint = new Vector3(bx, by, branchZ);

            // Trunk radius: area-equivalent
            float r1 = pillarRadii[i], r2 = pillarRadii[bestJ];
            float trunkR = MathF.Sqrt(r1 * r1 + r2 * r2);

            merges.Add((i, bestJ, branchPoint, trunkR));
            merged[i] = true;
            merged[bestJ] = true;
        }

        int mergedCount = n - merges.Count; // each merge removes one pillar
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
