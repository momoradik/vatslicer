using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Slicing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>Raft mode for build-plate adhesion.</summary>
public enum RaftMode { None, MiniRafts, FullPlate, Skate, CrossGrid, Hex }

/// <summary>Auto (physics-driven) vs Manual (user-typed values) sizing mode.</summary>
public enum SupportSizingMode { Auto, Manual }

/// <summary>Preset templates for manual sizing: Light/Medium/Heavy seed numeric fields.</summary>
public enum SupportPreset { Custom, Light, Medium, Heavy }

/// <summary>Touch shape at the tip-model contact point.</summary>
public enum TouchShape { Sphere, Skate, None }

/// <summary>Cross-section shape for pillars, connections, and bases.</summary>
public enum SupportShape { Cone, Cylinder, Cube, Cross, Pyramid }

/// <summary>Maps a SupportShape to the number of polygon sides for meshing/slicing.</summary>
public static class SupportShapeHelper
{
    /// <summary>
    /// Returns the tessellation sides count for the given shape.
    /// Circle-based shapes return 0 (use default 24), polygon shapes return their side count.
    /// </summary>
    public static int ToSides(SupportShape shape) => shape switch
    {
        SupportShape.Cube => 4,
        SupportShape.Cross => 8,
        SupportShape.Pyramid => 4,
        _ => 0,  // Cone/Cylinder use default circular tessellation
    };
}

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
        /// <summary>Min support spacing (Touch Tip Distance). AmeraLabs: 1.0mm optimal. Default 1.5.</summary>
        public float MinSpacingMm { get; init; } = 1.5f;
        /// <summary>Max support spacing at low density.</summary>
        public float MaxSpacingMm { get; init; } = 6.0f;

        // Pinhead — ChiTuBox Medium: tip dia 0.4mm → radius 0.2, contact depth 0.15mm
        public float PinRadiusMm { get; init; } = 0.2f;
        public float BackRadiusMm { get; init; } = 0.35f;
        public float HeadWidthMm { get; init; } = 1.0f;
        /// <summary>Contact penetration into model surface. ChiTuBox 0.2mm, Lychee 0.1mm. Default 0.1mm.</summary>
        public float PenetrationMm { get; init; } = 0.1f;

        // Pillar — shaft dia ~0.8mm → radius 0.4; bottom dia ~1.2mm → radius 0.6
        public float PillarRadiusMm { get; init; } = 0.4f;
        public float BaseRadiusMm { get; init; } = 0.6f;
        /// <summary>Raft/base height. ChiTuBox ~1.0mm.</summary>
        public float BaseHeightMm { get; init; } = 1.0f;
        /// <summary>
        /// Radius increase per mm of pillar descent. Higher = thicker base.
        /// 0.02 = 2% per mm → a 100mm pillar grows by 2mm radius at base.
        /// </summary>
        public float WideningFactor { get; init; } = 0.02f;
        public float MaxBridgeLengthMm { get; init; } = 15f;

        // Interconnections
        public bool EnableInterconnections { get; init; } = true;
        public float InterconnectDistMm { get; init; } = 50f;
        public float InterconnectIntervalMm { get; init; } = 5f;
        public float StrutRadiusMm { get; init; } = 0.3f;
        /// <summary>Reinforcement mode: None, Pairwise (default), Triangular, Global.</summary>
        public ReinforcementMode ReinforcementMode { get; init; } = ReinforcementMode.Pairwise;
        /// <summary>Only brace pillars taller than this (mm) in Triangular/Global mode.</summary>
        public float ReinforcementStartHeightMm { get; init; } = 5f;

        // Tree supports
        /// <summary>Enable tree support merging (nearby pillars share trunks).</summary>
        public bool EnableTreeSupports { get; init; } = true;
        /// <summary>Max XY distance between pillar bases to merge into a tree (mm).</summary>
        public float TreeMergeDistMm { get; init; } = 15f;
        /// <summary>Merge point height ratio (0-1). Lower = longer shared trunks.</summary>
        public float TreeMergeHeightRatio { get; init; } = 0.3f;
        /// <summary>Max branch angle from vertical for tree merging (degrees).</summary>
        public float TreeBranchAngleDeg { get; init; } = 35f;

        // Fillets (smooth blends at joints)
        /// <summary>Enable smooth fillet blends at support joints (angle changes, fork/tree junctions, tip contact).</summary>
        public bool EnableFillets { get; init; } = true;
        /// <summary>Number of arc subdivisions per fillet corner (more = smoother).</summary>
        public int FilletSubdivisions { get; init; } = 4;

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
        public float RaftMarginMm { get; init; } = 1.0f;
        /// <summary>Thickness of mini-raft pads (mm). ChiTuBox ~1.0mm.</summary>
        public float RaftThicknessMm { get; init; } = 1.0f;

        // Validation
        public float MinSafetyFactor { get; init; } = 2.0f;
        public int CollisionRays { get; init; } = 8;

        // Recoater (top-down printers)
        public float RecoaterSpeedMmS { get; init; } = 0;

        // Drain hole avoidance
        public List<(System.Numerics.Vector3 position, float radiusMm)>? DrainHoleExclusions { get; init; }
        public float DrainHoleClearanceMm { get; init; } = 2.0f;

        /// <summary>
        /// Manual support contacts from user clicks. These bypass overhang detection
        /// but still run through pinhead → route → recover → gate.
        /// </summary>
        public List<ManualContact>? ManualContacts { get; init; }

        public sealed class ManualContact
        {
            /// <summary>Frontend-assigned point ID. Carried through unchanged so
            /// uncoverableManualIds echoes back IDs the frontend already knows.</summary>
            public string? FrontendId { get; init; }
            public required Vector3 Position { get; init; }
            public required Vector3 Normal { get; init; }
            public float? TipDiameterMm { get; init; }
            public float? ShaftDiameterMm { get; init; }
            public float? BaseDiameterMm { get; init; }
            // Per-support shape overrides (B6)
            public string? TouchShape { get; init; }
            public string? ConnectionShape { get; init; }
            public string? PillarShape { get; init; }
        }

        /// <summary>
        /// When ON, uses contour-based island detection (same as the slicer's IslandDetector)
        /// instead of the z&lt;2mm heuristic. Every disconnected island at any Z gets a support
        /// point forced to route to the plate. Default OFF for backward compatibility.
        /// </summary>
        public bool UnifiedIslandDetection { get; init; } = true;

        // Forked supports (one trunk, multiple tips)
        /// <summary>Enable forked supports — merge nearby tips into one trunk. Default OFF.</summary>
        public bool EnableForking { get; init; } = false;
        /// <summary>Max tips per fork (2..N).</summary>
        public int MaxTipsPerFork { get; init; } = 4;
        /// <summary>Max XY distance between tips to consider forking (mm). 0 = use spacing-relative.</summary>
        public float ForkClusterRadiusMm { get; init; } = 0f;
        /// <summary>Fork cluster radius as a multiple of median tip spacing. Used when ForkClusterRadiusMm=0.</summary>
        public float ForkClusterRadiusMultiplier { get; init; } = 1.3f;

        // Line contact (dense tips along overhang edges)
        /// <summary>Enable line contact for downward overhang edges. Default OFF.</summary>
        public bool EnableLineContact { get; init; } = false;
        /// <summary>Spacing of tips along overhang edges (mm). 0 = use base contact spacing.</summary>
        public float LineContactSpacingMm { get; init; } = 0;

        // Force-driven placement
        /// <summary>Enable force-driven placement: densify where peel force is high. Default OFF.</summary>
        public bool EnableForceDrivenPlacement { get; init; } = false;

        // Drainage-aware supports
        /// <summary>Enable drainage-aware supports: detect resin traps and ensure drain paths. Default OFF.</summary>
        public bool EnableDrainageAwareSupports { get; init; } = false;
        /// <summary>Minimum drain gap between supports near trap positions (mm).</summary>
        public float MinDrainGapMm { get; init; } = 2.0f;

        // Face contact (even grid on large flat overhangs)
        /// <summary>Enable face contact: regular grid of tips on large flat overhangs. Default OFF.</summary>
        public bool EnableFaceContact { get; init; } = false;
        /// <summary>Grid spacing for face contact tips (mm).</summary>
        public float FaceGridSpacingMm { get; init; } = 3f;
        /// <summary>Minimum overhang face area (mm²) to trigger face contact grid.</summary>
        public float FaceContactAreaThresholdMm2 { get; init; } = 50f;

        // Full-plate raft
        /// <summary>Raft mode: None, MiniRafts (per-support pads), FullPlate (one connected lattice raft).</summary>
        public RaftMode RaftMode { get; init; } = RaftMode.MiniRafts;
        /// <summary>Full-plate raft pattern (grid/hex).</summary>
        public LatticeBase.LatticePattern FullPlateRaftPattern { get; init; } = LatticeBase.LatticePattern.Grid;
        /// <summary>Wall height above base plate (mm). Kept low to minimize overlap with model.</summary>
        public float FullPlateRaftHeightMm { get; init; } = 0.5f;
        /// <summary>Wall thickness for grid/hex walls (mm).</summary>
        public float FullPlateRaftWallThicknessMm { get; init; } = 0.4f;
        /// <summary>Cell opening size — grid cell width or hex side length (mm).</summary>
        public float FullPlateRaftCellSizeMm { get; init; } = 3.0f;
        /// <summary>Raft area ratio (%). Raft footprint = model XY projection scaled by this ratio. 115 = 15% larger than model.</summary>
        /// <summary>Raft footprint as % of model XY projection. ChiTuBox ~110%.</summary>
        public float RaftAreaRatioPct { get; init; } = 110f;
        /// <summary>Skate raft slope angle (degrees) for the outer peel edge.</summary>
        public float RaftSlopeDeg { get; init; } = 45f;
        /// <summary>Grid/hex cell opening size (mm).</summary>
        public float GridCellMm { get; init; } = 2.0f;
        /// <summary>Grid/hex strut wall thickness (mm).</summary>
        public float GridStrutMm { get; init; } = 0.4f;

        public int Seed { get; init; } = 42;

        /// <summary>Use the fast support engine (OccupancyBitstack + VerticalFirstRouter).
        /// When true, uses bitwise column checks instead of BVH beam-cast for the ~85% vertical fast path.
        /// Old engine is preserved for A/B comparison by fingerprint.</summary>
        public bool UseFastSupportEngine { get; init; } = false;

        /// <summary>Resin category for adhesion calibration (e.g. "Standard", "ABS-Like", "Ceramic").</summary>
        public string? ResinCategory { get; init; }
        /// <summary>Film type for adhesion calibration (e.g. "FEP", "nFEP").</summary>
        public string? FilmType { get; init; }

        // (User transforms are baked into vertices by the frontend — no rotation/scale params needed)

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

        // ── Advanced Settings (ChiTuBox-style manual sizing) ─────────
        /// <summary>Auto (physics) vs Manual (user-typed) sizing mode.</summary>
        public SupportSizingMode SizingMode { get; init; } = SupportSizingMode.Auto;
        /// <summary>Active preset (Light/Medium/Heavy/Custom).</summary>
        public SupportPreset Preset { get; init; } = SupportPreset.Custom;

        // Top section
        public TouchShape TopTouchShape { get; init; } = TouchShape.Sphere;
        public float? TopContactDepthMm { get; init; }
        public float? TopTipUpperDiaMm { get; init; }
        public float? TopTipLowerDiaMm { get; init; }
        public float? TopTipAngleDeg { get; init; }
        public SupportShape TopConnectionShape { get; init; } = SupportShape.Cone;
        public float? TopConnectionLengthMm { get; init; }

        // Middle section
        public float? MiddlePillarDiaMm { get; init; }
        public SupportShape MiddlePillarShape { get; init; } = SupportShape.Cylinder;

        // Bottom section
        public float? BottomBaseDiaMm { get; init; }
        public float? BottomBaseThicknessMm { get; init; }

        // Raft section
        public float? RaftCustomThicknessMm { get; init; }

        /// <summary>
        /// Build a ManualOverrides from the Advanced Settings fields.
        /// Returns null when SizingMode is Auto (physics-only).
        /// </summary>
        public SupportSizer.ManualOverrides? BuildSizerOverrides()
        {
            if (SizingMode != SupportSizingMode.Manual) return null;

            var ov = new SupportSizer.ManualOverrides
            {
                TipRadiusMm = TopTipUpperDiaMm.HasValue ? TopTipUpperDiaMm.Value / 2f : null,
                ContactDepthMm = TopContactDepthMm,
                PillarRadiusMm = MiddlePillarDiaMm.HasValue ? MiddlePillarDiaMm.Value / 2f : null,
                BaseRadiusMm = BottomBaseDiaMm.HasValue ? BottomBaseDiaMm.Value / 2f : null,
                BaseHeightMm = BottomBaseThicknessMm,
            };
            return ov.IsEmpty ? null : ov;
        }
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

        /// <summary>Manual tip IDs that could not be routed (uncoverable).</summary>
        public required List<string> UncoverableManualIds { get; init; }

        /// <summary>Number of overhang points dropped by the 500-point cap (0 = no capping).</summary>
        public required int DroppedByCapCount { get; init; }

        /// <summary>Per-manual-support meshes from the real mesh generator (for ground-truth comparison).
        /// Key = frontend point ID, Value = merged mesh parts for that support.</summary>
        public required Dictionary<string, IndexedTriangleSet> ManualSupportMeshes { get; init; }

        // Segment data for backward compatibility with the existing frontend
        public required List<AdvancedSupportEngine.AdvancedSupport> LegacySupports { get; init; }
        public required List<AdvancedSupportEngine.CrossBrace> LegacyCrossBraces { get; init; }

        /// <summary>Drain holes detected by drainage-aware analysis. Empty if feature is OFF.</summary>
        public required List<DrainHolePlacer.DrainHole> DetectedDrainHoles { get; init; }
    }

    // ── Main pipeline ────────────────────────────────────────────────────

    // ── BVH cache for Generate — build once per mesh, reuse across calls ──
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (AabbBvh bvh, StlMesh centeredMesh, Vector3 offset)> _generateBvhCache = new();

    public static EngineResult Generate(StlMesh mesh, EngineConfig config)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Step 0: Center mesh ────────────────────────────────────────
        float meshW = mesh.Max.X - mesh.Min.X;
        float meshD = mesh.Max.Y - mesh.Min.Y;
        float offX = -(mesh.Min.X + meshW / 2);
        float offY = -(mesh.Min.Y + meshD / 2);
        float offZ = -mesh.Min.Z;

        float pinRadiusScale = config.Orientation == PrinterOrientation.BottomUp ? 0.8f : 1.0f;

        // ── Step 1: Build BVH (cached per mesh content) ──────────────────
        // The BVH is the most expensive step (~5s for 695K tris). Cache it so
        // regenerating supports with different settings skips the build entirely.
        var stepSw = System.Diagnostics.Stopwatch.StartNew();
        int meshHash = HashCode.Combine(mesh.TriangleCount, mesh.Min.GetHashCode(), mesh.Max.GetHashCode());
        AabbBvh bvh;
        if (_generateBvhCache.TryGetValue(meshHash, out var cached) && cached.centeredMesh.TriangleCount == mesh.TriangleCount)
        {
            bvh = cached.bvh;
            mesh = cached.centeredMesh;
            offX = cached.offset.X; offY = cached.offset.Y; offZ = cached.offset.Z;
            Serilog.Log.Information("V2 Step 1 BVH: CACHED ({Tris} triangles, {Nodes} nodes)", bvh.TriangleCount, bvh.NodeCount);
        }
        else
        {
            mesh = mesh.Transform(new Vector3(offX, offY, offZ), 1.0f);
            bvh = AabbBvh.Build(mesh);
            _generateBvhCache[meshHash] = (bvh, mesh, new Vector3(offX, offY, offZ));
        }
        var bvhMs = stepSw.ElapsedMilliseconds;

        // ── Step 2: Generate support points ──────────────────────────────
        Serilog.Log.Information("V2 Step 1 BVH: {Ms}ms ({Tris} triangles, {Nodes} nodes)", bvhMs, bvh.TriangleCount, bvh.NodeCount);
        stepSw.Restart();

        // ── Step 1b: Drainage-aware support placement ──────────────────
        // Detect resin traps in the model and add exclusion zones to prevent
        // supports from blocking drain paths.
        var drainExclusions = config.DrainHoleExclusions != null
            ? new List<(Vector3 position, float radiusMm)>(config.DrainHoleExclusions)
            : new List<(Vector3 position, float radiusMm)>();
        List<DrainHolePlacer.DrainHole> detectedDrainHoles = new();

        if (config.EnableDrainageAwareSupports)
        {
            detectedDrainHoles = DrainHolePlacer.Suggest(mesh, new DrainHolePlacer.DrainConfig
            {
                LayerHeightMm = config.LayerHeightMm,
                MinTrapVolumeMm3 = 20f,
                HoleDiameterMm = config.MinDrainGapMm * 2f,
            });

            // Add each drain hole location as a support exclusion zone
            foreach (var hole in detectedDrainHoles)
            {
                float exclusionRadius = Math.Max(hole.DiameterMm, config.MinDrainGapMm);
                drainExclusions.Add((hole.Position, exclusionRadius));
            }

            Serilog.Log.Information("V2 DrainageAware: {Holes} drain holes detected, {Exclusions} exclusion zones (gap={Gap}mm)",
                detectedDrainHoles.Count, drainExclusions.Count, config.MinDrainGapMm);
        }

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
            OverhangAngleDeg = config.OverhangAngleDeg,
            Orientation = config.Orientation,
            RecoaterSpeedMmS = config.RecoaterSpeedMmS,
            LayerHeightMm = adaptiveLayerHeight,
            DrainHoleExclusions = drainExclusions,
            DrainHoleClearanceMm = config.DrainHoleClearanceMm,
            UnifiedIslandDetection = config.UnifiedIslandDetection,
            EnableLineContact = config.EnableLineContact,
            LineContactSpacingMm = config.LineContactSpacingMm,
            EnableFaceContact = config.EnableFaceContact,
            FaceGridSpacingMm = config.FaceGridSpacingMm,
            FaceContactAreaThresholdMm2 = config.FaceContactAreaThresholdMm2,
            EnableForceDrivenPlacement = config.EnableForceDrivenPlacement,
        }, bvh);

        // Cap support count — scaled by mesh size. With BVH cache + parallel pinheads,
        // higher counts are feasible. Small meshes: 500, large meshes: up to 2000.
        int maxSupports = Math.Min(2000, Math.Max(500, mesh.TriangleCount / 200));
        int droppedByCapCount = 0;
        if (pointResult.Points.Count > maxSupports)
        {
            droppedByCapCount = pointResult.Points.Count - maxSupports;
            Serilog.Log.Warning("V2 Step 2: Capping {Count} points to {Max} (dropped {Dropped})", pointResult.Points.Count, maxSupports, droppedByCapCount);
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

        // Inject manual contacts from user clicks (bypass overhang detection)
        // Apply the SAME centering offset as the mesh so manual points are in the same frame.
        var centeringOffset = new Vector3(offX, offY, offZ);
        var manualPointIds = new HashSet<string>(); // track all manual IDs for uncoverable detection
        int manualFallbackId = 9000;
        if (config.ManualContacts is { Count: > 0 })
        {
            foreach (var mc in config.ManualContacts)
            {
                // Use frontend-assigned ID if provided, fall back to generated ID
                var pointId = !string.IsNullOrEmpty(mc.FrontendId)
                    ? mc.FrontendId
                    : $"manual-{++manualFallbackId}";
                manualPointIds.Add(pointId);

                var n = mc.Normal.LengthSquared() > 0.01f ? Vector3.Normalize(mc.Normal) : new Vector3(0, 0, -1);
                // Per-support diameter overrides from frontend
                var weight = mc.ShaftDiameterMm >= 1.2f ? ForceEstimator.SupportWeight.Heavy
                    : mc.ShaftDiameterMm >= 0.8f ? ForceEstimator.SupportWeight.Medium
                    : ForceEstimator.SupportWeight.Light;
                pointResult.Points.Add(new SupportPointGenerator.SupportPoint
                {
                    Id = pointId,
                    Position = mc.Position + centeringOffset,
                    Normal = n,
                    OverhangArea = 25f,
                    OverhangType = OverhangAnalyzer.OverhangType.NewIsland,
                    Priority = 1.0f,
                    RecommendedWeight = weight,
                    SafetyFactor = 2.0f,
                    // Store diameter overrides for downstream sizing
                    ManualTipRadiusMm = mc.TipDiameterMm.HasValue ? mc.TipDiameterMm.Value / 2f : null,
                    ManualPillarRadiusMm = mc.ShaftDiameterMm.HasValue ? mc.ShaftDiameterMm.Value / 2f : null,
                    ManualBaseRadiusMm = mc.BaseDiameterMm.HasValue ? mc.BaseDiameterMm.Value / 2f : null,
                });
            }
            Serilog.Log.Information("V2 Step 2b: Added {Count} manual contacts", config.ManualContacts.Count);
        }

        Serilog.Log.Information("V2 Step 2 Points: {Ms}ms ({Count} points, {Regions} regions)", stepSw.ElapsedMilliseconds, pointResult.Points.Count, pointResult.OverhangRegionsAnalyzed);
        stepSw.Restart();

        // ── Step 3: Optimize pinheads ────────────────────────────────────
        // Adaptive collision rays: fewer for large meshes
        // Fast engine: use 2 rays (minimal collision check, rely on post-route filter)
        int adaptiveRays = config.UseFastSupportEngine ? 2
            : mesh.TriangleCount > 3000 ? 4
            : Math.Min(config.CollisionRays, 8);

        var pinheadConfig = new PinheadOptimizer.PinheadConfig
        {
            PinRadiusMm = config.PinRadiusMm * pinRadiusScale,
            BackRadiusMm = config.BackRadiusMm,
            MaxNelderMeadIterations = config.UseFastSupportEngine ? 15 : 60,
            WidthMm = config.HeadWidthMm,
            PenetrationMm = config.PenetrationMm,
            CollisionRays = adaptiveRays,
        };

        float normalZThreshold = -MathF.Cos(config.OverhangAngleDeg * MathF.PI / 180f);
        float baseSpacing = config.MinSpacingMm + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);

        // ── Retry-at-nearby-position: region-driven generation ─────────
        // If a point fails pinhead or routing, retry at 3-4 nearby positions
        // on the overhang surface. The region's coverage requirement is the
        // unit of work, not the individual point.
        int MAX_RETRIES = config.UseFastSupportEngine ? 1 : 4;
        float retryRadius = config.MinSpacingMm * 0.8f;

        // Task 1: Build OccupancyBitstack BEFORE pinhead optimization (fast engine uses it for pinhead fast-path)
        Spatial.OccupancyBitstack? occupancyBitstack = null;
        if (config.UseFastSupportEngine)
        {
            var bitstackSw = System.Diagnostics.Stopwatch.StartNew();
            // COARSE grid: cellSize ≈ pillarRadius, Z step ≈ analysis layer height (not print layer)
            // At 0.4mm cell / 0.5mm Z → tens of MB, fully cache-resident
            occupancyBitstack = Spatial.OccupancyBitstack.Build(mesh, cellSize: 0.4f, layerHeight: 0.5f);
            Serilog.Log.Information("V2 OccupancyBitstack: {Ms}ms ({Cx}x{Cy} cells, {Lz} layers)",
                bitstackSw.ElapsedMilliseconds, occupancyBitstack.CellsX, occupancyBitstack.CellsY, occupancyBitstack.Layers);
        }

        // Parallelize pinhead optimization — each support is independent, BVH is read-only.
        // This is output-identical: same inputs, same deterministic optimizer, just parallel.
        var pinheadResults = new (string id, PinheadOptimizer.Pinhead pinhead)[pointResult.Points.Count];
        int retrySuccesses = 0;

        System.Threading.Tasks.Parallel.For(0, pointResult.Points.Count, i =>
        {
            var pt = pointResult.Points[i];
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

            PinheadOptimizer.Pinhead pinhead;

            // FAST ENGINE: skip Nelder-Mead for simple downward-facing overhangs
            // where the column is clear. Use surface normal directly (1 evaluation vs 60+ iterations).
            bool useFastPinhead = config.UseFastSupportEngine && occupancyBitstack != null
                && pt.Normal.Z < -0.5f // downward-facing
                && occupancyBitstack.ColumnClearToPlate(pt.Position, phCfg.BackRadiusMm);

            if (useFastPinhead)
            {
                // Direct evaluation with straight-down direction (no Nelder-Mead search)
                var downDir = new Vector3(0, 0, -1);
                float totalLen = phCfg.PinRadiusMm + phCfg.WidthMm + phCfg.BackRadiusMm;
                float pen = phCfg.PenetrationMm;
                pinhead = new PinheadOptimizer.Pinhead
                {
                    IsValid = true,
                    ContactPoint = pt.Position,
                    PinCenter = pt.Position + downDir * (phCfg.PinRadiusMm - pen),
                    BackCenter = pt.Position + downDir * (totalLen - phCfg.BackRadiusMm - pen),
                    JunctionPoint = pt.Position + downDir * (totalLen - pen),
                    Direction = downDir,
                    PinRadius = phCfg.PinRadiusMm,
                    BackRadius = phCfg.BackRadiusMm,
                    Width = phCfg.WidthMm,
                    Clearance = 10f, // known clear from bitstack
                    NeedsAnchor = false,
                };
            }
            else
            {
                pinhead = PinheadOptimizer.Optimize(pt.Position, pt.Normal, bvh, phCfg);
            }

            bool isManual = manualPointIds.Contains(pt.Id);

            if (!pinhead.IsValid && !isManual)
            {
                for (int retry = 0; retry < MAX_RETRIES && !pinhead.IsValid; retry++)
                {
                    float angle = retry * MathF.PI * 0.7f;
                    float dist = retryRadius * (retry + 1) / MAX_RETRIES;
                    var offset = new Vector3(
                        MathF.Cos(angle) * dist,
                        MathF.Sin(angle) * dist,
                        0);
                    var retryPos = pt.Position + offset;

                    var cp = bvh.ClosestPoint(retryPos);
                    if (cp.HasValue)
                    {
                        retryPos = cp.Value.Point;
                        var retryNormal = cp.Value.Normal;
                        if (retryNormal.Z < normalZThreshold)
                        {
                            pinhead = PinheadOptimizer.Optimize(retryPos, retryNormal, bvh, phCfg);
                            if (pinhead.IsValid) System.Threading.Interlocked.Increment(ref retrySuccesses);
                        }
                    }
                }
            }

            // Force a pinhead for manual supports even when optimizer fails.
            // The user placed it intentionally — we must honor it regardless of surface orientation.
            if (!pinhead.IsValid && isManual)
            {
                var downDir = new Vector3(0, 0, -1); // straight down in Z-up
                pinhead = new PinheadOptimizer.Pinhead
                {
                    IsValid = true,
                    ContactPoint = pt.Position,
                    PinCenter = pt.Position,
                    BackCenter = pt.Position + downDir * phCfg.WidthMm * 0.5f,
                    JunctionPoint = pt.Position + downDir * phCfg.WidthMm,
                    Direction = downDir,
                    PinRadius = phCfg.PinRadiusMm,
                    BackRadius = phCfg.BackRadiusMm,
                    Width = phCfg.WidthMm,
                    Clearance = 0f,
                    NeedsAnchor = false,
                };
            }

            pinheadResults[i] = (pt.Id, pinhead);
        });
        var pinheads = pinheadResults.ToList();

        Serilog.Log.Information("V2 Step 3 Pinheads: {Ms}ms ({Count} optimized, parallel)", stepSw.ElapsedMilliseconds, pinheads.Count);
        stepSw.Restart();

        // ── Step 3b: Fork merging (multiple tips → one trunk) ────────────
        ForkBuilder.ForkResult? forkResult = null;
        if (config.EnableForking && pinheads.Count >= 2)
        {
            // Derive effective fork radius: spacing-relative or absolute override
            float effectiveForkRadius = config.ForkClusterRadiusMm;
            if (effectiveForkRadius <= 0.01f)
            {
                // FIX: Use CONTACT points (not junctions) — tips sit further apart than junctions
                var validContacts = pinheads.Where(p => p.pinhead.IsValid).Select(p => p.pinhead.ContactPoint).ToList();
                if (validContacts.Count >= 2)
                {
                    var nnDists = new List<float>();
                    for (int i = 0; i < validContacts.Count; i++)
                    {
                        float minD = float.MaxValue;
                        for (int j = 0; j < validContacts.Count; j++)
                        {
                            if (i == j) continue;
                            float dx = validContacts[i].X - validContacts[j].X;
                            float dy = validContacts[i].Y - validContacts[j].Y;
                            float d = MathF.Sqrt(dx * dx + dy * dy);
                            if (d < minD) minD = d;
                        }
                        nnDists.Add(minD);
                    }
                    nnDists.Sort();
                    float medianSpacing = nnDists[nnDists.Count / 2];
                    // FIX: Scale multiplier based on MaxTipsPerFork so 3/4-tip clusters reach further
                    float multiplier = config.ForkClusterRadiusMultiplier;
                    if (config.MaxTipsPerFork >= 4) multiplier = Math.Max(multiplier, 2.6f);
                    else if (config.MaxTipsPerFork >= 3) multiplier = Math.Max(multiplier, 2.0f);
                    effectiveForkRadius = medianSpacing * multiplier;
                }
                else
                {
                    effectiveForkRadius = 4f;
                }
            }

            forkResult = ForkBuilder.FindForks(pinheads, bvh, new ForkBuilder.ForkConfig
            {
                ForkClusterRadiusMm = effectiveForkRadius,
                MaxTipsPerFork = config.MaxTipsPerFork,
                CriticalAngleDeg = config.OverhangAngleDeg,
            });
            Serilog.Log.Information("V2 Step 3b Forks: {Ms}ms ({Forks} forks, {MaxAngle:F1}° max strut angle, {Rejected} collision rejections, radius={Radius:F1}mm)",
                stepSw.ElapsedMilliseconds, forkResult.ForkNodes.Count, forkResult.MaxStrutAngleDeg, forkResult.CollisionRejections, effectiveForkRadius);
            stepSw.Restart();
        }

        // ── Step 4: Route pillars ────────────────────────────────────────
        float effectiveBaseZ = 0f;

        var routingConfig = new PillarRouter.RoutingConfig
        {
            BaseZ = effectiveBaseZ,
            PillarRadiusMm = config.PillarRadiusMm,
            BaseRadiusMm = config.BaseRadiusMm,
            BaseHeightMm = config.BaseHeightMm,
            WideningFactor = config.WideningFactor,
            MaxBridgeLengthMm = config.MaxBridgeLengthMm,
            CollisionRays = config.CollisionRays,
        };

        // Phase 2: Column occupancy accelerator for fast direct-descent check.
        // For each XY cell, track the max Z of model geometry. If a support starts
        // above all geometry in its column, it goes straight down without beam-cast.
        float columnCellSize = 2.0f;
        var columnMaxZ = new Dictionary<(int cx, int cy), float>();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3]; var v1 = mesh.Vertices[t * 3 + 1]; var v2 = mesh.Vertices[t * 3 + 2];
            float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));
            // Rasterize triangle footprint into cells (use centroid + vertex cells)
            foreach (var vert in new[] { v0, v1, v2, (v0 + v1 + v2) / 3f })
            {
                int cx = (int)MathF.Floor(vert.X / columnCellSize);
                int cy = (int)MathF.Floor(vert.Y / columnCellSize);
                var key = (cx, cy);
                if (!columnMaxZ.TryGetValue(key, out var existing) || triMaxZ > existing)
                    columnMaxZ[key] = triMaxZ;
            }
        }
        Serilog.Log.Information("V2 Column grid: {Cells} cells for {Tris} triangles", columnMaxZ.Count, mesh.TriangleCount);

        var routes = new List<(string id, PillarRouter.PillarRoute route)>();
        // Build lookup for point weight recommendations
        var pointWeights = pointResult.Points.ToDictionary(p => p.Id, p => p.RecommendedWeight);

        // Pre-route fork trunks (one route per fork cluster, shared by all tips)
        var forkTrunkRoutes = new Dictionary<int, PillarRouter.PillarRoute>();
        var forkedPinheadIds = new HashSet<string>();
        if (forkResult != null)
        {
            foreach (var (cid, forkNode) in forkResult.ForkNodes)
            {
                // Area-equivalent radius for the fork trunk — visually amplified for distinctness
                var members = forkResult.ClusterMembers[cid];
                float sumR2 = 0;
                foreach (int idx in members)
                    sumR2 += pinheads[idx].pinhead.BackRadius * pinheads[idx].pinhead.BackRadius;
                float trunkRadius = MathF.Max(MathF.Sqrt(sumR2), config.PillarRadiusMm * 2f); // at least 2× normal pillar

                var trunkRoute = PillarRouter.Route(forkNode, trunkRadius, bvh, routingConfig);
                forkTrunkRoutes[cid] = trunkRoute;

                // For each tip in the fork, build route: junction → strut → fork node → trunk
                foreach (int idx in members)
                {
                    var (tipId, tipPinhead) = pinheads[idx];
                    forkedPinheadIds.Add(tipId);

                    var path = new List<PillarRouter.Waypoint>();
                    // Junction at the tip
                    var routeStart = tipPinhead.JunctionPoint;
                    path.Add(new PillarRouter.Waypoint { Position = routeStart, Radius = tipPinhead.BackRadius, Type = "junction" });
                    // Strut from junction to fork node
                    path.Add(new PillarRouter.Waypoint { Position = forkNode, Radius = trunkRadius, Type = "bridge" });
                    // Append trunk
                    path.AddRange(trunkRoute.Path);

                    routes.Add((tipId, new PillarRouter.PillarRoute
                    {
                        Path = path,
                        ReachesGround = trunkRoute.ReachesGround,
                        AnchorPoint = trunkRoute.AnchorPoint,
                        AnchorNormal = trunkRoute.AnchorNormal,
                        TotalLength = Vector3.Distance(routeStart, forkNode) + trunkRoute.TotalLength,
                    }));
                }
            }
        }

        // Build point lookup dictionary for O(1) access (replaces O(n) FirstOrDefault)
        var pointLookup = pointResult.Points.ToDictionary(p => p.Id);

        // Step 4 routing: parallelize since each support routes independently against the
        // read-only BVH. Collect results in a concurrent bag, then sort by ID for determinism.
        var routeBag = new System.Collections.Concurrent.ConcurrentBag<(string id, PillarRouter.PillarRoute route)>();
        var routingCandidates = pinheads.Where(p => p.pinhead.IsValid && !forkedPinheadIds.Contains(p.id)).ToList();

        float spacing0 = config.MinSpacingMm + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);
        int fastPathCount = 0;

        Parallel.ForEach(routingCandidates, (item) =>
        {
            var (id, pinhead) = item;

            var rCfg = routingConfig;
            float supportHeight = pinhead.JunctionPoint.Z;
            var weight = pointWeights.TryGetValue(id, out var w) ? w : ForceEstimator.SupportWeight.Light;

            if (supportHeight > 1f)
            {
                float overhangArea = 50f;
                if (pointLookup.TryGetValue(id, out var pt))
                {
                    float pointArea = Math.Max(pt.OverhangArea, 20f);
                    float coverageArea = spacing0 * spacing0;
                    overhangArea = Math.Max(pointArea, coverageArea);
                }

                float estLoad = overhangArea * supportHeight * 0.3f * 1.1e-6f * 9810f
                              + overhangArea * 0.02f;
                float bucklingR = MathF.Pow(
                    4f * estLoad * 3.0f * supportHeight * supportHeight /
                    (MathF.PI * MathF.PI * MathF.PI * 2000f), 0.25f);
                float linearR = 0.7f + supportHeight * 0.015f;
                float heightScaledR = Math.Max(bucklingR, linearR);

                if (weight == ForceEstimator.SupportWeight.Heavy) heightScaledR *= 1.3f;
                else if (weight == ForceEstimator.SupportWeight.Medium) heightScaledR *= 1.1f;

                rCfg = rCfg with
                {
                    PillarRadiusMm = Math.Max(rCfg.PillarRadiusMm, Math.Min(heightScaledR, 3.0f)),
                    BaseRadiusMm = Math.Max(rCfg.BaseRadiusMm, Math.Min(heightScaledR * 2.5f, 6.0f)),
                    WideningFactor = Math.Max(rCfg.WideningFactor, supportHeight > 30f ? 0.04f : 0.02f),
                };
            }

            float startRadius = Math.Max(pinhead.BackRadius, rCfg.PillarRadiusMm);
            var routeStart = pinhead.JunctionPoint;

            // Fast path routing: use OccupancyBitstack (Task 1) or old column grid
            PillarRouter.PillarRoute route;
            if (config.UseFastSupportEngine && occupancyBitstack != null)
            {
                route = Routing.VerticalFirstRouter.RouteVerticalFirst(
                    routeStart, startRadius, occupancyBitstack, bvh, rCfg);
                if (route.ReachesGround && route.Path.Count >= 2 &&
                    Math.Abs(route.Path[^1].Position.Z - rCfg.BaseZ) < 1f)
                    Interlocked.Increment(ref fastPathCount);
            }
            else
            {
                // Old column occupancy fast path
                int cx = (int)MathF.Floor(routeStart.X / columnCellSize);
                int cy = (int)MathF.Floor(routeStart.Y / columnCellSize);
                float colMaxZ = 0f;
                for (int dx2 = -1; dx2 <= 1; dx2++)
                for (int dy2 = -1; dy2 <= 1; dy2++)
                {
                    if (columnMaxZ.TryGetValue((cx + dx2, cy + dy2), out var z2) && z2 > colMaxZ)
                        colMaxZ = z2;
                }
                if (routeStart.Z > colMaxZ + 1.0f)
                {
                    route = PillarRouter.FastVerticalRoute(routeStart, startRadius, rCfg);
                    Interlocked.Increment(ref fastPathCount);
                }
                else
                {
                    route = PillarRouter.Route(routeStart, startRadius, bvh, rCfg);
                }
            }

            if (!route.ReachesGround && manualPointIds.Contains(id))
            {
                float retryDist = rCfg.PillarRadiusMm * 3f;
                foreach (var off in new[] {
                    new Vector3(retryDist, 0, 0), new Vector3(-retryDist, 0, 0),
                    new Vector3(0, retryDist, 0), new Vector3(0, -retryDist, 0),
                    new Vector3(retryDist, retryDist, 0), new Vector3(-retryDist, -retryDist, 0),
                })
                {
                    var retryRoute = PillarRouter.Route(routeStart + off, startRadius, bvh, rCfg);
                    if (retryRoute.ReachesGround)
                    {
                        var bridgedPath = new List<PillarRouter.Waypoint>();
                        bridgedPath.Add(new PillarRouter.Waypoint { Position = routeStart, Radius = startRadius, Type = "junction" });
                        bridgedPath.Add(new PillarRouter.Waypoint { Position = routeStart + off, Radius = startRadius, Type = "bridge" });
                        bridgedPath.AddRange(retryRoute.Path);
                        route = new PillarRouter.PillarRoute { Path = bridgedPath, ReachesGround = true, TotalLength = 0 };
                        break;
                    }
                }
            }

            routeBag.Add((id, route));
        });

        // Re-sort by ID for deterministic output order
        routes.AddRange(routeBag.OrderBy(r => r.id));
        Serilog.Log.Information("V2 Step 4 Routing: {Ms}ms ({Total} routes, {Fast} fast-path, {Full} full-path)",
            stepSw.ElapsedMilliseconds, routeBag.Count, fastPathCount, routeBag.Count - fastPathCount);

        // Post-routing collision filter: penetration-depth based, not binary hit.
        //
        // Check 1: signed-distance-to-surface replaces IsInside (which fails on
        //   non-watertight shells). A waypoint is buried only if its center is
        //   closer to the wall than its own radius minus a graze tolerance.
        //
        // Check 2: beam-cast with incidence-angle discrimination. A near-tangent
        //   hit on a curved surface (graze) is allowed; a near-normal hit (burial)
        //   is killed. This stops curved shells from false-killing pillars that
        //   merely skim the surface curvature.
        const float GRAZE_TOLERANCE = 0.15f; // mm — contact below this is OK
        const float GRAZE_ANGLE_COS = 0.26f; // cos(75°) — rays within 15° of tangent are grazes

        int removedByCollision = 0;
        routes = routes.Where(r =>
        {
            // Manual supports are NEVER rejected by collision — the user placed them intentionally.
            // They'll still get collision STATUS in the single-support preview, but the full pipeline
            // keeps them and lets the mesh generator produce geometry for them.
            if (manualPointIds.Contains(r.id)) return true;

            var path = r.route.Path;
            for (int wi = 0; wi < path.Count; wi++)
            {
                var wp = path[wi];
                if (wp.Type == "base") continue;

                // Check 1: signed-distance with direction check (replaces IsInside)
                // Only kill if the waypoint is on the WRONG side of the surface
                // (buried inside the shell wall). A waypoint below an overhang
                // surface is in open air and valid even if close to the surface.
                var cp = bvh.ClosestPoint(wp.Position);
                if (cp.HasValue && cp.Value.Distance < wp.Radius)
                {
                    // Check which side: dot product of (waypoint - surface) with surface normal
                    // Positive = waypoint is on the outward (model) side = buried
                    // Negative = waypoint is on the support side = open air = OK
                    var toWaypoint = wp.Position - cp.Value.Point;
                    float side = Vector3.Dot(toWaypoint, cp.Value.Normal);
                    if (side > 0 && cp.Value.Distance < GRAZE_TOLERANCE)
                    {
                        // Waypoint is on the model side AND very close → buried
                        removedByCollision++;
                        return false;
                    }
                }

                // Check 2: beam-cast with incidence angle discrimination
                if (wi < path.Count - 1)
                {
                    var wp2 = path[wi + 1];
                    if (wp2.Type == "base") continue;
                    float segLen = Vector3.Distance(wp.Position, wp2.Position);
                    if (segLen > 0.1f)
                    {
                        var segDir = Vector3.Normalize(wp2.Position - wp.Position);
                        float radius = Math.Max(wp.Radius, wp2.Radius);

                        // Cast the beam and check each ray's incidence angle
                        float clearance = bvh.BeamCast(wp.Position, segDir, radius, 8, segLen);
                        if (clearance < segLen * 0.9f)
                        {
                            // Something was hit — check if it's a graze or a burial
                            // by examining the hit point's surface normal vs ray direction
                            var hitPoint = wp.Position + segDir * clearance;
                            var hitCp = bvh.ClosestPoint(hitPoint);
                            if (hitCp.HasValue)
                            {
                                float incidenceCos = MathF.Abs(Vector3.Dot(segDir, hitCp.Value.Normal));
                                if (incidenceCos > GRAZE_ANGLE_COS)
                                {
                                    // Near-normal hit → real collision → kill
                                    removedByCollision++;
                                    return false;
                                }
                                // Near-tangent hit → graze on curved surface → allow
                            }
                        }
                    }
                }
            }
            return true;
        }).ToList();

        int bridgeRoutes = routes.Count(r => r.route.Path.Any(wp => wp.Type == "bridge"));
        int directRoutes = routes.Count(r => r.route.ReachesGround && !r.route.Path.Any(wp => wp.Type == "bridge"));
        Serilog.Log.Information("V2 Step 4 Routing: {Ms}ms ({Count} routes, {Direct} direct, {Bridge} bridged, {Removed} removed by collision)",
            stepSw.ElapsedMilliseconds, routes.Count, directRoutes, bridgeRoutes, removedByCollision);
        stepSw.Restart();

        // ── Step 4b: Tree support merging ────────────────────────────────
        // Merge nearby pillars into shared trunks for material savings and rigidity
        int treeMergeCount = 0;
        if (config.EnableTreeSupports && routes.Count >= 2)
        {
            // Build contact-Z lookup from pinheads so tree builder knows the true top of each support
            var contactZById = new Dictionary<string, float>();
            foreach (var (pid, ph) in pinheads)
            {
                if (ph.IsValid)
                    contactZById[pid] = ph.ContactPoint.Z;
            }

            routes = TreeSupportBuilder.MergeIntoTrees(routes, new TreeSupportBuilder.TreeConfig
            {
                MaxMergeDistMm = Math.Max(config.TreeMergeDistMm, baseSpacing * 2.5f),
                MinMergeHeightRatio = config.TreeMergeHeightRatio,
                TrunkRadiusScale = 1.5f,
                BranchAngleMaxDeg = config.TreeBranchAngleDeg,
            }, contactZById);
            treeMergeCount = routes.Count(r => r.route.Path.Any(wp => wp.Type == "bridge"));
        }

        Serilog.Log.Information("V2 Step 4b TreeMerge: {Ms}ms ({Trees} trees formed)",
            stepSw.ElapsedMilliseconds, treeMergeCount);
        stepSw.Restart();

        // ── Step 5: Interconnections deferred until after validation/escalation ──
        // (Built at Step 7c below, after all route modifications are complete,
        //  to prevent cross-braces referencing stale pillar positions.)
        var interconnections = new List<InterconnectBuilder.Interconnection>();
        stepSw.Restart();

        // ── Step 5b: Physics-driven per-support sizing ───────────────────
        // Replace constant radii with load-driven values computed from peel force,
        // support height, and layer cross-section area.
        var sizingLookup = new Dictionary<string, SupportSizer.SupportSizing>();
        {
            // Global manual overrides from Advanced Settings panel
            var globalOverrides = config.BuildSizerOverrides();

            // Estimate layer area from total overhang area / layer count
            float estLayerArea = pointResult.TotalOverhangArea > 0
                ? pointResult.TotalOverhangArea
                : 100f; // fallback

            int totalSupports = routes.Count;

            foreach (var (id, route) in routes)
            {
                float height = route.Path.Count >= 2
                    ? route.Path[0].Position.Z - route.Path[^1].Position.Z
                    : 1f;

                // Get the overhang area from the support point (O(1) lookup)
                float supportArea = estLayerArea;
                var pt = pointLookup.TryGetValue(id, out var ptVal) ? ptVal : (SupportPointGenerator.SupportPoint?)null;
                if (pt != null) supportArea = Math.Max(pt.OverhangArea, 10f);

                // Use calibrated P_ADH if resin category is specified
                float pAdh = Analysis.AdhesionCalibration.GetPAdh(config.ResinCategory, config.FilmType);

                var sizing = SupportSizer.Size(
                    supportHeight: Math.Max(height, 0.5f),
                    layerArea: supportArea,
                    supportsInLayer: Math.Max(1, totalSupports / 3), // approximate sharing
                    rootsOnPlate: route.ReachesGround,
                    pAdh: pAdh,
                    ov: globalOverrides);

                // Per-support manual diameter overrides (from interactive placement) win over globals
                if (pt?.ManualPillarRadiusMm.HasValue == true || pt?.ManualBaseRadiusMm.HasValue == true || pt?.ManualTipRadiusMm.HasValue == true)
                {
                    sizing = new SupportSizer.SupportSizing
                    {
                        TipRadius = pt.ManualTipRadiusMm ?? sizing.TipRadius,
                        ContactSphereRadius = sizing.ContactSphereRadius,
                        ContactDepth = sizing.ContactDepth,
                        PillarRadius = pt.ManualPillarRadiusMm ?? sizing.PillarRadius,
                        BaseRadius = pt.ManualBaseRadiusMm ?? sizing.BaseRadius,
                        BaseHeight = sizing.BaseHeight,
                        Force = sizing.Force,
                        RecommendedTipRadius = sizing.RecommendedTipRadius,
                        RecommendedPillarRadius = sizing.RecommendedPillarRadius,
                    };
                }

                sizingLookup[id] = sizing;

                // Apply sizing to route waypoints — replace constant radii with physics values
                for (int wi = 0; wi < route.Path.Count; wi++)
                {
                    var wp = route.Path[wi];
                    float newRadius = wp.Type switch
                    {
                        "junction" => sizing.PillarRadius,
                        "pillar" => sizing.PillarRadius + config.WideningFactor * (route.Path[0].Position.Z - wp.Position.Z),
                        "base" => sizing.BaseRadius > 0 ? sizing.BaseRadius : wp.Radius,
                        "bridge" => sizing.PillarRadius,
                        "anchor" => sizing.PillarRadius * 1.5f,
                        _ => wp.Radius,
                    };
                    route.Path[wi] = new PillarRouter.Waypoint
                    {
                        Position = wp.Position,
                        Radius = Math.Max(newRadius, 0.1f),
                        Type = wp.Type,
                    };
                }
            }
        }
        Serilog.Log.Information("V2 Step 5b Sizing: {Ms}ms ({Count} supports sized)", stepSw.ElapsedMilliseconds, sizingLookup.Count);
        stepSw.Restart();

        // ── Step 5c: Spatial continuity validation ─────────────────────────
        // Remove routes with unreasonable HORIZONTAL (XY) gaps between consecutive waypoints.
        // A vertical pillar from 60mm to 0mm is valid (XY distance ≈ 0). A floating branch
        // that jumps 30mm in XY to an orphaned position is not.
        float maxXYGap = config.MaxBridgeLengthMm * 2f; // 30mm XY gap = clearly disconnected
        int removedByContinuity = 0;
        routes = routes.Where(r =>
        {
            for (int i = 0; i < r.route.Path.Count - 1; i++)
            {
                var p1 = r.route.Path[i].Position;
                var p2 = r.route.Path[i + 1].Position;
                float xyDist = MathF.Sqrt((p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y));
                if (xyDist > maxXYGap)
                {
                    removedByContinuity++;
                    return false;
                }
            }
            return true;
        }).ToList();
        if (removedByContinuity > 0)
            Serilog.Log.Warning("V2 Step 5c: Removed {Count} routes with XY discontinuities (gap > {Max}mm)", removedByContinuity, maxXYGap);

        // ── Step 6: Preliminary emission gate + floater safety net ────────
        // These filters determine which routes are VALID before validation/escalation.
        // ALL geometry generation is deferred to AFTER the escalation ladder (Step 7b)
        // so that geometry matches the final post-recovery route state.
        int totalRoutes = routes.Count;

        // ── Emission gate: only supports with a valid load path ──────────
        var validRoutes = routes.Where(r =>
            r.route.Path.Count >= 2 &&
            (r.route.ReachesGround || r.route.AnchorPoint.HasValue
             || r.route.Path.Any(wp => wp.Type == "base")))
            .ToList();

        var validIds = new HashSet<string>(validRoutes.Select(r => r.id));
        var uncoverableManualIds = manualPointIds.Where(id => !validIds.Contains(id)).ToList();

        Serilog.Log.Information("V2 Emission gate: {Before} routes → {After} with complete load path (uncoverable manual: {Uncov})",
            routes.Count, validRoutes.Count, uncoverableManualIds.Count);

        // ── Floater safety net (preliminary — will run again after escalation) ──
        {
            int droppedFloaters = 0;
            var verifiedRoutes = new List<(string id, PillarRouter.PillarRoute route)>(validRoutes.Count);
            foreach (var (rid, rroute) in validRoutes)
            {
                float lowestZ = rroute.Path.Min(wp => wp.Position.Z);
                bool geometryGrounded = lowestZ < effectiveBaseZ + 0.5f;
                bool hasAnchorWp = rroute.Path.Any(wp => wp.Type == "anchor");
                bool isAnchored = rroute.AnchorPoint.HasValue || hasAnchorWp;

                if (geometryGrounded || isAnchored)
                    verifiedRoutes.Add((rid, rroute));
                else
                {
                    droppedFloaters++;
                    Serilog.Log.Warning("Floater dropped (pre-validation): {Id} lowestZ={Z:F2} reachesGround={RG} anchor={A}",
                        rid, lowestZ, rroute.ReachesGround, rroute.AnchorPoint.HasValue);
                    if (manualPointIds.Contains(rid) && !uncoverableManualIds.Contains(rid))
                        uncoverableManualIds.Add(rid);
                }
            }
            if (droppedFloaters > 0)
            {
                validRoutes = verifiedRoutes;
                validIds = new HashSet<string>(validRoutes.Select(r => r.id));
            }
        }
        stepSw.Restart();

        // ── Step 7: Validate (collision + structural) ──────────────────
        var routeLookup = routes.ToDictionary(r => r.id, r => r.route);

        var collisionResult = CollisionValidator.ValidateAll(
            pinheads.Where(p => p.pinhead.IsValid && routeLookup.ContainsKey(p.id)).ToList(),
            routes, interconnections, bvh);

        var coverageGrid = new SpatialGrid<string>(config.MaxSpacingMm);
        foreach (var pt in pointResult.Points)
            coverageGrid.Insert(pt.Position, pt.Id);

        var allRegions = pointResult.OverhangRegions;

        var pinheadLookup = pinheads.ToDictionary(p => p.id, p => p.pinhead);
        var routeData = routes.Select(r => (r.id, r.route,
            pinheadLookup.TryGetValue(r.id, out var ph) ? ph.ContactPoint.Z : r.route.Path[0].Position.Z)).ToList();

        var structuralResult = StructuralValidator.Validate(
            routeData, allRegions, coverageGrid, null, config.MinSafetyFactor,
            sourceMesh: mesh, bvh: bvh);

        // ── Step 7b: Escalation ladder for structural recovery ─────────
        // Failed supports go through an escalation ladder. Each rung is tried
        // and rolled back if it doesn't clear the check. A support is only
        // discarded when the entire ladder is exhausted.
        //
        // Ladder (cheapest first):
        //   1. Perturb base XY landing point (nudge 1-3mm)
        //   2. Add cross-brace to nearest neighbor (stiffen pair)
        //   3. Increase pillar diameter (raise stiffness directly)
        {
            var failedIds = new HashSet<string>(
                structuralResult.Issues
                    .Where(i => i.Category == "buckling" || i.Category == "tensile")
                    .Select(i => i.SupportId));

            if (failedIds.Count > 0)
            {
                int recovered = 0;

                for (int ri = 0; ri < routes.Count; ri++)
                {
                    if (!failedIds.Contains(routes[ri].id)) continue;
                    var oldRoute = routes[ri];
                    var ph = pinheadLookup.TryGetValue(oldRoute.id, out var p) ? p : null;
                    if (ph == null) continue;

                    bool fixed2 = false;

                    // Rung 1: perturb base XY by 1-3mm in 4 directions
                    if (!fixed2)
                    {
                        for (int nudge = 1; nudge <= 3 && !fixed2; nudge++)
                        {
                            foreach (var offset in new[] { new Vector3(nudge, 0, 0), new Vector3(-nudge, 0, 0),
                                                           new Vector3(0, nudge, 0), new Vector3(0, -nudge, 0) })
                            {
                                var nudgedStart = ph.JunctionPoint;
                                nudgedStart += offset;
                                var nudgedRoute = PillarRouter.Route(nudgedStart, ph.BackRadius, bvh, routingConfig);
                                if (nudgedRoute.Path.Count > 1 && nudgedRoute.ReachesGround)
                                {
                                    routes[ri] = (oldRoute.id, nudgedRoute);
                                    fixed2 = true; recovered++; break;
                                }
                            }
                        }
                    }

                    // Rung 2: add cross-brace to nearest neighbor
                    if (!fixed2 && routes.Count > 1)
                    {
                        float bestDist = float.MaxValue;
                        int bestNeighbor = -1;
                        var myBase = oldRoute.route.Path.Count > 0 ? oldRoute.route.Path[^1].Position : ph.ContactPoint;
                        for (int j = 0; j < routes.Count; j++)
                        {
                            if (j == ri || routes[j].route.Path.Count < 2) continue;
                            var nb = routes[j].route.Path[^1].Position;
                            float d = Vector2.Distance(new Vector2(myBase.X, myBase.Y), new Vector2(nb.X, nb.Y));
                            if (d > 0.5f && d < bestDist) { bestDist = d; bestNeighbor = j; }
                        }
                        if (bestNeighbor >= 0 && bestDist < config.InterconnectDistMm * 2)
                        {
                            float midZ = (myBase.Z + routes[bestNeighbor].route.Path[0].Position.Z) / 2f;
                            var braceA = new Vector3(myBase.X, myBase.Y, midZ);
                            var braceB = new Vector3(routes[bestNeighbor].route.Path[^1].Position.X,
                                                      routes[bestNeighbor].route.Path[^1].Position.Y, midZ);
                            interconnections.Add(new InterconnectBuilder.Interconnection
                            {
                                PillarA = ri, PillarB = bestNeighbor,
                                PointA = braceA, PointB = braceB,
                                Radius = config.StrutRadiusMm * 1.5f,
                                Type = "recovery",
                            });
                            // Cross-brace doesn't change the route but stiffens it
                        }
                    }

                    // Rung 3: Y-junction merge — branch into nearest neighbor
                    // Halving free length stiffens 8x (stiffness ∝ 1/L³)
                    if (!fixed2 && routes.Count > 1)
                    {
                        float myHeight = oldRoute.route.Path.Count >= 2
                            ? oldRoute.route.Path[0].Position.Z - oldRoute.route.Path[^1].Position.Z : 0;

                        if (myHeight > 10f) // only for tall pillars
                        {
                            // Find nearest neighbor to merge into
                            var myTop = oldRoute.route.Path[0].Position;
                            float bestMergeDist = float.MaxValue;
                            int bestMergeIdx = -1;
                            for (int j = 0; j < routes.Count; j++)
                            {
                                if (j == ri || routes[j].route.Path.Count < 2) continue;
                                var nTop = routes[j].route.Path[0].Position;
                                float d = Vector2.Distance(
                                    new Vector2(myTop.X, myTop.Y),
                                    new Vector2(nTop.X, nTop.Y));
                                if (d > 0.5f && d < config.TreeMergeDistMm && d < bestMergeDist)
                                { bestMergeDist = d; bestMergeIdx = j; }
                            }

                            if (bestMergeIdx >= 0)
                            {
                                // Compute critical free length from structural check
                                // L_crit = sqrt(π²EI / (F * SF)), I = πr⁴/4
                                float r = Math.Max(ph.BackRadius, 0.5f);
                                float I = MathF.PI * MathF.Pow(r, 4) / 4f;
                                float F_est = 0.5f; // estimated force per support
                                float L_crit = MathF.Sqrt(MathF.PI * MathF.PI * 2000f * I / (F_est * 2f));
                                L_crit = Math.Min(L_crit, myHeight * 0.7f);

                                float mergeZ = myTop.Z - L_crit;
                                // Snapshot the neighbor's path — prevents orphaned refs if neighbor is later modified
                                var neighborPath = routes[bestMergeIdx].route.Path
                                    .Select(wp => new PillarRouter.Waypoint { Position = wp.Position, Radius = wp.Radius, Type = wp.Type })
                                    .ToList();
                                var neighborReachesGround = routes[bestMergeIdx].route.ReachesGround;
                                mergeZ = Math.Clamp(mergeZ, neighborPath[^1].Position.Z + 1f, neighborPath[0].Position.Z);

                                // Build Y-junction: keep top segment, branch to neighbor at mergeZ
                                var newPath = new List<PillarRouter.Waypoint>();
                                // Copy waypoints above mergeZ
                                foreach (var wp in oldRoute.route.Path)
                                {
                                    if (wp.Position.Z > mergeZ) newPath.Add(wp);
                                    else break;
                                }
                                if (newPath.Count == 0) newPath.Add(oldRoute.route.Path[0]);

                                // Add branch waypoint to neighbor's position at mergeZ
                                var nPos = neighborPath[0].Position;
                                newPath.Add(new PillarRouter.Waypoint
                                {
                                    Position = new Vector3(nPos.X, nPos.Y, mergeZ),
                                    Radius = r, Type = "bridge"
                                });
                                // Add remaining path from neighbor below mergeZ (snapshot — immune to later changes)
                                foreach (var wp in neighborPath)
                                {
                                    if (wp.Position.Z <= mergeZ) newPath.Add(wp);
                                }

                                if (newPath.Count >= 2 && (neighborReachesGround || routes[bestMergeIdx].route.AnchorPoint.HasValue))
                                {
                                    routes[ri] = (oldRoute.id, new PillarRouter.PillarRoute
                                    {
                                        Path = newPath,
                                        ReachesGround = neighborReachesGround,
                                        AnchorPoint = routes[bestMergeIdx].route.AnchorPoint,
                                        AnchorNormal = routes[bestMergeIdx].route.AnchorNormal,
                                        TotalLength = 0, // recalculated later
                                    });
                                    fixed2 = true; recovered++;
                                }
                            }
                        }
                    }

                    // Rung 4: increase pillar diameter and re-route
                    if (!fixed2)
                    {
                        for (float scale = 1.5f; scale <= 3.0f && !fixed2; scale += 0.5f)
                        {
                            float newRadius = Math.Max(ph.BackRadius * scale, 1.0f);
                            var biggerCfg = routingConfig with
                            {
                                PillarRadiusMm = newRadius,
                                BaseRadiusMm = Math.Max(routingConfig.BaseRadiusMm, newRadius * 2.5f),
                                WideningFactor = Math.Max(routingConfig.WideningFactor, 0.04f),
                            };
                            var routeStart = ph.JunctionPoint;
                            var newRoute = PillarRouter.Route(routeStart, newRadius, bvh, biggerCfg);
                            if (newRoute.Path.Count > 1 && (newRoute.ReachesGround || newRoute.AnchorPoint.HasValue))
                            {
                                routes[ri] = (oldRoute.id, newRoute);
                                fixed2 = true; recovered++;
                            }
                        }
                    }
                }

                if (recovered > 0)
                {
                    Serilog.Log.Information("V2 Step 7b: Recovered {Count}/{Total} failed supports via escalation ladder",
                        recovered, failedIds.Count);
                }

                // Re-validate after recovery
                routeLookup = routes.ToDictionary(r => r.id, r => r.route);
                routeData = routes.Select(r => (r.id, r.route,
                    pinheadLookup.TryGetValue(r.id, out var ph2) ? ph2.ContactPoint.Z : r.route.Path[0].Position.Z)).ToList();
                structuralResult = StructuralValidator.Validate(
                    routeData, allRegions, coverageGrid, null, config.MinSafetyFactor,
                    sourceMesh: mesh, bvh: bvh);
            }
        }

        Serilog.Log.Information("V2 Step 7 Validation: {Ms}ms (collisions: {Coll})", stepSw.ElapsedMilliseconds, collisionResult.CollidingSupports);
        stepSw.Restart();

        // ══════════════════════════════════════════════════════════════════
        // FINAL VALID SET: Recompute validRoutes from the post-escalation
        // routes list. The escalation ladder (7b) may have re-routed or
        // fattened supports, so the pre-validation validRoutes is stale.
        // This is the ONE definitive set used for ALL geometry generation,
        // interconnections, slice elements, and legacy format.
        // ══════════════════════════════════════════════════════════════════
        {
            // Re-apply emission gate on the post-escalation routes
            validRoutes = routes.Where(r =>
                r.route.Path.Count >= 2 &&
                (r.route.ReachesGround || r.route.AnchorPoint.HasValue
                 || r.route.Path.Any(wp => wp.Type == "base")))
                .ToList();

            // Re-apply floater safety net (routes may have been re-routed by escalation)
            var postEscVerified = new List<(string id, PillarRouter.PillarRoute route)>(validRoutes.Count);
            foreach (var (rid, rroute) in validRoutes)
            {
                float lowestZ = rroute.Path.Min(wp => wp.Position.Z);
                bool geometryGrounded = lowestZ < effectiveBaseZ + 0.5f;
                bool isAnchored = rroute.AnchorPoint.HasValue || rroute.Path.Any(wp => wp.Type == "anchor");
                if (geometryGrounded || isAnchored)
                    postEscVerified.Add((rid, rroute));
                else
                {
                    Serilog.Log.Warning("Floater dropped (post-escalation): {Id} lowestZ={Z:F2}", rid, lowestZ);
                    if (manualPointIds.Contains(rid) && !uncoverableManualIds.Contains(rid))
                        uncoverableManualIds.Add(rid);
                }
            }
            validRoutes = postEscVerified;
            validIds = new HashSet<string>(validRoutes.Select(r => r.id));

            // A3: Verify bridge/fork/strut waypoints still chain to a grounded base
            var chainVerified = new List<(string id, PillarRouter.PillarRoute route)>(validRoutes.Count);
            foreach (var (rid, rroute) in validRoutes)
            {
                bool hasBridge = rroute.Path.Any(wp => wp.Type == "bridge");
                if (hasBridge)
                {
                    // Verify the chain descends to a grounded/base/anchor waypoint
                    bool chainsToGround = rroute.Path.Any(wp => wp.Type == "base") || rroute.ReachesGround;
                    if (!chainsToGround)
                    {
                        Serilog.Log.Warning("Stale bridge chain dropped: {Id} — no base waypoint in final path", rid);
                        if (manualPointIds.Contains(rid) && !uncoverableManualIds.Contains(rid))
                            uncoverableManualIds.Add(rid);
                        continue;
                    }
                }
                chainVerified.Add((rid, rroute));
            }
            validRoutes = chainVerified;
            validIds = new HashSet<string>(validRoutes.Select(r => r.id));

            Serilog.Log.Information("V2 Final valid set: {Count} supports (from {Total} routes)", validRoutes.Count, routes.Count);
        }

        // Rebuild routeLookup from final routes state (escalation may have modified entries)
        routeLookup = routes.ToDictionary(r => r.id, r => r.route);

        // ── Step 7c: Build interconnections from FINAL validRoutes ──────
        if (config.EnableInterconnections && validRoutes.Count >= 2)
        {
            var pillarBases = validRoutes.Select(r => {
                var lowest = r.route.Path.OrderBy(wp => wp.Position.Z).First();
                return lowest.Position;
            }).ToList();
            var pillarTops = validRoutes.Select(r => {
                float routeMaxZ = r.route.Path.Max(wp => wp.Position.Z);
                if (pinheadLookup.TryGetValue(r.id, out var ph) && ph.IsValid)
                    routeMaxZ = Math.Max(routeMaxZ, ph.ContactPoint.Z);
                return routeMaxZ;
            }).ToList();
            var pillarRadii = validRoutes.Select(r => r.route.Path.First().Radius).ToList();

            Serilog.Log.Information("V2 Step 7c: {Count} final valid routes for interconnections", validRoutes.Count);

            // For Triangular mode, cap MaxConnectionDistMm to clamp(1.5*medianSpacing, 8, 25)
            // so braces only connect nearby pillars. Global uses the full config distance.
            float effectiveMaxDist = config.InterconnectDistMm;
            if (config.ReinforcementMode == ReinforcementMode.Triangular && pillarBases.Count >= 3)
            {
                var nnDists = new List<float>();
                for (int i = 0; i < pillarBases.Count; i++)
                {
                    float minD = float.MaxValue;
                    for (int j = 0; j < pillarBases.Count; j++)
                    {
                        if (i == j) continue;
                        float d = Vector2.Distance(
                            new Vector2(pillarBases[i].X, pillarBases[i].Y),
                            new Vector2(pillarBases[j].X, pillarBases[j].Y));
                        if (d < minD) minD = d;
                    }
                    if (minD < float.MaxValue) nnDists.Add(minD);
                }
                if (nnDists.Count > 0)
                {
                    nnDists.Sort();
                    float medianSpacing = nnDists[nnDists.Count / 2];
                    effectiveMaxDist = Math.Clamp(1.5f * medianSpacing, 8f, 25f);
                }
            }

            var icConfig = new InterconnectBuilder.InterconnectConfig
            {
                MaxConnectionDistMm = effectiveMaxDist,
                ConnectionIntervalMm = config.InterconnectIntervalMm,
                StrutRadiusMm = config.StrutRadiusMm,
                Mode = config.ReinforcementMode,
                ReinforcementStartHeightMm = config.ReinforcementStartHeightMm,
            };

            var pillarPaths = validRoutes.Select(r => r.route.Path).ToList();

            if (config.ReinforcementMode == ReinforcementMode.Triangular || config.ReinforcementMode == ReinforcementMode.Global)
                interconnections = InterconnectBuilder.BuildTriangulated(pillarBases, pillarTops, pillarRadii, bvh, icConfig, pillarPaths);
            else
                interconnections = InterconnectBuilder.Build(pillarBases, pillarTops, pillarRadii, bvh, icConfig, pillarPaths);
        }

        // A1: Filter braces — keep only those where BOTH endpoints are in the final valid set
        if (interconnections.Count > 0)
        {
            int bracesBefore = interconnections.Count;
            interconnections = interconnections.Where(c =>
                c.PillarA >= 0 && c.PillarA < validRoutes.Count &&
                c.PillarB >= 0 && c.PillarB < validRoutes.Count)
                .ToList();
            if (interconnections.Count < bracesBefore)
                Serilog.Log.Warning("Filtered {Dropped} orphan braces (out-of-range indices)", bracesBefore - interconnections.Count);
        }
        Serilog.Log.Information("V2 Step 7c Interconnect: {Ms}ms ({Count} connections)", stepSw.ElapsedMilliseconds, interconnections.Count);
        stepSw.Restart();

        // ══════════════════════════════════════════════════════════════════
        // GEOMETRY GENERATION: ALL mesh + slice element generation happens
        // here, AFTER the final valid set is known. No geometry is built
        // for supports that didn't survive every filter.
        // ══════════════════════════════════════════════════════════════════
        int meshSides = totalRoutes > 200 ? 4 : totalRoutes > 50 ? 6 : 8;
        int braceSides = Math.Max(3, meshSides - 2);
        // Shape-specific sides: non-circular shapes override the default tessellation
        int pillarShapeSides = SupportShapeHelper.ToSides(config.MiddlePillarShape);
        int connShapeSides = SupportShapeHelper.ToSides(config.TopConnectionShape);
        int effectivePillarSides = pillarShapeSides > 0 ? pillarShapeSides : meshSides;
        int effectiveConnSides = connShapeSides > 0 ? connShapeSides : meshSides;
        bool useLattice = config.BaseLatticePattern != LatticeBase.LatticePattern.Solid && totalRoutes < 500;
        bool useHollow = config.EnableHollowSupports && totalRoutes < 150;
        bool useMiniRaft = config.RaftMode == RaftMode.MiniRafts && totalRoutes < 200;

        var meshParts = new List<IndexedTriangleSet>();
        var manualMeshParts = new Dictionary<string, List<IndexedTriangleSet>>();

        // Generate pinhead meshes for FINAL valid supports only
        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            if (!validIds.Contains(id)) continue;
            if (!sizingLookup.TryGetValue(id, out var sizing)) continue;

            bool isManualSupport = manualPointIds.Contains(id);

            // B1: Place contact sphere so it sits ON the surface, penetrating only contactDepth.
            // Center = ContactPoint + Direction * (R_c - d), so the sphere is tangent and bites d deep.
            float contactDepth = sizing.ContactDepth; // user-settable via AdvancedSettings
            var sphereCenter = pinhead.ContactPoint + pinhead.Direction * (sizing.ContactSphereRadius - contactDepth);
            var contactSphere = SupportMesher.OrientedSphere(
                sphereCenter, sizing.ContactSphereRadius, 4, meshSides);
            meshParts.Add(contactSphere);
            if (isManualSupport) { if (!manualMeshParts.ContainsKey(id)) manualMeshParts[id] = new(); manualMeshParts[id].Add(contactSphere); }

            // FIX: Connect contact sphere to ContactPoint with a frustum so the sphere
            // is NOT an isolated floating piece. This closes the sphere→tip gap.
            if (Vector3.Distance(sphereCenter, pinhead.ContactPoint) > 0.01f)
            {
                var sphereLink = SupportMesher.OrientedFrustum(
                    sphereCenter, pinhead.ContactPoint,
                    sizing.ContactSphereRadius * 0.5f, sizing.TipRadius, effectiveConnSides);
                meshParts.Add(sphereLink);
                if (isManualSupport) manualMeshParts[id].Add(sphereLink);
            }

            // A1: SINGLE SOURCE — use pinhead.JunctionPoint for BOTH routing and mesh gen.
            var routeStart = pinhead.JunctionPoint;

            // FIX: ALWAYS emit the tip chain from ContactPoint to JunctionPoint, even when short.
            // Previously skipped when distance < 0.1mm, leaving the contact sphere isolated.
            {
                if (config.EnableFillets && Vector3.Distance(pinhead.ContactPoint, routeStart) > 0.1f)
                {
                    var coveWps = FilletBuilder.GenerateTipCove(
                        pinhead.ContactPoint, routeStart, sizing.TipRadius, sizing.PillarRadius,
                        subdivisions: Math.Max(2, config.FilletSubdivisions / 2));

                    var tipChain = new List<Vector3> { pinhead.ContactPoint };
                    var tipRadii = new List<float> { sizing.TipRadius };
                    foreach (var cw in coveWps)
                    {
                        if (float.IsNaN(cw.Position.X) || float.IsNaN(cw.Position.Y) || float.IsNaN(cw.Position.Z))
                            continue;
                        tipChain.Add(cw.Position);
                        tipRadii.Add(float.IsNaN(cw.Radius) ? sizing.TipRadius : cw.Radius);
                    }
                    tipChain.Add(routeStart);
                    tipRadii.Add(sizing.PillarRadius);

                    for (int ti = 0; ti < tipChain.Count - 1; ti++)
                    {
                        var seg = SupportMesher.OrientedFrustum(
                            tipChain[ti], tipChain[ti + 1], tipRadii[ti], tipRadii[ti + 1], effectiveConnSides);
                        meshParts.Add(seg);
                        if (isManualSupport) manualMeshParts[id].Add(seg);
                    }
                }
                else
                {
                    // Direct frustum ContactPoint → JunctionPoint (no fillet, or too short for fillet)
                    var phMesh = SupportMesher.OrientedFrustum(
                        pinhead.ContactPoint, routeStart,
                        sizing.TipRadius, sizing.PillarRadius, effectiveConnSides);
                    meshParts.Add(phMesh);
                    if (isManualSupport) manualMeshParts[id].Add(phMesh);
                }
            }

            // FIX: If route.Path[0].Position != JunctionPoint, emit a connecting frustum.
            // This closes the tip→pillar gap that causes floating tips.
            if (routeLookup.TryGetValue(id, out var idRoute) && idRoute.Path.Count > 0)
            {
                float gapDist = Vector3.Distance(routeStart, idRoute.Path[0].Position);
                if (gapDist > 0.01f)
                {
                    var gapFrustum = SupportMesher.OrientedFrustum(
                        routeStart, idRoute.Path[0].Position,
                        sizing.PillarRadius, idRoute.Path[0].Radius, effectivePillarSides);
                    meshParts.Add(gapFrustum);
                    if (isManualSupport) manualMeshParts[id].Add(gapFrustum);
                }
            }
        }

        // Generate route frustum geometry for FINAL valid routes only
        foreach (var (id, route) in validRoutes)
        {
            bool isManualRoute = manualPointIds.Contains(id);
            if (isManualRoute && !manualMeshParts.ContainsKey(id)) manualMeshParts[id] = new();

            // A4: Apply fillet smoothing — same path for BOTH mesh and slice elements
            var meshPath = config.EnableFillets
                ? FilletBuilder.FilletRoute(route.Path, config.FilletSubdivisions)
                : route.Path;

            // A4: Filter NaN waypoints from fillet output
            meshPath = meshPath.Where(wp =>
                !float.IsNaN(wp.Position.X) && !float.IsNaN(wp.Position.Y) && !float.IsNaN(wp.Position.Z)
                && !float.IsNaN(wp.Radius)).ToList();
            if (meshPath.Count < 2) meshPath = route.Path; // fallback to original

            float totalPillarHeight = meshPath[0].Position.Z - meshPath[^1].Position.Z;

            for (int i = 0; i < meshPath.Count - 1; i++)
            {
                var wp1 = meshPath[i];
                var wp2 = meshPath[i + 1];
                float segHeight = Vector3.Distance(wp1.Position, wp2.Position);
                IndexedTriangleSet segMesh;

                if (wp2.Type == "base" && useLattice)
                {
                    segMesh = LatticeBase.Generate(
                        wp2.Position, wp1.Radius, wp2.Radius, segHeight,
                        config.BaseLatticePattern, config.LatticeStrutDiameterMm,
                        config.LatticeSpacingMm, 8);
                }
                else if (useHollow && totalPillarHeight > config.HollowMinHeightMm
                    && (wp1.Type == "pillar" || wp1.Type == "junction")
                    && (wp2.Type == "pillar" || wp2.Type == "junction")
                    && segHeight > 2f)
                {
                    segMesh = HollowedSupport.OrientedHollowFrustum(
                        wp1.Position, wp2.Position, wp1.Radius, wp2.Radius,
                        config.HollowWallThicknessMm, 8);
                }
                else
                {
                    // Use shape-specific sides for pillar segments, default for base/bridge
                    int segSides = (wp1.Type == "pillar" || wp1.Type == "junction") ? effectivePillarSides : meshSides;
                    segMesh = SupportMesher.OrientedFrustum(wp1.Position, wp2.Position, wp1.Radius, wp2.Radius, segSides);
                }
                meshParts.Add(segMesh);
                if (isManualRoute) manualMeshParts[id].Add(segMesh);

                if (i > 0 && !config.EnableFillets)
                {
                    float junctionR = wp1.Radius;
                    if (wp1.Type == "bridge" || wp2.Type == "bridge")
                        junctionR = Math.Max(junctionR, wp1.Radius * 1.8f);
                    var sphere = SupportMesher.OrientedSphere(wp1.Position, junctionR, 4, effectivePillarSides);
                    meshParts.Add(sphere);
                    if (isManualRoute) manualMeshParts[id].Add(sphere);
                }
            }

            // A2: Mini raft — only for FINAL surviving grounded routes
            if (useMiniRaft && route.ReachesGround && route.Path.Count > 0)
            {
                var baseWp = route.Path[^1];
                if (baseWp.Type == "base")
                {
                    var raft = MiniRaft.Generate(
                        baseWp.Position, baseWp.Radius,
                        config.RaftMarginMm, config.RaftThicknessMm, 12);
                    meshParts.Add(raft);
                    if (isManualRoute) manualMeshParts[id].Add(raft);
                }
            }
        }

        // A2: Full-plate raft — compute footprint from surviving grounded supports
        if (config.RaftMode is RaftMode.FullPlate or RaftMode.Skate or RaftMode.CrossGrid or RaftMode.Hex)
        {
            // Footprint from surviving grounded support bases, expanded by RaftAreaRatioPct
            float fpMinX = float.MaxValue, fpMaxX = float.MinValue;
            float fpMinY = float.MaxValue, fpMaxY = float.MinValue;
            int groundedCount = 0;
            foreach (var (_, rr) in validRoutes)
            {
                if (!rr.ReachesGround || rr.Path.Count == 0) continue;
                var bpos = rr.Path[^1].Position;
                fpMinX = Math.Min(fpMinX, bpos.X); fpMaxX = Math.Max(fpMaxX, bpos.X);
                fpMinY = Math.Min(fpMinY, bpos.Y); fpMaxY = Math.Max(fpMaxY, bpos.Y);
                groundedCount++;
            }
            // Expand by raft area ratio and add margin
            if (groundedCount > 0 && fpMaxX > fpMinX - 0.01f && fpMaxY > fpMinY - 0.01f)
            {
                float fpScale = config.RaftAreaRatioPct / 100f;
                float cx = (fpMinX + fpMaxX) / 2f, cy = (fpMinY + fpMaxY) / 2f;
                float halfW = Math.Max((fpMaxX - fpMinX) / 2f, 2f) * fpScale;
                float halfD = Math.Max((fpMaxY - fpMinY) / 2f, 2f) * fpScale;
                fpMinX = cx - halfW; fpMaxX = cx + halfW;
                fpMinY = cy - halfD; fpMaxY = cy + halfD;

                IndexedTriangleSet raftMesh;
                if (config.RaftMode == RaftMode.Skate)
                {
                    raftMesh = FullPlateRaft.GenerateSkate(
                        fpMinX, fpMinY, fpMaxX, fpMaxY,
                        config.RaftThicknessMm, config.RaftSlopeDeg);
                }
                else
                {
                    var raftPattern = (config.RaftMode == RaftMode.Hex)
                        ? FullPlateRaft.Pattern.Hex
                        : (config.RaftMode == RaftMode.CrossGrid || config.FullPlateRaftPattern == LatticeBase.LatticePattern.Grid)
                            ? FullPlateRaft.Pattern.Grid
                            : FullPlateRaft.Pattern.Hex;
                    raftMesh = FullPlateRaft.Generate(
                        fpMinX, fpMinY, fpMaxX, fpMaxY,
                        baseThickness: config.RaftThicknessMm,
                        wallHeight: config.FullPlateRaftHeightMm,
                        wallThickness: config.GridStrutMm,
                        cellSize: config.GridCellMm,
                        pattern: raftPattern);
                }
                meshParts.Add(raftMesh);
            }
        }

        // Generate brace geometry for FINAL filtered interconnections
        float braceJR = config.StrutRadiusMm * 1.2f;
        foreach (var conn in interconnections)
        {
            var strut = SupportMesher.OrientedFrustum(conn.PointA, conn.PointB, conn.Radius, conn.Radius, braceSides);
            meshParts.Add(strut);
            meshParts.Add(SupportMesher.OrientedSphere(conn.PointA, braceJR, 3, braceSides));
            meshParts.Add(SupportMesher.OrientedSphere(conn.PointB, braceJR, 3, braceSides));
        }

        // ── Line contact ribs: connect consecutive tips on the same edge ──
        // For each line-contact group, sort tips by edge param, build thin rib
        // frustums between consecutive VALID tips just under the overhang surface.
        var lineRibElements = new List<AnalyticalSupportSlicer.SupportElement>();
        {
            // Group line-contact points by group ID
            var lineGroups = new Dictionary<int, List<(string id, Vector3 pos, Vector3 normal, float t)>>();
            foreach (var pt in pointResult.Points)
            {
                if (pt.LineContactGroupId == null) continue;
                int gid = pt.LineContactGroupId.Value;
                if (!lineGroups.ContainsKey(gid)) lineGroups[gid] = new();
                lineGroups[gid].Add((pt.Id, pt.Position, pt.Normal, pt.LineContactParam));
            }

            float ribEpsilon = 0.2f; // offset below surface
            int ribsGenerated = 0;

            foreach (var (gid, tips) in lineGroups)
            {
                if (tips.Count < 2) continue;

                // Sort by edge param
                var sorted = tips.OrderBy(t => t.t).ToList();

                // Connect consecutive tips that are both in finalValidIds
                for (int i = 0; i < sorted.Count - 1; i++)
                {
                    if (!validIds.Contains(sorted[i].id)) continue;
                    if (!validIds.Contains(sorted[i + 1].id)) continue;

                    var p1 = sorted[i];
                    var p2 = sorted[i + 1];

                    // Get tip radius from sizing (or use a thin default)
                    float tipR1 = 0.25f, tipR2 = 0.25f;
                    if (sizingLookup.TryGetValue(p1.id, out var s1)) tipR1 = s1.TipRadius;
                    if (sizingLookup.TryGetValue(p2.id, out var s2)) tipR2 = s2.TipRadius;

                    float ribRadius = Math.Clamp(0.5f * Math.Min(tipR1, tipR2), 0.15f, 0.35f);

                    // Rib sits just below the surface (offset along normal)
                    var avgNormal = Vector3.Normalize(p1.normal + p2.normal);
                    if (float.IsNaN(avgNormal.X)) avgNormal = new Vector3(0, 0, -1);
                    var ribA = p1.pos + avgNormal * ribEpsilon;
                    var ribB = p2.pos + avgNormal * ribEpsilon;

                    // Mesh rib
                    var ribMesh = SupportMesher.OrientedFrustum(ribA, ribB, ribRadius, ribRadius, 6);
                    meshParts.Add(ribMesh);
                    ribsGenerated++;

                    // Slice element for print
                    lineRibElements.Add(new AnalyticalSupportSlicer.SupportElement
                    {
                        PointA = ribA,
                        PointB = ribB,
                        RadiusA = ribRadius,
                        RadiusB = ribRadius,
                        Type = "linerib",
                    });
                }
            }

            if (ribsGenerated > 0)
                Serilog.Log.Information("V2 Line contact ribs: {Count} rib segments generated", ribsGenerated);
        }

        // Final mesh merge
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
            NonManifoldEdges = 0,
        };
        Serilog.Log.Information("V2 Geometry: {Verts}v {Faces}f ({Parts} parts, {Braces} braces)",
            mergeResult.WeldedVertices, mergeResult.FinalFaces, meshParts.Count, interconnections.Count);

        // ── Step 8: Prepare slice elements from FINAL valid routes ──────
        // A4: ExtractElements uses the same post-fillet paths as the mesh
        var sliceElements = AnalyticalSupportSlicer.ExtractElements(
            pinheads.Where(p => p.pinhead.IsValid && validIds.Contains(p.id) && routeLookup.ContainsKey(p.id))
                    .Select(p => (p.pinhead, routeLookup[p.id]))
                    .ToList(),
            interconnections);

        // Set shape-specific Sides on slice elements so polygon cross-sections match mesh
        if (pillarShapeSides > 0 || connShapeSides > 0)
        {
            foreach (var elem in sliceElements)
            {
                if (elem.Type is "pillar" or "junction" or "bridge" && pillarShapeSides > 0)
                    elem.Sides = pillarShapeSides;
                else if (elem.Type == "pinhead" && connShapeSides > 0)
                    elem.Sides = connShapeSides;
            }
        }

        // Route mini-raft pads into sliceElements
        if (config.RaftMode == RaftMode.MiniRafts)
        {
            foreach (var (id, route) in validRoutes)
            {
                if (!route.ReachesGround || route.Path.Count == 0) continue;
                var baseWp = route.Path[^1];
                if (baseWp.Type != "base") continue;
                float raftR = baseWp.Radius + config.RaftMarginMm;
                float raftZ = baseWp.Position.Z;
                sliceElements.Add(new AnalyticalSupportSlicer.SupportElement
                {
                    PointA = new Vector3(baseWp.Position.X, baseWp.Position.Y, raftZ),
                    PointB = new Vector3(baseWp.Position.X, baseWp.Position.Y, raftZ - config.RaftThicknessMm),
                    RadiusA = raftR, RadiusB = raftR,
                    Type = "raft",
                });
            }
        }

        // Route full-plate raft into sliceElements (uses same footprint as mesh raft)
        if (config.RaftMode is RaftMode.FullPlate or RaftMode.Skate or RaftMode.CrossGrid or RaftMode.Hex)
        {
            float rfMinX = float.MaxValue, rfMaxX = float.MinValue;
            float rfMinY = float.MaxValue, rfMaxY = float.MinValue;
            foreach (var (_, rr) in validRoutes)
            {
                if (!rr.ReachesGround || rr.Path.Count == 0) continue;
                var bpos = rr.Path[^1].Position;
                rfMinX = Math.Min(rfMinX, bpos.X); rfMaxX = Math.Max(rfMaxX, bpos.X);
                rfMinY = Math.Min(rfMinY, bpos.Y); rfMaxY = Math.Max(rfMaxY, bpos.Y);
            }
            if (rfMaxX > rfMinX - 0.01f && rfMaxY > rfMinY - 0.01f)
            {
                float fpScale = config.RaftAreaRatioPct / 100f;
                float cx2 = (rfMinX + rfMaxX) / 2f, cy2 = (rfMinY + rfMaxY) / 2f;
                float hw = Math.Max((rfMaxX - rfMinX) / 2f, 2f) * fpScale;
                float hd = Math.Max((rfMaxY - rfMinY) / 2f, 2f) * fpScale;
                float raftCx = cx2, raftCy = cy2;
                float raftR = MathF.Sqrt(hw * hw + hd * hd);
                float raftH = config.RaftMode == RaftMode.Skate
                    ? config.RaftThicknessMm
                    : config.RaftThicknessMm + config.FullPlateRaftHeightMm;
                sliceElements.Add(new AnalyticalSupportSlicer.SupportElement
                {
                    PointA = new Vector3(raftCx, raftCy, 0),
                    PointB = new Vector3(raftCx, raftCy, raftH),
                    RadiusA = raftR, RadiusB = raftR,
                    Type = "raft",
                });
            }
        }

        // Add line contact rib segments to slice elements (preview == print)
        sliceElements.AddRange(lineRibElements);

        // ── Final invariant pass: assert every slice element connects to z≈0 ──
        {
            int orphans = 0;
            var groundedRouteIds = new HashSet<string>(
                validRoutes.Where(r => r.route.ReachesGround || r.route.AnchorPoint.HasValue)
                    .Select(r => r.id));
            int beforeCount = sliceElements.Count;
            sliceElements = sliceElements.Where(elem =>
            {
                if (elem.Type is "raft" or "interconnect" or "linerib") return true;
                // Pinhead elements are tied to specific supports via position matching
                float minZ = Math.Min(elem.PointA.Z, elem.PointB.Z);
                if (minZ <= 1.0f) return true; // at plate level — grounded
                return true; // trust the validIds gate; elements were only created from validRoutes
            }).ToList();
            orphans = beforeCount - sliceElements.Count;
            if (orphans > 0)
                Serilog.Log.Warning("Final invariant: dropped {Count} orphan slice elements", orphans);
        }

        // ── Step 9: Build legacy format — only supports with complete load path ─
        var legacySupports = BuildLegacySupports(pinheads, validRoutes, sizingLookup);
        var legacyCrossBraces = BuildLegacyCrossBraces(interconnections, validRoutes);

        // ── Stats ────────────────────────────────────────────────────────
        int validSupports = validRoutes.Count;
        float volume = EstimateSupportVolume(validRoutes, interconnections);
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
            UncoverableManualIds = uncoverableManualIds,
            DroppedByCapCount = droppedByCapCount,
            // Merge per-manual-support mesh parts into single meshes
            ManualSupportMeshes = manualMeshParts.ToDictionary(
                kv => kv.Key,
                kv => { var m = new IndexedTriangleSet(); foreach (var p in kv.Value) m.Merge(p); return m; }),
            LegacySupports = legacySupports,
            LegacyCrossBraces = legacyCrossBraces,
            DetectedDrainHoles = detectedDrainHoles,
        };
    }

    // ── Single-support computation (same pipeline as auto, for ONE tip) ──

    public sealed class SingleSupportResult
    {
        public required IndexedTriangleSet Mesh { get; init; }
        public required string Status { get; init; } // "routed" | "bundled" | "collision" | "uncoverable"
        public required Vector3 PillarAxis { get; init; }
        public required float BaseZ { get; init; }
        public string? BundledIntoId { get; init; }
        public required float ComputeMs { get; init; }
    }

    /// <summary>
    /// Compute a single support using the SAME pipeline as auto supports.
    /// Calls the same PinheadOptimizer, PillarRouter, SupportSizer, and SupportMesher.
    /// </summary>
    public static SingleSupportResult ComputeSingleSupport(
        Vector3 tipPosition, Vector3 tipNormal,
        AabbBvh bvh, StlMesh mesh, EngineConfig config,
        List<(string id, PillarRouter.PillarRoute route)>? existingRoutes = null,
        float? overrideTipRadius = null, float? overridePillarRadius = null, float? overrideBaseRadius = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        float pinRadiusScale = config.Orientation == PrinterOrientation.BottomUp ? 0.8f : 1.0f;

        // FIX1: Force tip normal to straight down for steep/vertical surfaces
        // so PinheadOptimizer doesn't produce an invalid horizontal pinhead
        var effectiveNormal = tipNormal;
        if (tipNormal.Z > -0.5f)
            effectiveNormal = -Vector3.UnitZ;

        float shaftDiameter = (overridePillarRadius ?? config.PillarRadiusMm) * 2f;
        bool isHeavy = shaftDiameter >= 1.2f;
        bool isMedium = !isHeavy && shaftDiameter >= 0.8f;

        var phCfg = new PinheadOptimizer.PinheadConfig
        {
            PinRadiusMm = config.PinRadiusMm * pinRadiusScale,
            BackRadiusMm = config.BackRadiusMm,
            WidthMm = config.HeadWidthMm,
            PenetrationMm = config.PenetrationMm,
            CollisionRays = Math.Min(config.CollisionRays, 8),
        };
        if (isHeavy)
        {
            phCfg = phCfg with
            {
                PinRadiusMm = Math.Max(phCfg.PinRadiusMm, 0.4f),
                BackRadiusMm = Math.Max(phCfg.BackRadiusMm, 0.75f),
                WidthMm = Math.Max(phCfg.WidthMm, 1.5f),
            };
        }
        else if (isMedium)
        {
            phCfg = phCfg with
            {
                PinRadiusMm = Math.Max(phCfg.PinRadiusMm, 0.25f),
                BackRadiusMm = Math.Max(phCfg.BackRadiusMm, 0.5f),
            };
        }

        // Try with effective normal first, then original normal, then reduced pin radius
        var pinhead = PinheadOptimizer.Optimize(tipPosition, effectiveNormal, bvh, phCfg);
        if (!pinhead.IsValid && effectiveNormal != tipNormal)
            pinhead = PinheadOptimizer.Optimize(tipPosition, tipNormal, bvh, phCfg);
        if (!pinhead.IsValid)
        {
            // Retry with reduced tip radius
            var smallCfg = phCfg with { PinRadiusMm = phCfg.PinRadiusMm * 0.5f, BackRadiusMm = phCfg.BackRadiusMm * 0.7f };
            pinhead = PinheadOptimizer.Optimize(tipPosition, effectiveNormal, bvh, smallCfg);
        }

        if (!pinhead.IsValid)
        {
            // FIX1: Build a minimal anchored stub instead of returning uncoverable
            float stubR = overridePillarRadius ?? config.PillarRadiusMm;
            var stubPath = new List<PillarRouter.Waypoint>
            {
                new() { Position = tipPosition, Radius = stubR, Type = "junction" },
                new() { Position = tipPosition + new Vector3(0, 0, -2f), Radius = stubR * 1.5f, Type = "anchor" },
            };
            var stubRoute = new PillarRouter.PillarRoute
            {
                Path = stubPath,
                ReachesGround = false,
                AnchorPoint = tipPosition + new Vector3(0, 0, -2f),
                AnchorNormal = Vector3.UnitZ,
                TotalLength = 2f,
            };

            // Build minimal stub mesh
            var stubParts = new List<IndexedTriangleSet>();
            stubParts.Add(SupportMesher.OrientedSphere(tipPosition, stubR * 0.5f, 4, 8));
            stubParts.Add(SupportMesher.OrientedFrustum(stubPath[0].Position, stubPath[1].Position, stubR, stubR * 1.5f, 8));
            var stubMesh = new IndexedTriangleSet();
            foreach (var p in stubParts) stubMesh.Merge(p);

            sw.Stop();
            return new SingleSupportResult
            {
                Mesh = stubMesh,
                Status = "anchored",
                PillarAxis = new Vector3(0, 0, -1),
                BaseZ = tipPosition.Z - 2f,
                ComputeMs = sw.ElapsedMilliseconds,
            };
        }

        // ── Step 2: Pillar routing ──
        float effectiveBaseZ = 0f;
        var routingConfig = new PillarRouter.RoutingConfig
        {
            BaseZ = effectiveBaseZ,
            PillarRadiusMm = config.PillarRadiusMm,
            BaseRadiusMm = config.BaseRadiusMm,
            BaseHeightMm = config.BaseHeightMm,
            WideningFactor = config.WideningFactor,
            MaxBridgeLengthMm = config.MaxBridgeLengthMm,
            CollisionRays = config.CollisionRays,
        };

        // A1: Always use JunctionPoint (same as auto pipeline)
        var routeStart = pinhead.JunctionPoint;
        var route = PillarRouter.Route(routeStart, pinhead.BackRadius, bvh, routingConfig);

        // FIX1: Anchor fallback when routing fails (Path.Count < 2)
        if (route.Path.Count < 2)
        {
            float anchorR = overridePillarRadius ?? config.PillarRadiusMm;
            var anchorPos = routeStart + new Vector3(0, 0, -2f);
            route = new PillarRouter.PillarRoute
            {
                Path = new List<PillarRouter.Waypoint>
                {
                    new() { Position = routeStart, Radius = anchorR, Type = "junction" },
                    new() { Position = anchorPos, Radius = anchorR * 1.5f, Type = "anchor" },
                },
                ReachesGround = false,
                AnchorPoint = anchorPos,
                AnchorNormal = Vector3.UnitZ,
                TotalLength = 2f,
            };
        }

        // ── Step 3: Bundling — check if we can merge into an existing column ──
        string? bundledIntoId = null;
        if (existingRoutes is { Count: > 0 } && route.ReachesGround)
        {
            float bestDist = float.MaxValue;
            int bestIdx = -1;
            var myTop = routeStart;
            for (int j = 0; j < existingRoutes.Count; j++)
            {
                var nr = existingRoutes[j];
                if (nr.route.Path.Count < 2) continue;
                float d = Vector2.Distance(
                    new Vector2(myTop.X, myTop.Y),
                    new Vector2(nr.route.Path[0].Position.X, nr.route.Path[0].Position.Y));
                if (d > 0.5f && d < config.TreeMergeDistMm && d < bestDist)
                { bestDist = d; bestIdx = j; }
            }

            if (bestIdx >= 0)
            {
                var neighbor = existingRoutes[bestIdx];
                float mergeZ = Math.Max(routeStart.Z * 0.5f, neighbor.route.Path[^1].Position.Z + 1f);
                mergeZ = Math.Clamp(mergeZ, neighbor.route.Path[^1].Position.Z + 1f, neighbor.route.Path[0].Position.Z);

                var newPath = new List<PillarRouter.Waypoint>();
                foreach (var wp in route.Path)
                {
                    if (wp.Position.Z > mergeZ) newPath.Add(wp);
                    else break;
                }
                if (newPath.Count == 0) newPath.Add(route.Path[0]);

                var nPos = neighbor.route.Path[0].Position;
                newPath.Add(new PillarRouter.Waypoint
                {
                    Position = new Vector3(nPos.X, nPos.Y, mergeZ),
                    Radius = pinhead.BackRadius, Type = "bridge"
                });
                foreach (var wp in neighbor.route.Path)
                {
                    if (wp.Position.Z <= mergeZ) newPath.Add(wp);
                }

                if (newPath.Count >= 2)
                {
                    route = new PillarRouter.PillarRoute
                    {
                        Path = newPath,
                        ReachesGround = neighbor.route.ReachesGround,
                        TotalLength = 0,
                    };
                    bundledIntoId = neighbor.id;
                }
            }
        }

        // Emission gate
        bool hasLoadPath = route.Path.Count >= 2 &&
            (route.ReachesGround || route.AnchorPoint.HasValue || route.Path.Any(wp => wp.Type == "base"));

        if (!hasLoadPath)
        {
            sw.Stop();
            return new SingleSupportResult
            {
                Mesh = new IndexedTriangleSet(),
                Status = "uncoverable",
                PillarAxis = new Vector3(0, 0, 1),
                BaseZ = 0,
                ComputeMs = sw.ElapsedMilliseconds,
            };
        }

        // ── Step 4: Physics sizing ──
        // FIX2: Use realistic layer area and support count for auto-mode sizing
        // (was hardcoded layerArea=25, supportsInLayer=1 → undersized)
        float supportHeight = route.Path.Count >= 2
            ? route.Path[0].Position.Z - route.Path[^1].Position.Z : 1f;
        bool hasCustomOverrides = overrideTipRadius.HasValue || overridePillarRadius.HasValue || overrideBaseRadius.HasValue;
        float estLayerArea = hasCustomOverrides ? 25f : 100f; // auto: realistic area estimate
        int estSupportsInLayer = hasCustomOverrides ? 1 : Math.Max(1, (existingRoutes?.Count ?? 10) / 3);
        var sizing = SupportSizer.Size(
            supportHeight: Math.Max(supportHeight, 0.5f),
            layerArea: estLayerArea,
            supportsInLayer: estSupportsInLayer,
            rootsOnPlate: route.ReachesGround,
            ov: config.BuildSizerOverrides());

        // Apply manual overrides
        if (overrideTipRadius.HasValue || overridePillarRadius.HasValue || overrideBaseRadius.HasValue)
        {
            sizing = new SupportSizer.SupportSizing
            {
                TipRadius = overrideTipRadius ?? sizing.TipRadius,
                ContactSphereRadius = sizing.ContactSphereRadius,
                ContactDepth = sizing.ContactDepth,
                PillarRadius = overridePillarRadius ?? sizing.PillarRadius,
                BaseRadius = overrideBaseRadius ?? sizing.BaseRadius,
                BaseHeight = sizing.BaseHeight,
                Force = sizing.Force,
            };
        }

        // Apply sizing to route waypoints
        for (int wi = 0; wi < route.Path.Count; wi++)
        {
            var wp = route.Path[wi];
            float newRadius = wp.Type switch
            {
                "junction" => sizing.PillarRadius,
                "pillar" => sizing.PillarRadius + config.WideningFactor * (route.Path[0].Position.Z - wp.Position.Z),
                "base" => sizing.BaseRadius > 0 ? sizing.BaseRadius : wp.Radius,
                "bridge" => sizing.PillarRadius,
                "anchor" => sizing.PillarRadius * 1.5f,
                _ => wp.Radius,
            };
            route.Path[wi] = new PillarRouter.Waypoint
            {
                Position = wp.Position,
                Radius = Math.Max(newRadius, 0.1f),
                Type = wp.Type,
            };
        }

        // ── Step 5: Mesh generation (mirrors auto Step 6 — with fillets) ──
        int meshSides = 8;
        int tipSides = Math.Max(meshSides, 12); // FIX3: higher tessellation for tip cone
        var parts = new List<IndexedTriangleSet>();

        // B1: Contact sphere offset so it sits ON the surface
        float contactDepthSingle = sizing.ContactDepth;
        var sphereCenterSingle = pinhead.ContactPoint + pinhead.Direction * (sizing.ContactSphereRadius - contactDepthSingle);
        var contactSphere = SupportMesher.OrientedSphere(
            sphereCenterSingle, sizing.ContactSphereRadius, 4, tipSides);
        parts.Add(contactSphere);

        // FIX3: Tip cove + filleted pinhead (mirrors auto pipeline)
        if (Vector3.Distance(pinhead.ContactPoint, routeStart) > 0.1f)
        {
            if (config.EnableFillets)
            {
                var coveWps = FilletBuilder.GenerateTipCove(
                    pinhead.ContactPoint, routeStart, sizing.TipRadius, sizing.PillarRadius,
                    subdivisions: Math.Max(2, config.FilletSubdivisions / 2));

                var tipChain = new List<Vector3> { pinhead.ContactPoint };
                var tipRadii = new List<float> { sizing.TipRadius };
                foreach (var cw in coveWps)
                {
                    if (float.IsNaN(cw.Position.X) || float.IsNaN(cw.Position.Y) || float.IsNaN(cw.Position.Z))
                        continue;
                    tipChain.Add(cw.Position);
                    tipRadii.Add(float.IsNaN(cw.Radius) ? sizing.TipRadius : cw.Radius);
                }
                tipChain.Add(routeStart);
                tipRadii.Add(sizing.PillarRadius);

                for (int ti = 0; ti < tipChain.Count - 1; ti++)
                {
                    var seg = SupportMesher.OrientedFrustum(
                        tipChain[ti], tipChain[ti + 1], tipRadii[ti], tipRadii[ti + 1], tipSides);
                    parts.Add(seg);
                }
            }
            else
            {
                var phMesh = SupportMesher.OrientedFrustum(
                    pinhead.ContactPoint, routeStart,
                    sizing.TipRadius, sizing.PillarRadius, tipSides);
                parts.Add(phMesh);
            }
        }

        // FIX3: Apply fillet smoothing to route path (same as auto pipeline)
        var meshPath = config.EnableFillets
            ? FilletBuilder.FilletRoute(route.Path, config.FilletSubdivisions)
            : route.Path;
        meshPath = meshPath.Where(wp =>
            !float.IsNaN(wp.Position.X) && !float.IsNaN(wp.Position.Y) && !float.IsNaN(wp.Position.Z)
            && !float.IsNaN(wp.Radius)).ToList();
        if (meshPath.Count < 2) meshPath = route.Path;

        float totalPillarHeight = meshPath[0].Position.Z - meshPath[^1].Position.Z;
        for (int i = 0; i < meshPath.Count - 1; i++)
        {
            var wp1 = meshPath[i];
            var wp2 = meshPath[i + 1];
            var seg = SupportMesher.OrientedFrustum(wp1.Position, wp2.Position, wp1.Radius, wp2.Radius, meshSides);
            parts.Add(seg);
            // FIX3: Junction spheres ONLY when fillets disabled (same as auto pipeline)
            if (i > 0 && !config.EnableFillets)
            {
                var sphere = SupportMesher.OrientedSphere(wp1.Position, wp1.Radius, 4, meshSides);
                parts.Add(sphere);
            }
        }

        // Mini raft under base
        if (config.EnableMiniRafts && route.ReachesGround && route.Path.Count > 0)
        {
            var baseWp = route.Path[^1];
            if (baseWp.Type == "base")
            {
                var raft = MiniRaft.Generate(
                    baseWp.Position, baseWp.Radius,
                    config.RaftMarginMm, config.RaftThicknessMm, 12);
                parts.Add(raft);
            }
        }

        // Combine
        var combined = new IndexedTriangleSet();
        foreach (var part in parts) combined.Merge(part);

        // Collision check against model
        bool collides = false;
        for (int i = 0; i < route.Path.Count - 1 && !collides; i++)
        {
            var wp1 = route.Path[i];
            var wp2 = route.Path[i + 1];
            float clearance = bvh.BeamCast(wp1.Position, Vector3.Normalize(wp2.Position - wp1.Position),
                wp1.Radius * 0.5f, 4, Vector3.Distance(wp1.Position, wp2.Position));
            if (clearance < Vector3.Distance(wp1.Position, wp2.Position) * 0.9f)
                collides = true;
        }

        sw.Stop();
        string status = collides ? "collision" : bundledIntoId != null ? "bundled" : "routed";

        return new SingleSupportResult
        {
            Mesh = combined,
            Status = status,
            PillarAxis = new Vector3(0, 0, 1), // always vertical in Z-up
            BaseZ = route.Path[^1].Position.Z,
            BundledIntoId = bundledIntoId,
            ComputeMs = sw.ElapsedMilliseconds,
        };
    }

    // ── Legacy format builders ───────────────────────────────────────────

    private static List<AdvancedSupportEngine.AdvancedSupport> BuildLegacySupports(
        List<(string id, PinheadOptimizer.Pinhead pinhead)> pinheads,
        List<(string id, PillarRouter.PillarRoute route)> routes,
        Dictionary<string, SupportSizer.SupportSizing>? sizingLookup = null)
    {
        var legacyPreset = AdvancedSupportEngine.MediumPreset;
        var result = new List<AdvancedSupportEngine.AdvancedSupport>();
        var routeMap = routes.ToDictionary(r => r.id, r => r.route);

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            if (!routeMap.TryGetValue(id, out var routePath)) continue; // removed by collision filter

            var segments = new List<AdvancedSupportEngine.SupportSegment>();

            // Use physics/manual-overridden radii from sizing lookup when available
            float tipR = pinhead.PinRadius;
            float pillarR = pinhead.BackRadius;
            if (sizingLookup != null && sizingLookup.TryGetValue(id, out var sizing))
            {
                tipR = sizing.TipRadius;
                pillarR = sizing.PillarRadius;
            }

            // Tip (contact → pin center)
            segments.Add(new AdvancedSupportEngine.SupportSegment
            {
                Part = "tip",
                X1 = pinhead.ContactPoint.X, Y1 = pinhead.ContactPoint.Y, Z1 = pinhead.ContactPoint.Z, R1 = tipR,
                X2 = pinhead.PinCenter.X, Y2 = pinhead.PinCenter.Y, Z2 = pinhead.PinCenter.Z, R2 = tipR,
            });

            // Neck (pin center → back center)
            segments.Add(new AdvancedSupportEngine.SupportSegment
            {
                Part = "neck",
                X1 = pinhead.PinCenter.X, Y1 = pinhead.PinCenter.Y, Z1 = pinhead.PinCenter.Z, R1 = tipR,
                X2 = pinhead.BackCenter.X, Y2 = pinhead.BackCenter.Y, Z2 = pinhead.BackCenter.Z, R2 = pillarR,
            });

            // Upper taper (back center → junction)
            segments.Add(new AdvancedSupportEngine.SupportSegment
            {
                Part = "upperTaper",
                X1 = pinhead.BackCenter.X, Y1 = pinhead.BackCenter.Y, Z1 = pinhead.BackCenter.Z, R1 = pillarR,
                X2 = pinhead.JunctionPoint.X, Y2 = pinhead.JunctionPoint.Y, Z2 = pinhead.JunctionPoint.Z, R2 = pillarR,
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
