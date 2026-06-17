using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Auto-arranges multiple models on a build plate using bottom-left bin packing.
///
/// Each model is represented by its XY bounding box. The algorithm places models
/// in decreasing-area order using a skyline-based placement strategy that finds
/// the lowest available position without overlapping existing placements.
///
/// Approach: Skyline Bottom-Left (SBL) — fast, produces good results for typical
/// MSLA print plate arrangements (5-50 parts). O(n^2) in number of parts.
/// </summary>
public static class BuildPlateNester
{
    public sealed class NestConfig
    {
        /// <summary>Build plate usable width (mm).</summary>
        public float PlateWidthMm { get; init; } = 192f;
        /// <summary>Build plate usable depth (mm).</summary>
        public float PlateDepthMm { get; init; } = 120f;
        /// <summary>Minimum gap between parts (mm).</summary>
        public float PartGapMm { get; init; } = 2.0f;
        /// <summary>Margin from plate edges (mm).</summary>
        public float PlateMarginMm { get; init; } = 3.0f;
    }

    /// <summary>A part to be placed, described by its XY bounding box.</summary>
    public sealed class PartFootprint
    {
        /// <summary>Part identifier.</summary>
        public required string Id { get; init; }
        /// <summary>Width of the part's XY bounding box (mm).</summary>
        public required float WidthMm { get; init; }
        /// <summary>Depth of the part's XY bounding box (mm).</summary>
        public required float DepthMm { get; init; }
    }

    /// <summary>Result of nesting: where each part should be placed.</summary>
    public sealed class NestResult
    {
        /// <summary>Placement positions (center of each part's bounding box).</summary>
        public required List<PartPlacement> Placements { get; init; }
        /// <summary>Part IDs that didn't fit on the plate.</summary>
        public required List<string> Overflow { get; init; }
        /// <summary>Plate utilization ratio (0-1).</summary>
        public required float Utilization { get; init; }
    }

    public sealed class PartPlacement
    {
        /// <summary>Part identifier.</summary>
        public required string Id { get; init; }
        /// <summary>Center X position on the build plate (mm).</summary>
        public required float CenterX { get; init; }
        /// <summary>Center Y position on the build plate (mm).</summary>
        public required float CenterY { get; init; }
        /// <summary>Whether the part was rotated 90 degrees for better fit.</summary>
        public required bool Rotated90 { get; init; }
    }

    /// <summary>
    /// Arrange parts on the build plate using skyline bottom-left packing.
    /// </summary>
    public static NestResult Arrange(IReadOnlyList<PartFootprint> parts, NestConfig config)
    {
        float usableW = config.PlateWidthMm - 2 * config.PlateMarginMm;
        float usableD = config.PlateDepthMm - 2 * config.PlateMarginMm;
        float gap = config.PartGapMm;

        if (usableW <= 0 || usableD <= 0 || parts.Count == 0)
            return new NestResult { Placements = new(), Overflow = parts.Select(p => p.Id).ToList(), Utilization = 0 };

        // Sort by area descending (large parts first = better packing)
        var sorted = parts.OrderByDescending(p => p.WidthMm * p.DepthMm).ToList();

        // Skyline: tracks the height profile across the plate width.
        // Each segment is (x, y) = left X position and top Y of the placed material.
        var skyline = new List<(float x, float y)> { (0, 0) };

        var placements = new List<PartPlacement>();
        var overflow = new List<string>();
        float totalPlacedArea = 0;

        foreach (var part in sorted)
        {
            // Try both orientations
            float w1 = part.WidthMm + gap, d1 = part.DepthMm + gap;
            float w2 = part.DepthMm + gap, d2 = part.WidthMm + gap;

            var (pos1, waste1) = FindBestPosition(skyline, w1, d1, usableW, usableD);
            var (pos2, waste2) = FindBestPosition(skyline, w2, d2, usableW, usableD);

            bool useRotated;
            Vector2? bestPos;
            float bestW, bestD;

            if (pos1.HasValue && pos2.HasValue)
            {
                useRotated = waste2 < waste1;
                bestPos = useRotated ? pos2 : pos1;
                bestW = useRotated ? w2 : w1;
                bestD = useRotated ? d2 : d1;
            }
            else if (pos1.HasValue)
            {
                useRotated = false; bestPos = pos1; bestW = w1; bestD = d1;
            }
            else if (pos2.HasValue)
            {
                useRotated = true; bestPos = pos2; bestW = w2; bestD = d2;
            }
            else
            {
                overflow.Add(part.Id);
                continue;
            }

            float px = bestPos.Value.X;
            float py = bestPos.Value.Y;

            // Update skyline
            AddToSkyline(skyline, px, px + bestW, py + bestD, usableW);

            // Center position (account for gap and plate margin)
            float actualW = useRotated ? part.DepthMm : part.WidthMm;
            float actualD = useRotated ? part.WidthMm : part.DepthMm;
            float centerX = config.PlateMarginMm + px + actualW / 2 + gap / 2;
            float centerY = config.PlateMarginMm + py + actualD / 2 + gap / 2;

            placements.Add(new PartPlacement
            {
                Id = part.Id,
                CenterX = centerX,
                CenterY = centerY,
                Rotated90 = useRotated,
            });

            totalPlacedArea += part.WidthMm * part.DepthMm;
        }

        float plateArea = usableW * usableD;
        float utilization = plateArea > 0 ? totalPlacedArea / plateArea : 0;

        return new NestResult { Placements = placements, Overflow = overflow, Utilization = utilization };
    }

