using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Slicing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>
/// Production-grade resin support engine (V2).
///
/// Full pipeline:
/// 1. Build AABB BVH for O(log n) spatial queries
/// 2. Layer-based overhang analysis with island detection
/// 3. Structural support point generation with force estimation
/// 4. Pinhead optimization (iterative angle search with collision avoidance)
/// 5. Pillar routing around obstacles (direct descent → bridge → anchor)
/// 6. Cross-connection building with structural requirements
/// 7. Watertight mesh generation (sphere-cone-sphere pinheads, frustum pillars)
/// 8. Volumetric collision validation (BVH beam-cast)
/// 9. Structural validation (Euler buckling, peel force, coverage)
/// 10. Analytical slicing (exact circle cross-sections per layer)
///
/// This engine produces manufacturing-ready support structures, not visualization props.
/// </summary>
public static class SupportEngineV2
{
    // ── Configuration ────────────────────────────────────────────────────

    public sealed class EngineConfig
    {
        public PrinterOrientation Orientation { get; init; } = PrinterOrientation.BottomUp;
        /// <summary>
        /// Layer height for overhang analysis. Use coarser than print layer height
        /// for performance. 2-5mm is typical for support point placement.
        /// </summary>
        public float LayerHeightMm { get; init; } = 3.0f;
        public float OverhangAngleDeg { get; init; } = 45f;
        public float DensityFactor { get; init; } = 0.5f;
        public float MinSpacingMm { get; init; } = 2.0f;
        public float MaxSpacingMm { get; init; } = 8.0f;

        // Pinhead
        public float PinRadiusMm { get; init; } = 0.2f;
        public float BackRadiusMm { get; init; } = 0.5f;
        public float HeadWidthMm { get; init; } = 1.0f;
        public float PenetrationMm { get; init; } = 0.05f;

        // Pillar
        public float PillarRadiusMm { get; init; } = 0.5f;
        public float BaseRadiusMm { get; init; } = 2.0f;
        public float BaseHeightMm { get; init; } = 1.0f;
        /// <summary>
        /// Radius increase per mm of pillar descent. Higher = thicker base.
        /// 0.02 = 2% per mm → a 100mm pillar grows by 2mm radius at base.
        /// </summary>
        public float WideningFactor { get; init; } = 0.02f;
        public float MaxBridgeLengthMm { get; init; } = 15f;

        // Interconnections
        public bool EnableInterconnections { get; init; } = true;
        public float InterconnectDistMm { get; init; } = 20f;
        public float InterconnectIntervalMm { get; init; } = 5f;
        public float StrutRadiusMm { get; init; } = 0.3f;

        // Tree supports
        /// <summary>Enable tree support merging (nearby pillars share trunks).</summary>
        public bool EnableTreeSupports { get; init; } = true;
        /// <summary>Max XY distance between pillar bases to merge into a tree (mm).</summary>
        public float TreeMergeDistMm { get; init; } = 15f;
        /// <summary>Merge point height ratio (0-1). Lower = longer shared trunks.</summary>
        public float TreeMergeHeightRatio { get; init; } = 0.3f;
        /// <summary>Max branch angle from vertical for tree merging (degrees).</summary>
        public float TreeBranchAngleDeg { get; init; } = 35f;

        // Hollow supports
        /// <summary>Enable hollow shell geometry for tall pillars.</summary>
        public bool EnableHollowSupports { get; init; } = true;
        /// <summary>Minimum pillar height to apply hollowing (mm).</summary>
        public float HollowMinHeightMm { get; init; } = 20f;
        /// <summary>Wall thickness for hollow supports (mm).</summary>
        public float HollowWallThicknessMm { get; init; } = 0.6f;

        // Lattice bases
        /// <summary>Lattice pattern for support bases. Solid = traditional cone.</summary>
        public LatticeBase.LatticePattern BaseLatticePattern { get; init; } = LatticeBase.LatticePattern.Grid;
        /// <summary>Strut diameter for lattice bases (mm).</summary>
        public float LatticeStrutDiameterMm { get; init; } = 0.4f;
        /// <summary>Spacing between lattice struts (mm).</summary>
        public float LatticeSpacingMm { get; init; } = 1.0f;

