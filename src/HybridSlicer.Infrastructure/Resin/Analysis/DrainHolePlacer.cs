using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Identifies resin trap regions (concave pockets that would trap uncured resin)
/// and suggests drain hole positions.
///
/// Algorithm:
/// 1. Slice the mesh from top to bottom at layerHeight intervals
/// 2. Track "filled volume" — at each layer, check if contour area DECREASES
///    from the layer above (pocket is closing, trapping resin)
/// 3. For regions where area decreases by >50% within 5mm of height, mark as
///    resin trap and suggest a drain hole at the lowest external surface nearby
/// 4. Use BVH to find the nearest surface point for hole positioning
/// 5. The hole normal points outward (away from mesh interior)
/// </summary>
public static class DrainHolePlacer
{
    /// <summary>
    /// A suggested drain hole position with metadata.
    /// </summary>
    public sealed record DrainHole
    {
        /// <summary>Position on the mesh surface where the hole should be placed.</summary>
        public required Vector3 Position { get; init; }
        /// <summary>Outward-facing normal at the hole position.</summary>
        public required Vector3 Normal { get; init; }
        /// <summary>Suggested hole diameter in mm.</summary>
        public required float DiameterMm { get; init; }
        /// <summary>Estimated trapped volume this hole would drain (mm^3).</summary>
        public required float TrapVolumeMm3 { get; init; }
        /// <summary>Human-readable reason for this hole suggestion.</summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Configuration for drain hole placement.
    /// </summary>
    public sealed record DrainConfig
    {
        /// <summary>Diameter of suggested drain holes (mm).</summary>
        public float HoleDiameterMm { get; init; } = 2.5f;
        /// <summary>Minimum trapped volume to warrant a drain hole (mm^3).</summary>
        public float MinTrapVolumeMm3 { get; init; } = 50f;
        /// <summary>Layer height for slicing analysis (mm).</summary>
        public float LayerHeightMm { get; init; } = 1.0f;
    }

    // Height window to detect area decrease (mm)
    private const float TrapDetectionWindowMm = 5.0f;

    // Minimum area decrease ratio to flag as a trap
    private const float AreaDecreaseThreshold = 0.50f;

    // Minimum spacing between suggested drain holes (mm)
    private const float MinHoleSpacingMm = 10.0f;