    /// <summary>
    /// Find the best bottom-left position for a rectangle of size (w, d) on the skyline.
    /// Returns the position and a waste metric (lower = better fit).
    /// </summary>
    private static (Vector2? pos, float waste) FindBestPosition(
        List<(float x, float y)> skyline, float w, float d, float maxW, float maxD)
    {
        Vector2? bestPos = null;
        float bestWaste = float.MaxValue;

        for (int i = 0; i < skyline.Count; i++)
        {
            float startX = skyline[i].x;
            if (startX + w > maxW + 0.01f) continue;

            // Find the max Y within [startX, startX+w] — this is where the rectangle bottom sits
            float maxY = 0;
            float endX = startX + w;
            for (int j = i; j < skyline.Count; j++)
            {
                float segEnd = j + 1 < skyline.Count ? skyline[j + 1].x : maxW;
                if (skyline[j].x >= endX) break;
                float overlapStart = Math.Max(skyline[j].x, startX);
                float overlapEnd = Math.Min(segEnd, endX);
                if (overlapEnd > overlapStart)
                    maxY = Math.Max(maxY, skyline[j].y);
            }

            if (maxY + d > maxD + 0.01f) continue;

            // Waste = height above the minimum skyline level in this region
            float waste = maxY + d;
            if (waste < bestWaste)
            {
                bestWaste = waste;
                bestPos = new Vector2(startX, maxY);
            }
        }

        return (bestPos, bestWaste);
    }

    /// <summary>
    /// Update the skyline after placing a rectangle from x1 to x2 at height y.
    /// </summary>
    private static void AddToSkyline(List<(float x, float y)> skyline, float x1, float x2, float y, float maxW)
    {
        // Remove segments that are fully covered by the new rectangle
        var newSkyline = new List<(float x, float y)>();
        bool inserted = false;

        for (int i = 0; i < skyline.Count; i++)
        {
            float segStart = skyline[i].x;
            float segEnd = i + 1 < skyline.Count ? skyline[i + 1].x : maxW;

            if (segEnd <= x1 || segStart >= x2)
            {
                // Segment is completely outside the new rectangle
                newSkyline.Add(skyline[i]);
            }
            else
            {
                // Segment overlaps — split around the new rectangle
                if (segStart < x1)
                    newSkyline.Add((segStart, skyline[i].y));

                if (!inserted)
                {
                    newSkyline.Add((x1, y));
                    inserted = true;
                }

                if (segEnd > x2)
                    newSkyline.Add((x2, skyline[i].y));
            }
        }

        if (!inserted)
            newSkyline.Add((x1, y));

        // Merge consecutive segments with the same height
        skyline.Clear();
        for (int i = 0; i < newSkyline.Count; i++)
        {
            if (skyline.Count > 0 && Math.Abs(skyline[^1].y - newSkyline[i].y) < 0.001f)
                continue;
            skyline.Add(newSkyline[i]);
        }
    }
}