        // Mini rafts
        /// <summary>Enable individual mini-raft pads under each support base.</summary>
        public bool EnableMiniRafts { get; init; } = true;
        /// <summary>Extra margin beyond the support base for mini rafts (mm).</summary>
        public float RaftMarginMm { get; init; } = 1.5f;
        /// <summary>Thickness of mini-raft pads (mm).</summary>
        public float RaftThicknessMm { get; init; } = 0.3f;

        // Validation
        public float MinSafetyFactor { get; init; } = 2.0f;
        public int CollisionRays { get; init; } = 8;

        // Recoater (top-down printers)
        public float RecoaterSpeedMmS { get; init; } = 0;

        // Drain hole avoidance
        public List<(System.Numerics.Vector3 position, float radiusMm)>? DrainHoleExclusions { get; init; }
        public float DrainHoleClearanceMm { get; init; } = 2.0f;

        /// <summary>
        /// Random seed for deterministic output. Same seed = same supports.
        /// Use 0 for non-deterministic (time-based seed).
        /// </summary>
        public int Seed { get; init; } = 42;

        // Model transform (from frontend viewport)
        public float TranslateX { get; init; } = 0;
        public float TranslateY { get; init; } = 0;
        public float TranslateZ { get; init; } = 0;
        public float Scale { get; init; } = 1.0f;

        // Auto-orientation
        /// <summary>Enable auto-orientation before support generation.</summary>
        public bool EnableAutoOrient { get; init; } = false;
        /// <summary>Number of candidate orientations to evaluate.</summary>
        public int AutoOrientCandidates { get; init; } = 36;

        // Drain hole analysis
        /// <summary>Enable automatic drain hole suggestion for resin traps.</summary>
        public bool EnableDrainHoleAnalysis { get; init; } = false;
        /// <summary>Drain hole diameter for resin trap analysis (mm).</summary>
        public float DrainHoleDiameterMm { get; init; } = 2.5f;
        /// <summary>Minimum trapped volume to warrant a drain hole (mm^3).</summary>
        public float DrainHoleMinTrapVolumeMm3 { get; init; } = 50f;
    }

    // ── Result ───────────────────────────────────────────────────────────

    public sealed class EngineResult
    {
        // Pipeline data
        public required AabbBvh Bvh { get; init; }
        public required List<SupportPointGenerator.SupportPoint> Points { get; init; }
        public required List<(string id, PinheadOptimizer.Pinhead pinhead)> Pinheads { get; init; }
        public required List<(string id, PillarRouter.PillarRoute route)> Routes { get; init; }
        public required List<InterconnectBuilder.Interconnection> Interconnections { get; init; }

        // Generated geometry
        public required IndexedTriangleSet SupportMesh { get; init; }
        public required MeshMerger.MergeResult MergeInfo { get; init; }

        // Validation
        public required CollisionValidator.CollisionResult CollisionResult { get; init; }
        public required StructuralValidator.StructuralResult StructuralResult { get; init; }

        // Slicing data
        public required List<AnalyticalSupportSlicer.SupportElement> SliceElements { get; init; }

        // Stats
        public required int TotalSupports { get; init; }
        public required int ValidSupports { get; init; }
        public required int RejectedCollisions { get; init; }
        public required float TotalSupportVolumeMm3 { get; init; }
        public required long TotalElapsedMs { get; init; }
        public required int SupportLayerCount { get; init; }
        public required float TotalSupportCrossSectionArea { get; init; }

        /// <summary>
        /// Centering offset applied to the mesh before support generation.
        /// Frontend must apply the same offset to align supports with the model.
        /// Print-space coordinates: (offX, offY, offZ) where Z = height.
        /// </summary>
        public required Vector3 MeshCenteringOffset { get; init; }

        // Segment data for backward compatibility with the existing frontend
        public required List<AdvancedSupportEngine.AdvancedSupport> LegacySupports { get; init; }
        public required List<AdvancedSupportEngine.CrossBrace> LegacyCrossBraces { get; init; }
    }

    // ── Main pipeline ────────────────────────────────────────────────────

    public static EngineResult Generate(StlMesh mesh, EngineConfig config)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Step 0: Apply transform and center mesh ────────────────────
        // Apply user transform first (translation + uniform scale)
        if (config.Scale != 1.0f || config.TranslateX != 0 || config.TranslateY != 0 || config.TranslateZ != 0)
        {
            mesh = mesh.Transform(
                new Vector3(config.TranslateX, config.TranslateY, config.TranslateZ),
                config.Scale);
        }
        // Center mesh: XY at origin, Z bottom at 0
        float meshW = mesh.Max.X - mesh.Min.X;
        float meshD = mesh.Max.Y - mesh.Min.Y;
        float offX = -(mesh.Min.X + meshW / 2);
        float offY = -(mesh.Min.Y + meshD / 2);
        float offZ = -mesh.Min.Z;
        mesh = mesh.Transform(new Vector3(offX, offY, offZ), 1.0f);