    /// <summary>
    /// Analyze mesh for resin traps and suggest drain hole positions.
    /// </summary>
    /// <param name="mesh">The mesh to analyze.</param>
    /// <param name="config">Optional configuration; defaults to standard settings.</param>
    /// <returns>List of suggested drain holes, sorted by trapped volume descending.</returns>
    public static List<DrainHole> Suggest(StlMesh mesh, DrainConfig? config = null)
    {
        config ??= new DrainConfig();

        // Step 1: Slice the mesh from top to bottom
        var layers = BatchSlicer.SliceAll(mesh, config.LayerHeightMm);
        if (layers.Count < 3)
            return new List<DrainHole>();

        // Step 2: Build BVH for surface point queries
        var bvh = AabbBvh.Build(mesh);

        // Step 3: Detect resin traps by analyzing area changes top-to-bottom
        var traps = DetectResinTraps(layers, config);

        // Step 4: For each trap, find the best drain hole position
        var holes = new List<DrainHole>();

        foreach (var trap in traps)
        {
            if (trap.TrappedVolumeMm3 < config.MinTrapVolumeMm3)
                continue;

            // Find the nearest surface point to the trap's lowest point
            var drainHole = PlaceHoleForTrap(trap, bvh, mesh, config.HoleDiameterMm);
            if (drainHole != null)
            {
                // Check spacing against already-placed holes
                bool tooClose = false;
                foreach (var existing in holes)
                {
                    if (Vector3.Distance(existing.Position, drainHole.Position) < MinHoleSpacingMm)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                    holes.Add(drainHole);
            }
        }

        // Sort by trapped volume descending (largest traps first)
        holes.Sort((a, b) => b.TrapVolumeMm3.CompareTo(a.TrapVolumeMm3));

        return holes;
    }

    // ── Trap detection ──────────────────────────────────────────────────

    private sealed class ResinTrap
    {
        public required float TopZ { get; init; }
        public required float BottomZ { get; init; }
        public required float PeakAreaMm2 { get; init; }
        public required float BottomAreaMm2 { get; init; }
        public required float TrappedVolumeMm3 { get; init; }
        public required Vector2 Centroid { get; init; }
    }

    private static List<ResinTrap> DetectResinTraps(
        List<BatchSlicer.LayerResult> layers,
        DrainConfig config)
    {
        var traps = new List<ResinTrap>();

        int windowLayers = Math.Max(1, (int)(TrapDetectionWindowMm / config.LayerHeightMm));

        // Scan from top to bottom looking for area decreases
        // A resin trap forms when area decreases significantly (pocket closing)
        for (int i = layers.Count - 1; i >= windowLayers; i--)
        {
            float currentArea = layers[i].TotalArea;
            if (currentArea < 1.0f) continue; // skip negligible layers

            // Look at layers below: if area decreases rapidly, it's a pocket closing
            float peakArea = currentArea;
            float peakZ = layers[i].Z;
            int peakIndex = i;

            // Find the local area peak (widest part of the pocket)
            for (int j = i; j >= Math.Max(0, i - windowLayers); j--)
            {
                if (layers[j].TotalArea > peakArea)
                {
                    peakArea = layers[j].TotalArea;
                    peakZ = layers[j].Z;
                    peakIndex = j;
                }
            }

            // Check if area decreases below the peak
            int bottomIndex = Math.Max(0, peakIndex - windowLayers);
            float bottomArea = layers[bottomIndex].TotalArea;
            float bottomZ = layers[bottomIndex].Z;

            float decreaseRatio = peakArea > 0.01f
                ? (peakArea - bottomArea) / peakArea
                : 0f;

            if (decreaseRatio >= AreaDecreaseThreshold && peakArea > 5.0f)
            {
                // Estimate trapped volume: integrate area difference across layers
                float trappedVolume = 0f;
                float centroidX = 0f, centroidY = 0f;
                int centroidCount = 0;

                for (int j = peakIndex; j >= bottomIndex; j--)
                {
                    float layerArea = layers[j].TotalArea;
                    // The "trapped" portion is the area that exists above but not at this level
                    float trappedArea = Math.Max(0, peakArea - layerArea);
                    trappedVolume += trappedArea * config.LayerHeightMm;

                    // Accumulate centroid from contour data
                    foreach (var contour in layers[j].Contours)
                    {
                        if (contour.Count < 3) continue;
                        foreach (var pt in contour)
                        {
                            centroidX += pt.X;
                            centroidY += pt.Y;
                            centroidCount++;
                        }
                    }
                }

                if (centroidCount > 0)
                {
                    centroidX /= centroidCount;
                    centroidY /= centroidCount;
                }

                traps.Add(new ResinTrap
                {
                    TopZ = peakZ,
                    BottomZ = bottomZ,
                    PeakAreaMm2 = peakArea,
                    BottomAreaMm2 = bottomArea,
                    TrappedVolumeMm3 = trappedVolume,
                    Centroid = new Vector2(centroidX, centroidY),
                });

                // Skip past this trap to avoid double-counting
                i = bottomIndex;
            }
        }

        return traps;
    }

    // ── Hole placement ──────────────────────────────────────────────────

    private static DrainHole? PlaceHoleForTrap(
        ResinTrap trap, AabbBvh bvh, StlMesh mesh, float holeDiameter)
    {
        // The drain hole should be at the lowest point of the trap,
        // on the nearest external surface.

        // Start from the centroid at the bottom of the trap
        var interiorPoint = new Vector3(trap.Centroid.X, trap.Centroid.Y, trap.BottomZ);

        // Find the nearest surface point using BVH
        var closest = bvh.ClosestPoint(interiorPoint);
        if (closest == null)
            return null;

        var surfacePoint = closest.Value.Point;
        var surfaceNormal = closest.Value.Normal;

        // Ensure the normal points outward (away from mesh interior).
        // If the surface point is on the inside of the pocket, the normal
        // should already point inward; we flip it to point outward.
        // Test: if moving along the normal goes deeper inside, flip it.
        var testPoint = surfacePoint + surfaceNormal * 0.5f;
        if (bvh.IsInside(testPoint))
        {
            surfaceNormal = -surfaceNormal;
        }

        // Prefer placing hole at lower Z positions for gravity drainage.
        // If the surface point is much higher than the trap bottom,
        // try to find a lower surface point by searching downward.
        if (surfacePoint.Z > trap.BottomZ + TrapDetectionWindowMm)
        {
            // Search for lower surface points around the trap
            var lowerPoint = new Vector3(trap.Centroid.X, trap.Centroid.Y, trap.BottomZ - 1f);
            var lowerClosest = bvh.ClosestPoint(lowerPoint);
            if (lowerClosest != null && lowerClosest.Value.Point.Z < surfacePoint.Z)
            {
                surfacePoint = lowerClosest.Value.Point;
                surfaceNormal = lowerClosest.Value.Normal;

                // Re-check outward direction
                testPoint = surfacePoint + surfaceNormal * 0.5f;
                if (bvh.IsInside(testPoint))
                    surfaceNormal = -surfaceNormal;
            }
        }

        string reason = $"Resin trap: {trap.TrappedVolumeMm3:F0}mm3 trapped between Z={trap.BottomZ:F1}mm and Z={trap.TopZ:F1}mm " +
                         $"(area drops from {trap.PeakAreaMm2:F0}mm2 to {trap.BottomAreaMm2:F0}mm2)";

        return new DrainHole
        {
            Position = surfacePoint,
            Normal = surfaceNormal,
            DiameterMm = holeDiameter,
            TrapVolumeMm3 = trap.TrappedVolumeMm3,
            Reason = reason,
        };
    }
}
