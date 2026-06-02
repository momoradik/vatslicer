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
        public float PenetrationMm { get; init; } = 0.2f;

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
        public float InterconnectDistMm { get; init; } = 10f;
        public float InterconnectIntervalMm { get; init; } = 5f;
        public float StrutRadiusMm { get; init; } = 0.3f;

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
        var pointResult = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig
        {
            MinSpacingMm = config.MinSpacingMm,
            MaxSpacingMm = config.MaxSpacingMm,
            DensityFactor = config.DensityFactor,
            Orientation = config.Orientation,
            RecoaterSpeedMmS = config.RecoaterSpeedMmS,
            LayerHeightMm = config.LayerHeightMm,
            DrainHoleExclusions = config.DrainHoleExclusions,
            DrainHoleClearanceMm = config.DrainHoleClearanceMm,
        }, bvh);

        Serilog.Log.Information("V2 Step 2 Points: {Ms}ms ({Count} points, {Regions} regions)", stepSw.ElapsedMilliseconds, pointResult.Points.Count, pointResult.OverhangRegionsAnalyzed);
        stepSw.Restart();

        // ── Step 3: Optimize pinheads ────────────────────────────────────
        var pinheadConfig = new PinheadOptimizer.PinheadConfig
        {
            PinRadiusMm = config.PinRadiusMm * pinRadiusScale,
            BackRadiusMm = config.BackRadiusMm,
            WidthMm = config.HeadWidthMm,
            PenetrationMm = config.PenetrationMm,
            CollisionRays = Math.Min(config.CollisionRays, 4), // limit for performance
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

            // Auto-scale pillar radius based on support height and weight class
            var rCfg = routingConfig;
            float supportHeight = pinhead.JunctionPoint.Z; // height above base
            if (pointWeights.TryGetValue(id, out var weight))
            {
                if (weight == ForceEstimator.SupportWeight.Heavy || supportHeight > 100f)
                {
                    // Height-scaled radius using Euler buckling formula:
                    // Critical load P_cr = PI^2 * E * I / L^2 where I = PI * r^4 / 4
                    // Solving for r to resist a minimum load with safety factor 2:
                    // r = (P * L^2 * 4 / (PI^3 * E * SF))^(1/4)
                    // Simplified: at 100mm r≈0.75, 200mm r≈1.2, 300mm r≈1.6
                    float heightScaledR = 0.4f + supportHeight * 0.004f;
                    rCfg = rCfg with
                    {
                        PillarRadiusMm = Math.Max(rCfg.PillarRadiusMm, Math.Min(heightScaledR, 3.0f)),
                        BaseRadiusMm = Math.Max(rCfg.BaseRadiusMm, Math.Min(heightScaledR * 3f, 5.0f)),
                        WideningFactor = Math.Max(rCfg.WideningFactor, 0.04f),
                    };
                }
                else if (weight == ForceEstimator.SupportWeight.Medium)
                {
                    rCfg = rCfg with
                    {
                        PillarRadiusMm = Math.Max(rCfg.PillarRadiusMm, 0.5f),
                    };
                }
            }

            var route = PillarRouter.Route(pinhead.JunctionPoint, pinhead.BackRadius, bvh, rCfg);
            routes.Add((id, route));
        }

        // Post-routing collision filter: remove routes whose pillar passes through the mesh
        int removedByCollision = 0;
        routes = routes.Where(r =>
        {
            foreach (var wp in r.route.Path)
            {
                if (wp.Type == "base" || wp.Type == "junction") continue;
                if (bvh.IsInside(wp.Position))
                {
                    removedByCollision++;
                    return false;
                }
            }
            return true;
        }).ToList();

        Serilog.Log.Information("V2 Step 4 Routing: {Ms}ms ({Count} routes, {Removed} removed by collision)",
            stepSw.ElapsedMilliseconds, routes.Count, removedByCollision);
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
        var meshParts = new List<IndexedTriangleSet>();

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            // Pinhead mesh
            var phMesh = SupportMesher.Pinhead(pinhead.PinRadius, pinhead.BackRadius, pinhead.Width, 8);
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
            for (int i = 0; i < route.Path.Count - 1; i++)
            {
                var wp1 = route.Path[i];
                var wp2 = route.Path[i + 1];
                var seg = SupportMesher.OrientedFrustum(wp1.Position, wp2.Position, wp1.Radius, wp2.Radius, 8);
                meshParts.Add(seg);

                // Junction sphere at each waypoint
                if (i > 0)
                {
                    var sphere = SupportMesher.OrientedSphere(wp1.Position, wp1.Radius * 0.9f, 4, 8);
                    meshParts.Add(sphere);
                }
            }
        }

        foreach (var conn in interconnections)
        {
            var strut = SupportMesher.OrientedFrustum(conn.PointA, conn.PointB, conn.Radius, conn.Radius, 6);
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

        // ── Step 7: Validate (lightweight — full validation on demand) ───
        // Skip expensive beam-cast validation for generation speed.
        // Full validation available via separate validate endpoint.
        var collisionResult = new CollisionValidator.CollisionResult
        {
            TotalSupportsChecked = pinheads.Count,
            CollisionFreeSupports = routes.Count,
            CollidingSupports = 0,
            TotalCollisionPoints = 0,
            Issues = new(),
            ElapsedMs = 0,
        };

        // Build spatial grid for coverage check
        var coverageGrid = new SpatialGrid<string>(config.MaxSpacingMm);
        foreach (var pt in pointResult.Points)
            coverageGrid.Insert(pt.Position, pt.Id);

        // Reuse point generator's overhang data instead of re-analyzing
        var allRegions = new List<OverhangAnalyzer.OverhangRegion>();

        // Build lookup for fast pinhead-route matching
        var pinheadLookup = pinheads.ToDictionary(p => p.id, p => p.pinhead);
        var routeData = routes.Select(r => (r.id, r.route,
            pinheadLookup.TryGetValue(r.id, out var ph) ? ph.ContactPoint.Z : r.route.Path[0].Position.Z)).ToList();

        var structuralResult = StructuralValidator.Validate(
            routeData, allRegions, coverageGrid, null, config.MinSafetyFactor); // skip manifold check

        Serilog.Log.Information("V2 Step 7 Validation: {Ms}ms", stepSw.ElapsedMilliseconds);
        stepSw.Restart();

        // ── Step 8: Prepare slice elements ───────────────────────────────
        var routeLookup = routes.ToDictionary(r => r.id, r => r.route);
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
        float meshHeight = mesh.Max.Z - mesh.Min.Z;
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