        // Bottom-Up specific: reduce pin radius for better surface quality
        // (thinner tips leave smaller marks on the visible surface near FEP)
        float pinRadiusScale = config.Orientation == PrinterOrientation.BottomUp ? 0.8f : 1.0f;

        // ── Step 1: Build BVH ────────────────────────────────────────────
        var stepSw = System.Diagnostics.Stopwatch.StartNew();
        var bvh = AabbBvh.Build(mesh);
        var bvhMs = stepSw.ElapsedMilliseconds;

        // ── Step 2: Generate support points ──────────────────────────────
        Serilog.Log.Information("V2 Step 1 BVH: {Ms}ms ({Tris} triangles, {Nodes} nodes)", bvhMs, bvh.TriangleCount, bvh.NodeCount);
        stepSw.Restart();

        // Adaptive layer height for large models — coarser analysis = faster
        float meshHeight = mesh.Max.Z - mesh.Min.Z;
        float adaptiveLayerHeight = config.LayerHeightMm;
        if (meshHeight > 100f) adaptiveLayerHeight = Math.Max(adaptiveLayerHeight, 3f);
        else if (meshHeight > 50f) adaptiveLayerHeight = Math.Max(adaptiveLayerHeight, 2f);

        var pointResult = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig
        {
            MinSpacingMm = config.MinSpacingMm,
            MaxSpacingMm = config.MaxSpacingMm,
            DensityFactor = config.DensityFactor,
            Orientation = config.Orientation,
            RecoaterSpeedMmS = config.RecoaterSpeedMmS,
            LayerHeightMm = adaptiveLayerHeight,
            DrainHoleExclusions = config.DrainHoleExclusions,
            DrainHoleClearanceMm = config.DrainHoleClearanceMm,
        }, bvh);

        // Cap support count for large models to prevent timeout
        int maxSupports = 500;
        if (pointResult.Points.Count > maxSupports)
        {
            Serilog.Log.Warning("V2 Step 2: Capping {Count} points to {Max}", pointResult.Points.Count, maxSupports);
            var sorted = pointResult.Points.OrderByDescending(p => p.Priority).ThenByDescending(p => p.OverhangArea).ToList();
            pointResult = new SupportPointGenerator.GenerationResult
            {
                Points = sorted.Take(maxSupports).ToList(),
                OverhangRegions = pointResult.OverhangRegions,
                OverhangRegionsAnalyzed = pointResult.OverhangRegionsAnalyzed,
                IslandsDetected = pointResult.IslandsDetected,
                TotalOverhangArea = pointResult.TotalOverhangArea,
                ElapsedMs = pointResult.ElapsedMs,
            };
        }

        Serilog.Log.Information("V2 Step 2 Points: {Ms}ms ({Count} points, {Regions} regions)", stepSw.ElapsedMilliseconds, pointResult.Points.Count, pointResult.OverhangRegionsAnalyzed);
        stepSw.Restart();

        // ── Step 3: Optimize pinheads ────────────────────────────────────
        // Adaptive collision rays: fewer for large meshes
        int adaptiveRays = mesh.TriangleCount > 3000 ? 4 : Math.Min(config.CollisionRays, 8);

        var pinheadConfig = new PinheadOptimizer.PinheadConfig
        {
            PinRadiusMm = config.PinRadiusMm * pinRadiusScale,
            BackRadiusMm = config.BackRadiusMm,
            WidthMm = config.HeadWidthMm,
            PenetrationMm = config.PenetrationMm,
            CollisionRays = adaptiveRays,
        };

        var pinheads = new List<(string id, PinheadOptimizer.Pinhead pinhead)>();
        foreach (var pt in pointResult.Points)
        {
            // Auto-scale pinhead based on structural weight recommendation
            var phCfg = pinheadConfig;
            if (pt.RecommendedWeight == ForceEstimator.SupportWeight.Heavy)
            {
                phCfg = phCfg with
                {
                    PinRadiusMm = Math.Max(phCfg.PinRadiusMm, 0.4f),
                    BackRadiusMm = Math.Max(phCfg.BackRadiusMm, 0.75f),
                    WidthMm = Math.Max(phCfg.WidthMm, 1.5f),
                };
            }
            else if (pt.RecommendedWeight == ForceEstimator.SupportWeight.Medium)
            {
                phCfg = phCfg with
                {
                    PinRadiusMm = Math.Max(phCfg.PinRadiusMm, 0.25f),
                    BackRadiusMm = Math.Max(phCfg.BackRadiusMm, 0.5f),
                };
            }

            var pinhead = PinheadOptimizer.Optimize(pt.Position, pt.Normal, bvh, phCfg);
            pinheads.Add((pt.Id, pinhead));
        }

        Serilog.Log.Information("V2 Step 3 Pinheads: {Ms}ms ({Count} optimized)", stepSw.ElapsedMilliseconds, pinheads.Count);
        stepSw.Restart();

        // ── Step 4: Route pillars ────────────────────────────────────────
        var routingConfig = new PillarRouter.RoutingConfig
        {
            BaseZ = 0,
            PillarRadiusMm = config.PillarRadiusMm,
            BaseRadiusMm = config.BaseRadiusMm,
            BaseHeightMm = config.BaseHeightMm,
            WideningFactor = config.WideningFactor,
            MaxBridgeLengthMm = config.MaxBridgeLengthMm,
            CollisionRays = config.CollisionRays,
        };

        var routes = new List<(string id, PillarRouter.PillarRoute route)>();
        // Build lookup for point weight recommendations
        var pointWeights = pointResult.Points.ToDictionary(p => p.Id, p => p.RecommendedWeight);

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;

            // Auto-scale pillar radius based on support height — ALL supports, not just heavy
            var rCfg = routingConfig;
            float supportHeight = pinhead.JunctionPoint.Z; // height above base
            var weight = pointWeights.TryGetValue(id, out var w) ? w : ForceEstimator.SupportWeight.Light;

            // Height-based auto-sizing: compute minimum radius from Euler buckling
            // P_cr = π²EI/L², I = πr⁴/4, solve for r: r = (4PL²/(π³E))^(1/4)
            // With SF=2.5 and estimated load from overhang area
            if (supportHeight > 1f)
            {
                // Estimate load from overhang area — use total nearby overhang area divided by
                // estimated number of supports in region for more realistic per-support load
                float overhangArea = 50f; // conservative default
                var pt = pointResult.Points.FirstOrDefault(p => p.Id == id);
                if (pt != null)
                {
                    // Use the point's overhang area but assume it shares with nearby supports
                    float pointArea = Math.Max(pt.OverhangArea, 20f);
                    // Approximate region coverage: each support covers spacing² area
                    float spacing = config.MinSpacingMm + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);
                    float coverageArea = spacing * spacing;
                    overhangArea = Math.Max(pointArea, coverageArea);
                }

                // Conservative load: gravity (full column) + peel force (proportional to area)
                float estLoad = overhangArea * supportHeight * 0.3f * 1.1e-6f * 9810f
                              + overhangArea * 0.02f; // higher peel coefficient for safety

                // Euler buckling minimum radius with SF=3.0 (margin for load uncertainty)
                float bucklingR = MathF.Pow(
                    4f * estLoad * 3.0f * supportHeight * supportHeight /
                    (MathF.PI * MathF.PI * MathF.PI * 2000f),
                    0.25f);

                // Minimum floor: ensures all supports handle peel + bending forces
                float linearR = 0.7f + supportHeight * 0.015f;
                float heightScaledR = Math.Max(bucklingR, linearR);

                // Weight class scaling
                if (weight == ForceEstimator.SupportWeight.Heavy)
                    heightScaledR *= 1.3f;
                else if (weight == ForceEstimator.SupportWeight.Medium)
                    heightScaledR *= 1.1f;

                rCfg = rCfg with
                {
                    PillarRadiusMm = Math.Max(rCfg.PillarRadiusMm, Math.Min(heightScaledR, 3.0f)),
                    BaseRadiusMm = Math.Max(rCfg.BaseRadiusMm, Math.Min(heightScaledR * 2.5f, 6.0f)),
                    WideningFactor = Math.Max(rCfg.WideningFactor, supportHeight > 30f ? 0.04f : 0.02f),
                };
            }

            // Start pillar at the larger of: pinhead back radius or auto-sized pillar radius
            float startRadius = Math.Max(pinhead.BackRadius, rCfg.PillarRadiusMm);
            var route = PillarRouter.Route(pinhead.JunctionPoint, startRadius, bvh, rCfg);
            routes.Add((id, route));
        }

        // Post-routing collision filter: remove routes whose pillar passes through the mesh
        // Uses both waypoint checks and segment beam-casts
        int removedByCollision = 0;
        routes = routes.Where(r =>
        {
            var path = r.route.Path;
            for (int wi = 0; wi < path.Count; wi++)
            {
                var wp = path[wi];
                if (wp.Type == "base") continue;

                // Check if waypoint is inside mesh (check ALL types except base)
                if (bvh.IsInside(wp.Position))
                {
                    removedByCollision++;
                    return false;
                }

                // Beam-cast along each segment to check for clipping
                if (wi < path.Count - 1)
                {
                    var wp2 = path[wi + 1];
                    if (wp2.Type == "base") continue;
                    float segLen = Vector3.Distance(wp.Position, wp2.Position);
                    if (segLen > 0.1f)
                    {
                        var dir = Vector3.Normalize(wp2.Position - wp.Position);
                        float radius = Math.Max(wp.Radius, wp2.Radius);
                        float clearance = bvh.BeamCast(wp.Position, dir, radius, 8, segLen);
                        if (clearance < segLen * 0.95f)
                        {
                            removedByCollision++;
                            return false;
                        }
                    }
                }
            }
            return true;
        }).ToList();

        Serilog.Log.Information("V2 Step 4 Routing: {Ms}ms ({Count} routes, {Removed} removed by collision)",
            stepSw.ElapsedMilliseconds, routes.Count, removedByCollision);
        stepSw.Restart();

        // ── Step 4b: Tree support merging ────────────────────────────────
        // Merge nearby pillars into shared trunks for material savings and rigidity
        int treeMergeCount = 0;
        if (config.EnableTreeSupports && routes.Count >= 2)
        {
            int routesBefore = routes.Count;
            routes = TreeSupportBuilder.MergeIntoTrees(routes, new TreeSupportBuilder.TreeConfig
            {
                MaxMergeDistMm = config.TreeMergeDistMm,
                MinMergeHeightRatio = config.TreeMergeHeightRatio,
                TrunkRadiusScale = 1.5f,
                BranchAngleMaxDeg = config.TreeBranchAngleDeg,
            });
            treeMergeCount = routesBefore - routes.Count(r => r.route.Path.All(wp => wp.Type != "bridge" || wp.Position.Z > 1f));
        }

        Serilog.Log.Information("V2 Step 4b TreeMerge: {Ms}ms ({Trees} trees formed)",
            stepSw.ElapsedMilliseconds, treeMergeCount);
        stepSw.Restart();

        // ── Step 5: Build interconnections ───────────────────────────────
        var interconnections = new List<InterconnectBuilder.Interconnection>();
        if (config.EnableInterconnections && routes.Count >= 2)
        {
            var pillarBases = routes.Select(r => r.route.Path.Last().Position).ToList();
            var pillarTops = routes.Select(r => r.route.Path.First().Position.Z).ToList();
            var pillarRadii = routes.Select(r => r.route.Path.First().Radius).ToList();

            interconnections = InterconnectBuilder.Build(pillarBases, pillarTops, pillarRadii, bvh,
                new InterconnectBuilder.InterconnectConfig
                {
                    MaxConnectionDistMm = config.InterconnectDistMm,
                    ConnectionIntervalMm = config.InterconnectIntervalMm,
                    StrutRadiusMm = config.StrutRadiusMm,
                });
        }

        Serilog.Log.Information("V2 Step 5 Interconnect: {Ms}ms ({Count} connections)", stepSw.ElapsedMilliseconds, interconnections.Count);
        stepSw.Restart();

        // ── Step 6: Generate meshes ──────────────────────────────────────
        // Adaptive tessellation: fewer sides when many supports to keep mesh size manageable
        int totalRoutes = routes.Count;
        int meshSides = totalRoutes > 200 ? 4 : totalRoutes > 50 ? 6 : 8;
        int braceSides = Math.Max(3, meshSides - 2);
        // Disable heavy features for large support sets to prevent mesh explosion
        bool useLattice = config.BaseLatticePattern != LatticeBase.LatticePattern.Solid && totalRoutes < 100;
        bool useHollow = config.EnableHollowSupports && totalRoutes < 150;
        bool useMiniRaft = config.EnableMiniRafts && totalRoutes < 200;

        var meshParts = new List<IndexedTriangleSet>();

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            // Pinhead mesh
            var phMesh = SupportMesher.Pinhead(pinhead.PinRadius, pinhead.BackRadius, pinhead.Width, meshSides);
            var dir = pinhead.Direction;
            var defaultDir = -Vector3.UnitY;
            Quaternion rot;
            float dot = Vector3.Dot(defaultDir, dir);
            if (dot < -0.999f) rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            else if (dot > 0.999f) rot = Quaternion.Identity;
            else
            {
                var cross = Vector3.Cross(defaultDir, dir);
                rot = Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot));
            }
            phMesh.Transform(rot, pinhead.ContactPoint);
            meshParts.Add(phMesh);
        }

        foreach (var (id, route) in routes)
        {
            float totalPillarHeight = route.Path.Count >= 2
                ? route.Path[0].Position.Z - route.Path[^1].Position.Z
                : 0;

            for (int i = 0; i < route.Path.Count - 1; i++)
            {
                var wp1 = route.Path[i];
                var wp2 = route.Path[i + 1];
                float segHeight = Vector3.Distance(wp1.Position, wp2.Position);

                // Use lattice base instead of solid pedestal for base segments
                if (wp2.Type == "base" && useLattice)
                {
                    var lattice = LatticeBase.Generate(
                        wp2.Position, wp1.Radius, wp2.Radius,
                        segHeight,
                        config.BaseLatticePattern,
                        config.LatticeStrutDiameterMm,
                        config.LatticeSpacingMm, 8);
                    meshParts.Add(lattice);
                }
                // Use hollow frustum for pillar segments when total pillar is tall enough
                else if (useHollow && totalPillarHeight > config.HollowMinHeightMm
                    && (wp1.Type == "pillar" || wp1.Type == "junction")
                    && (wp2.Type == "pillar" || wp2.Type == "junction")
                    && segHeight > 2f)
                {
                    var hollow = HollowedSupport.OrientedHollowFrustum(
                        wp1.Position, wp2.Position,
                        wp1.Radius, wp2.Radius,
                        config.HollowWallThicknessMm, 8);
                    meshParts.Add(hollow);
                }
                else
                {
                    // Standard solid frustum
                    var seg = SupportMesher.OrientedFrustum(wp1.Position, wp2.Position, wp1.Radius, wp2.Radius, meshSides);
                    meshParts.Add(seg);
                }

                // Junction sphere at each waypoint
                if (i > 0)
                {
                    var sphere = SupportMesher.OrientedSphere(wp1.Position, wp1.Radius * 0.9f, 4, 8);
                    meshParts.Add(sphere);
                }
            }

            // Mini raft under each support base
            if (useMiniRaft && route.ReachesGround && route.Path.Count > 0)
            {
                var baseWp = route.Path[^1];
                if (baseWp.Type == "base")
                {
                    var raft = MiniRaft.Generate(
                        baseWp.Position, baseWp.Radius,
                        config.RaftMarginMm, config.RaftThicknessMm, 12);
                    meshParts.Add(raft);
                }
            }
        }

        foreach (var conn in interconnections)
        {
            var strut = SupportMesher.OrientedFrustum(conn.PointA, conn.PointB, conn.Radius, conn.Radius, braceSides);
            meshParts.Add(strut);
        }

        // Skip vertex welding for speed — meshes are already clean individually.
        // Welding is only needed for watertight export, not for preview/slicing.
        var combined = new IndexedTriangleSet();
        foreach (var part in meshParts) combined.Merge(part);
        var mergeResult = new MeshMerger.MergeResult
        {
            Mesh = combined,
            OriginalVertices = combined.VertexCount,
            WeldedVertices = combined.VertexCount,
            OriginalFaces = combined.FaceCount,
            FinalFaces = combined.FaceCount,
            DegenerateFacesRemoved = 0,
            NonManifoldEdges = 0, // skip expensive check
        };

        Serilog.Log.Information("V2 Step 6 Meshing: {Ms}ms ({Verts}v {Faces}f)", stepSw.ElapsedMilliseconds, mergeResult.WeldedVertices, mergeResult.FinalFaces);
        stepSw.Restart();

        // ── Step 7: Validate (collision + structural) ──────────────────
        var routeLookup = routes.ToDictionary(r => r.id, r => r.route);

        var collisionResult = CollisionValidator.ValidateAll(
            pinheads.Where(p => p.pinhead.IsValid && routeLookup.ContainsKey(p.id)).ToList(),
            routes, interconnections, bvh);

        // Build spatial grid for coverage check
        var coverageGrid = new SpatialGrid<string>(config.MaxSpacingMm);
        foreach (var pt in pointResult.Points)
            coverageGrid.Insert(pt.Position, pt.Id);

        // Use overhang regions from point generator for coverage + load estimation
        var allRegions = pointResult.OverhangRegions;

        var pinheadLookup = pinheads.ToDictionary(p => p.id, p => p.pinhead);
        var routeData = routes.Select(r => (r.id, r.route,
            pinheadLookup.TryGetValue(r.id, out var ph) ? ph.ContactPoint.Z : r.route.Path[0].Position.Z)).ToList();

        var structuralResult = StructuralValidator.Validate(
            routeData, allRegions, coverageGrid, null, config.MinSafetyFactor,
            sourceMesh: mesh, bvh: bvh);

        Serilog.Log.Information("V2 Step 7 Validation: {Ms}ms (collisions: {Coll})", stepSw.ElapsedMilliseconds, collisionResult.CollidingSupports);
        stepSw.Restart();

        // ── Step 8: Prepare slice elements ───────────────────────────────
        var sliceElements = AnalyticalSupportSlicer.ExtractElements(
            pinheads.Where(p => p.pinhead.IsValid && routeLookup.ContainsKey(p.id))
                    .Select(p => (p.pinhead, routeLookup[p.id]))
                    .ToList(),
            interconnections);

        // ── Step 9: Build legacy format for backward-compatible frontend ─
        var legacySupports = BuildLegacySupports(pinheads, routes);
        var legacyCrossBraces = BuildLegacyCrossBraces(interconnections, routes);

        // ── Stats ────────────────────────────────────────────────────────
        int validSupports = pinheads.Count(p => p.pinhead.IsValid);
        float volume = EstimateSupportVolume(routes, interconnections);
        var supportStats = SupportSliceIntegrator.ComputeSupportStats(
            sliceElements, config.LayerHeightMm, 0, meshHeight);

        sw.Stop();
        return new EngineResult
        {
            Bvh = bvh,
            Points = pointResult.Points,
            Pinheads = pinheads,
            Routes = routes,
            Interconnections = interconnections,
            SupportMesh = mergeResult.Mesh,
            MergeInfo = mergeResult,
            CollisionResult = collisionResult,
            StructuralResult = structuralResult,
            SliceElements = sliceElements,
            TotalSupports = pinheads.Count,
            ValidSupports = validSupports,
            RejectedCollisions = collisionResult.CollidingSupports,
            TotalSupportVolumeMm3 = volume,
            TotalElapsedMs = sw.ElapsedMilliseconds,
            SupportLayerCount = supportStats.supportLayers,
            TotalSupportCrossSectionArea = supportStats.totalSupportAreaMm2,
            MeshCenteringOffset = new Vector3(offX, offY, offZ),
            LegacySupports = legacySupports,
            LegacyCrossBraces = legacyCrossBraces,
        };
    }

    // ── Legacy format builders ───────────────────────────────────────────

    private static List<AdvancedSupportEngine.AdvancedSupport> BuildLegacySupports(
        List<(string id, PinheadOptimizer.Pinhead pinhead)> pinheads,
        List<(string id, PillarRouter.PillarRoute route)> routes)
    {
        var legacyPreset = AdvancedSupportEngine.MediumPreset;
        var result = new List<AdvancedSupportEngine.AdvancedSupport>();
        var routeMap = routes.ToDictionary(r => r.id, r => r.route);

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            if (!routeMap.TryGetValue(id, out var routePath)) continue; // removed by collision filter

            var segments = new List<AdvancedSupportEngine.SupportSegment>();

            // Tip (contact → pin center)
            segments.Add(new AdvancedSupportEngine.SupportSegment
            {
                Part = "tip",
                X1 = pinhead.ContactPoint.X, Y1 = pinhead.ContactPoint.Y, Z1 = pinhead.ContactPoint.Z, R1 = 0,
                X2 = pinhead.PinCenter.X, Y2 = pinhead.PinCenter.Y, Z2 = pinhead.PinCenter.Z, R2 = pinhead.PinRadius,
            });

            // Neck (pin center → back center)
            segments.Add(new AdvancedSupportEngine.SupportSegment
            {
                Part = "neck",
                X1 = pinhead.PinCenter.X, Y1 = pinhead.PinCenter.Y, Z1 = pinhead.PinCenter.Z, R1 = pinhead.PinRadius,
                X2 = pinhead.BackCenter.X, Y2 = pinhead.BackCenter.Y, Z2 = pinhead.BackCenter.Z, R2 = pinhead.BackRadius,
            });

            // Upper taper (back center → junction)
            segments.Add(new AdvancedSupportEngine.SupportSegment
            {
                Part = "upperTaper",
                X1 = pinhead.BackCenter.X, Y1 = pinhead.BackCenter.Y, Z1 = pinhead.BackCenter.Z, R1 = pinhead.BackRadius,
                X2 = pinhead.JunctionPoint.X, Y2 = pinhead.JunctionPoint.Y, Z2 = pinhead.JunctionPoint.Z, R2 = pinhead.BackRadius,
            });

            // Route waypoints
            for (int i = 0; i < routePath.Path.Count - 1; i++)
            {
                var wp1 = routePath.Path[i];
                var wp2 = routePath.Path[i + 1];
                string part = wp2.Type == "base" ? "base" :
                              wp1.Type == "bridge" ? "branch" :
                              i == routePath.Path.Count - 2 ? "lowerTaper" : "shaft";
                segments.Add(new AdvancedSupportEngine.SupportSegment
                {
                    Part = part,
                    X1 = wp1.Position.X, Y1 = wp1.Position.Y, Z1 = wp1.Position.Z, R1 = wp1.Radius,
                    X2 = wp2.Position.X, Y2 = wp2.Position.Y, Z2 = wp2.Position.Z, R2 = wp2.Radius,
                });
            }

            result.Add(new AdvancedSupportEngine.AdvancedSupport
            {
                Id = id, Type = "v2", Preset = legacyPreset,
                ContactX = pinhead.ContactPoint.X, ContactY = pinhead.ContactPoint.Y, ContactZ = pinhead.ContactPoint.Z,
                NormalX = pinhead.Direction.X, NormalY = pinhead.Direction.Y, NormalZ = pinhead.Direction.Z,
                BaseX = routePath.Path.Last().Position.X,
                BaseY = routePath.Path.Last().Position.Y,
                BaseZ = routePath.Path.Last().Position.Z,
                Segments = segments,
            });
        }

        return result;
    }

    private static List<AdvancedSupportEngine.CrossBrace> BuildLegacyCrossBraces(
        List<InterconnectBuilder.Interconnection> interconnections,
        List<(string id, PillarRouter.PillarRoute route)> routes)
    {
        return interconnections.Select(c => new AdvancedSupportEngine.CrossBrace
        {
            SupportA = c.PillarA < routes.Count ? routes[c.PillarA].id : $"pillar-{c.PillarA}",
            SupportB = c.PillarB < routes.Count ? routes[c.PillarB].id : $"pillar-{c.PillarB}",
            X1 = c.PointA.X, Y1 = c.PointA.Y, Z1 = c.PointA.Z,
            X2 = c.PointB.X, Y2 = c.PointB.Y, Z2 = c.PointB.Z,
            Diameter = c.Radius * 2,
        }).ToList();
    }

    private static float EstimateSupportVolume(
        List<(string id, PillarRouter.PillarRoute route)> routes,
        List<InterconnectBuilder.Interconnection> interconnections)
    {
        float vol = 0;
        foreach (var (_, route) in routes)
        {
            for (int i = 0; i < route.Path.Count - 1; i++)
            {
                var wp1 = route.Path[i]; var wp2 = route.Path[i + 1];
                float h = Vector3.Distance(wp1.Position, wp2.Position);
                float r1 = wp1.Radius, r2 = wp2.Radius;
                vol += MathF.PI * h / 3f * (r1 * r1 + r1 * r2 + r2 * r2);
            }
        }
        foreach (var c in interconnections)
        {
            float h = Vector3.Distance(c.PointA, c.PointB);
            vol += MathF.PI * c.Radius * c.Radius * h;
        }
        return vol;
    }
}
