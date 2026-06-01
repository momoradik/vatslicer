using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Validation;

/// <summary>
/// Structural validation of generated supports.
///
/// Checks:
/// 1. Euler buckling: each pillar can withstand axial load without buckling
/// 2. Peel/recoat force: support cross-section can resist separation forces
/// 3. Coverage: every overhang region has adequate support density
/// 4. Manifold: merged support mesh is watertight (every edge shared by 2 faces)
/// 5. Minimum safety factor: no support below configurable threshold
///
/// For aerospace: all checks must pass with safety factor >= 2.0
/// </summary>
public static class StructuralValidator
{
    // Material properties
    private const float RESIN_ELASTIC_MODULUS_MPA = 2000f;
    private const float RESIN_TENSILE_STRENGTH_MPA = 40f;
    private const float RESIN_DENSITY_KG_PER_MM3 = 1.1e-6f;
    private const float GRAVITY = 9810f; // mm/s²

    public sealed class StructuralIssue
    {
        public required string SupportId { get; init; }
        public required string Category { get; init; } // "buckling", "tensile", "coverage", "manifold"
        public required string Description { get; init; }
        public required float SafetyFactor { get; init; } // <1 = failure, 1-2 = marginal, >2 = safe
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
    }

    public sealed class StructuralResult
    {
        public required int TotalSupports { get; init; }
        public required int PassedBuckling { get; init; }
        public required int FailedBuckling { get; init; }
        public required int PassedTensile { get; init; }
        public required int FailedTensile { get; init; }
        public required int OverhangRegionsCovered { get; init; }
        public required int OverhangRegionsUncovered { get; init; }
        public required float MinSafetyFactor { get; init; }
        public required float AvgSafetyFactor { get; init; }
        public required int ManifoldErrors { get; init; }
        public required List<StructuralIssue> Issues { get; init; }
        public required long ElapsedMs { get; init; }
    }

