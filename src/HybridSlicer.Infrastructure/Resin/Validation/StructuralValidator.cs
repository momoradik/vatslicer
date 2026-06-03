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
    private const float GRAVITY = 9810f; // mm/s²

    /// <summary>
    /// Configurable resin material properties. Different resins have vastly different
    /// mechanical properties — using wrong values causes structural failures.
    /// </summary>
    public sealed record MaterialProperties
    {
        /// <summary>Young's modulus (MPa). Standard=2000, Tough=3000, Flexible=800, Castable=2500.</summary>
        public float ElasticModulusMpa { get; init; } = 2000f;
        /// <summary>Ultimate tensile strength (MPa). Standard=40, Tough=65, Flexible=25, Castable=45.</summary>
        public float TensileStrengthMpa { get; init; } = 40f;
        /// <summary>Density (kg/mm³). Typically 1.0-1.3 g/cm³ = 1.0e-6 to 1.3e-6 kg/mm³.</summary>
        public float DensityKgPerMm3 { get; init; } = 1.1e-6f;
        /// <summary>Peel force coefficient (N/mm²). FEP=0.01-0.03, nFEP=0.005-0.015.</summary>
        public float PeelCoefficientNPerMm2 { get; init; } = 0.015f;

        public static readonly MaterialProperties Standard = new();
        public static readonly MaterialProperties Tough = new()
        {
            ElasticModulusMpa = 3000f, TensileStrengthMpa = 65f, DensityKgPerMm3 = 1.15e-6f,
        };
        public static readonly MaterialProperties Flexible = new()
        {
            ElasticModulusMpa = 800f, TensileStrengthMpa = 25f, DensityKgPerMm3 = 1.05e-6f,
        };
        public static readonly MaterialProperties Castable = new()
        {
            ElasticModulusMpa = 2500f, TensileStrengthMpa = 45f, DensityKgPerMm3 = 1.2e-6f,
        };
        public static readonly MaterialProperties DentalModel = new()
        {
            ElasticModulusMpa = 2200f, TensileStrengthMpa = 50f, DensityKgPerMm3 = 1.12e-6f,
        };
    }

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
        float minSafetyFactor = 2.0f,
        MaterialProperties? material = null,
        StlMesh? sourceMesh = null,
        AabbBvh? bvh = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mat = material ?? MaterialProperties.Standard;
        var issues = new List<StructuralIssue>();

        int passedBuckling = 0, failedBuckling = 0;
        int passedTensile = 0, failedTensile = 0;
        float minSF = float.MaxValue;
        float totalSF = 0;

        // Build a lookup from support position to nearby overhang area for accurate load estimation
        var overhangLookup = new SpatialGrid<float>(10f);
        foreach (var region in overhangRegions)
            overhangLookup.Insert(new Vector3(region.Centroid.X, region.Centroid.Y, region.Z), region.Area);

        // ── 1. Per-support structural checks ─────────────────────────────

        foreach (var (id, route, contactZ) in supports)
        {
            // Use minimum radius for buckling (weakest point), not average
            float pillarHeight = 0;
            float minRadius = float.MaxValue;
            float maxRadius = 0;
            foreach (var wp in route.Path)
            {
                if (wp.Type == "pillar" || wp.Type == "junction")
                {
                    minRadius = Math.Min(minRadius, wp.Radius);
                    maxRadius = Math.Max(maxRadius, wp.Radius);
                }
            }
            if (route.Path.Count >= 2)
                pillarHeight = route.Path[0].Position.Z - route.Path[^1].Position.Z;

            if (pillarHeight < 0.5f || minRadius >= float.MaxValue)
            {
                passedBuckling++;
                passedTensile++;
                continue;
            }

            // Use actual overhang area from nearby regions instead of estimating from pillar radius
            var topPos = route.Path[0].Position;
            float actualOverhangArea = 0;
            var nearbyAreas = overhangLookup.FindInRadius(topPos, 15f);
            if (nearbyAreas.Count > 0)
            {
                float totalNearbyArea = nearbyAreas.Sum(a => a.id); // id is the area (float)
                // Each support shares the load with other supports in the region
                int supportsNearby = Math.Max(1, supportGrid.CountInRadius(topPos, 10f));
                actualOverhangArea = totalNearbyArea / supportsNearby;
            }
            // Fallback: use pillar cross-section if no overhang data
            float loadArea = actualOverhangArea > 0.1f
                ? actualOverhangArea
                : MathF.PI * minRadius * minRadius * 4f;

            float loadVolume = loadArea * pillarHeight * 0.3f; // 30% fill approximation
            float gravityForce = loadVolume * mat.DensityKgPerMm3 * GRAVITY;
            float peelForce = loadArea * mat.PeelCoefficientNPerMm2;
            float totalForce = gravityForce + peelForce;

            // ── Euler buckling (uses minimum radius = weakest cross-section) ──
            float momentOfInertia = MathF.PI * MathF.Pow(minRadius, 4) / 4f;
            float criticalLoad = MathF.PI * MathF.PI * mat.ElasticModulusMpa * momentOfInertia /
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
            float tensileCapacity = crossSectionArea * mat.TensileStrengthMpa;
            float tensileSF = totalForce > 0 ? tensileCapacity / totalForce : 100f;
            tensileSF = Math.Min(tensileSF, 100f);

            // ── Bending moment check ──
            // Bending from eccentric peel load. The bending arm is the distance from
            // pillar center to centroid of tributary area, approximately spacing/4
            float tributaryRadius = MathF.Sqrt(loadArea / MathF.PI);
            float bendingArm = Math.Min(tributaryRadius * 0.5f, minRadius * 3f);
            float bendingMoment = peelForce * bendingArm;
            float sectionModulus = MathF.PI * MathF.Pow(minRadius, 3) / 4f;
            float bendingStress = sectionModulus > 0 ? bendingMoment / sectionModulus : 0;
            float combinedStress = totalForce / crossSectionArea + bendingStress;
            float combinedSF = combinedStress > 0 ? mat.TensileStrengthMpa / combinedStress : 100f;
            combinedSF = Math.Min(combinedSF, 100f);

            float effectiveTensileSF = Math.Min(tensileSF, combinedSF);

            if (effectiveTensileSF >= minSafetyFactor)
            {
                passedTensile++;
            }
            else
            {
                failedTensile++;
                issues.Add(new StructuralIssue
                {
                    SupportId = id, Category = "tensile",
                    Description = $"Tensile+bending SF={effectiveTensileSF:F1} (need {minSafetyFactor:F1}). " +
                                  $"area={crossSectionArea:F2}mm² axial={totalForce:F3}N bending={bendingMoment:F3}N·mm",
                    SafetyFactor = effectiveTensileSF,
                    X = route.Path[0].Position.X, Y = route.Path[0].Position.Y, Z = contactZ,
                });
            }

            float sf = Math.Min(bucklingSF, effectiveTensileSF);
            minSF = Math.Min(minSF, sf);
            totalSF += sf;
        }

        if (minSF == float.MaxValue) minSF = 0;
        float avgSF = supports.Count > 0 ? totalSF / supports.Count : 0;

        // ── 2. Coverage verification ─────────────────────────────────────

        int covered = 0, uncovered = 0;
        float baseCoverageRadius = 8f;

        // Cache overhang triangles per Z for intersection filtering
        var ovhTriCache = new Dictionary<int, HashSet<int>>();

        foreach (var region in overhangRegions)
        {
            if (region.Area < 4f) { covered++; continue; }

            // Compute the effective overhang area by intersecting the slicer contour
            // with actual overhang triangles. Points in the contour that sit over
            // non-overhang geometry (walls, supported faces) are excluded.
            var center3d = new Vector3(region.Centroid.X, region.Centroid.Y, region.Z);
            float effectiveArea = region.Area;

            if (sourceMesh != null && bvh != null && region.Contour.Count >= 3)
            {
                // Get overhang triangles at this Z
                int zKey = (int)(region.Z * 10);
                if (!ovhTriCache.TryGetValue(zKey, out var ovhTris))
                {
                    ovhTris = sourceMesh.FindOverhangTrianglesAtZ(region.Z);
                    ovhTriCache[zKey] = ovhTris;
                }

                if (ovhTris.Count > 0)
                {
                    // Sample contour points and check which ones are over actual overhang triangles
                    int totalSamples = Math.Min(region.Contour.Count, 16);
                    int overhangSamples = 0;
                    for (int si = 0; si < totalSamples; si++)
                    {
                        var pt = region.Contour[si * region.Contour.Count / totalSamples];
                        var pt3d = new Vector3(pt.X, pt.Y, region.Z);
                        var closest = bvh.ClosestPoint(pt3d, ovhTris);
                        if (closest.HasValue && closest.Value.Distance < 3f)
                            overhangSamples++;
                    }
                    // Scale area by the fraction that's actually over overhang geometry
                    float overhangFraction = totalSamples > 0 ? (float)overhangSamples / totalSamples : 1f;
                    effectiveArea = region.Area * overhangFraction;
                }
                else
                {
                    // No overhang triangles at this Z — the entire region is a slicer artifact
                    covered++;
                    continue;
                }
            }

            // Skip if effective overhang area is too small after intersection
            if (effectiveArea < 4f) { covered++; continue; }

            float regionRadius = MathF.Sqrt(effectiveArea / MathF.PI);
            float coverageRadius = Math.Min(baseCoverageRadius, regionRadius * 2f);

            bool isCovered = supportGrid.ExistsInRadius(center3d, coverageRadius);

            // For large effective regions, also check boundary points
            if (isCovered && region.Contour.Count >= 3 && effectiveArea > 10f)
            {
                int boundarySamples = Math.Min(region.Contour.Count, 8);
                int unsupportedBoundary = 0;
                for (int bi = 0; bi < boundarySamples; bi++)
                {
                    var bpt = region.Contour[bi * region.Contour.Count / boundarySamples];
                    var bpt3d = new Vector3(bpt.X, bpt.Y, region.Z);

                    // Only check boundary points that are over actual overhang geometry
                    if (sourceMesh != null && bvh != null)
                    {
                        int zk = (int)(region.Z * 10);
                        if (ovhTriCache.TryGetValue(zk, out var tris) && tris.Count > 0)
                        {
                            var cp = bvh.ClosestPoint(bpt3d, tris);
                            if (!cp.HasValue || cp.Value.Distance > 3f)
                                continue; // this boundary point is not over overhang — skip it
                        }
                    }

                    if (!supportGrid.ExistsInRadius(bpt3d, coverageRadius * 1.5f))
                        unsupportedBoundary++;
                }
                if (unsupportedBoundary > boundarySamples / 2)
                    isCovered = false;
            }

            if (isCovered)
            {
                covered++;
            }
            else
            {
                uncovered++;
                if (uncovered <= 20)
                {
                    issues.Add(new StructuralIssue
                    {
                        SupportId = "-", Category = "coverage",
                        Description = $"Overhang region (area={effectiveArea:F1}mm² effective, type={region.Type}) " +
                                      $"has insufficient support coverage within {coverageRadius:F1}mm",
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