    /// <summary>
    /// Run full structural validation.
    /// </summary>
    public static StructuralResult Validate(
        List<(string id, PillarRouter.PillarRoute route, float contactZ)> supports,
        List<OverhangAnalyzer.OverhangRegion> overhangRegions,
        SpatialGrid<string> supportGrid,
        IndexedTriangleSet? mergedMesh,
        float minSafetyFactor = 2.0f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var issues = new List<StructuralIssue>();

        int passedBuckling = 0, failedBuckling = 0;
        int passedTensile = 0, failedTensile = 0;
        float minSF = float.MaxValue;
        float totalSF = 0;

        // ── 1. Per-support structural checks ─────────────────────────────

        foreach (var (id, route, contactZ) in supports)
        {
            // Find the main pillar dimensions
            float pillarHeight = 0;
            float minRadius = float.MaxValue;
            foreach (var wp in route.Path)
            {
                if (wp.Type == "pillar" || wp.Type == "junction")
                    minRadius = Math.Min(minRadius, wp.Radius);
            }
            if (route.Path.Count >= 2)
                pillarHeight = route.Path[0].Position.Z - route.Path[^1].Position.Z;

            if (pillarHeight < 0.5f || minRadius >= float.MaxValue)
            {
                passedBuckling++;
                passedTensile++;
                continue;
            }

            // Estimate load: weight of resin supported by this pillar
            // Conservative: assume each support carries a column of resin above it
            float loadArea = MathF.PI * minRadius * minRadius * 4f; // assume 4x pillar area
            float loadVolume = loadArea * pillarHeight * 0.3f; // 30% fill approximation
            float gravityForce = loadVolume * RESIN_DENSITY_KG_PER_MM3 * GRAVITY;
            float peelForce = loadArea * 0.015f; // peel coefficient
            float totalForce = gravityForce + peelForce;

            // ── Euler buckling ──
            float momentOfInertia = MathF.PI * MathF.Pow(minRadius, 4) / 4f;
            float criticalLoad = MathF.PI * MathF.PI * RESIN_ELASTIC_MODULUS_MPA * momentOfInertia /
                                (pillarHeight * pillarHeight);
            float bucklingSF = totalForce > 0 ? criticalLoad / totalForce : 100f;
            bucklingSF = Math.Min(bucklingSF, 100f);

            if (bucklingSF >= minSafetyFactor)
            {
                passedBuckling++;
            }
            else
            {
                failedBuckling++;
                issues.Add(new StructuralIssue
                {
                    SupportId = id, Category = "buckling",
                    Description = $"Euler buckling SF={bucklingSF:F1} (need {minSafetyFactor:F1}). " +
                                  $"r={minRadius:F2}mm h={pillarHeight:F1}mm load={totalForce:F3}N crit={criticalLoad:F3}N",
                    SafetyFactor = bucklingSF,
                    X = route.Path[0].Position.X, Y = route.Path[0].Position.Y, Z = contactZ,
                });
            }

            // ── Tensile (peel resistance) ──
            float crossSectionArea = MathF.PI * minRadius * minRadius;
            float tensileCapacity = crossSectionArea * RESIN_TENSILE_STRENGTH_MPA;
            float tensileSF = totalForce > 0 ? tensileCapacity / totalForce : 100f;
            tensileSF = Math.Min(tensileSF, 100f);

            if (tensileSF >= minSafetyFactor)
            {
                passedTensile++;
            }
            else
            {
                failedTensile++;
                issues.Add(new StructuralIssue
                {
                    SupportId = id, Category = "tensile",
                    Description = $"Tensile SF={tensileSF:F1} (need {minSafetyFactor:F1}). " +
                                  $"area={crossSectionArea:F2}mm² capacity={tensileCapacity:F1}N force={totalForce:F3}N",
                    SafetyFactor = tensileSF,
                    X = route.Path[0].Position.X, Y = route.Path[0].Position.Y, Z = contactZ,
                });
            }

            float sf = Math.Min(bucklingSF, tensileSF);
            minSF = Math.Min(minSF, sf);
            totalSF += sf;
        }

        if (minSF == float.MaxValue) minSF = 0;
        float avgSF = supports.Count > 0 ? totalSF / supports.Count : 0;

        // ── 2. Coverage verification ─────────────────────────────────────

        int covered = 0, uncovered = 0;
        float coverageRadius = 10f; // mm — generous coverage check

        foreach (var region in overhangRegions)
        {
            if (region.Area < 0.5f) { covered++; continue; }

            var center3d = new Vector3(region.Centroid.X, region.Centroid.Y, region.Z);
            if (supportGrid.ExistsInRadius(center3d, coverageRadius))
            {
                covered++;
            }
            else
            {
                uncovered++;
                if (uncovered <= 20) // limit reports
                {
                    issues.Add(new StructuralIssue
                    {
                        SupportId = "-", Category = "coverage",
                        Description = $"Overhang region (area={region.Area:F1}mm², type={region.Type}) has no support within {coverageRadius:F1}mm",
                        SafetyFactor = 0,
                        X = region.Centroid.X, Y = region.Centroid.Y, Z = region.Z,
                    });
                }
            }
        }

        // ── 3. Manifold validation ───────────────────────────────────────

        int manifoldErrors = 0;
        if (mergedMesh != null)
        {
            manifoldErrors = MeshMerger.CountNonManifoldEdges(mergedMesh);
            if (manifoldErrors > 0)
            {
                issues.Add(new StructuralIssue
                {
                    SupportId = "-", Category = "manifold",
                    Description = $"Merged support mesh has {manifoldErrors} non-manifold edges (not watertight)",
                    SafetyFactor = 0,
                });
            }
        }

        sw.Stop();
        return new StructuralResult
        {
            TotalSupports = supports.Count,
            PassedBuckling = passedBuckling,
            FailedBuckling = failedBuckling,
            PassedTensile = passedTensile,
            FailedTensile = failedTensile,
            OverhangRegionsCovered = covered,
            OverhangRegionsUncovered = uncovered,
            MinSafetyFactor = minSF,
            AvgSafetyFactor = avgSF,
            ManifoldErrors = manifoldErrors,
            Issues = issues,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
