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
        public float LayerHeightMm { get; init; } = 0.05f;
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
        public float WideningFactor { get; init; } = 0.01f;
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

        // Segment data for backward compatibility with the existing frontend
        public required List<AdvancedSupportEngine.AdvancedSupport> LegacySupports { get; init; }
        public required List<AdvancedSupportEngine.CrossBrace> LegacyCrossBraces { get; init; }
    }

    // ── Main pipeline ────────────────────────────────────────────────────

    public static EngineResult Generate(StlMesh mesh, EngineConfig config)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Step 0: Center mesh ──────────────────────────────────────────
        float meshW = mesh.Max.X - mesh.Min.X;
        float meshD = mesh.Max.Y - mesh.Min.Y;
        float offX = -(mesh.Min.X + meshW / 2);
        float offY = -(mesh.Min.Y + meshD / 2);
        float offZ = -mesh.Min.Z;
        mesh = mesh.Transform(new Vector3(offX, offY, offZ), 1.0f);

        // ── Step 1: Build BVH ────────────────────────────────────────────
        var bvh = AabbBvh.Build(mesh);

        // ── Step 2: Generate support points ──────────────────────────────
        var pointResult = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig
        {
            MinSpacingMm = config.MinSpacingMm,
            MaxSpacingMm = config.MaxSpacingMm,
            DensityFactor = config.DensityFactor,
            Orientation = config.Orientation,
            RecoaterSpeedMmS = config.RecoaterSpeedMmS,
            LayerHeightMm = config.LayerHeightMm,
        });

        // ── Step 3: Optimize pinheads ────────────────────────────────────
        var pinheadConfig = new PinheadOptimizer.PinheadConfig
        {
            PinRadiusMm = config.PinRadiusMm,
            BackRadiusMm = config.BackRadiusMm,
            WidthMm = config.HeadWidthMm,
            PenetrationMm = config.PenetrationMm,
            CollisionRays = config.CollisionRays,
        };

        var pinheads = new List<(string id, PinheadOptimizer.Pinhead pinhead)>();
        foreach (var pt in pointResult.Points)
        {
            var pinhead = PinheadOptimizer.Optimize(pt.Position, pt.Normal, bvh, pinheadConfig);
            pinheads.Add((pt.Id, pinhead));
        }

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
        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            var route = PillarRouter.Route(pinhead.JunctionPoint, pinhead.BackRadius, bvh, routingConfig);
            routes.Add((id, route));
        }

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

        // ── Step 6: Generate meshes ──────────────────────────────────────
        var meshParts = new List<IndexedTriangleSet>();

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            // Pinhead mesh
            var phMesh = SupportMesher.Pinhead(pinhead.PinRadius, pinhead.BackRadius, pinhead.Width, 16);
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
                var seg = SupportMesher.OrientedFrustum(wp1.Position, wp2.Position, wp1.Radius, wp2.Radius, 12);
                meshParts.Add(seg);

                // Junction sphere at each waypoint
                if (i > 0)
                {
                    var sphere = SupportMesher.OrientedSphere(wp1.Position, wp1.Radius * 0.9f, 6, 12);
                    meshParts.Add(sphere);
                }
            }
        }

        foreach (var conn in interconnections)
        {
            var strut = SupportMesher.OrientedFrustum(conn.PointA, conn.PointB, conn.Radius, conn.Radius, 8);
            meshParts.Add(strut);
        }

        var mergeResult = MeshMerger.MergeAll(meshParts, 0.01f);

        // ── Step 7: Validate ─────────────────────────────────────────────
        var collisionResult = CollisionValidator.ValidateAll(pinheads, routes, interconnections, bvh);

        // Build spatial grid for coverage check
        var coverageGrid = new SpatialGrid<string>(config.MaxSpacingMm);
        foreach (var pt in pointResult.Points)
            coverageGrid.Insert(pt.Position, pt.Id);

        var overhangAnalysis = OverhangAnalyzer.Analyze(mesh, config.LayerHeightMm);
        var allRegions = overhangAnalysis.Layers.SelectMany(l => l.Regions).ToList();

        var structuralResult = StructuralValidator.Validate(
            routes.Select(r => (r.id, r.route, pinheads.First(p => p.id == r.id).pinhead.ContactPoint.Z)).ToList(),
            allRegions, coverageGrid, mergeResult.Mesh, config.MinSafetyFactor);

        // ── Step 8: Prepare slice elements ───────────────────────────────
        var sliceElements = AnalyticalSupportSlicer.ExtractElements(
            pinheads.Where(p => p.pinhead.IsValid)
                    .Select(p => (p.pinhead, routes.First(r => r.id == p.id).route))
                    .ToList(),
            interconnections);

        // ── Step 9: Build legacy format for backward-compatible frontend ─
        var legacySupports = BuildLegacySupports(pinheads, routes);
        var legacyCrossBraces = BuildLegacyCrossBraces(interconnections, routes);

        // ── Stats ────────────────────────────────────────────────────────
        int validSupports = pinheads.Count(p => p.pinhead.IsValid);
        float volume = EstimateSupportVolume(routes, interconnections);

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

        foreach (var (id, pinhead) in pinheads)
        {
            if (!pinhead.IsValid) continue;
            var route = routes.FirstOrDefault(r => r.id == id);

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
            if (route.route != null)
            {
                for (int i = 0; i < route.route.Path.Count - 1; i++)
                {
                    var wp1 = route.route.Path[i];
                    var wp2 = route.route.Path[i + 1];
                    string part = wp2.Type == "base" ? "base" :
                                  wp1.Type == "bridge" ? "branch" :
                                  i == route.route.Path.Count - 2 ? "lowerTaper" : "shaft";
                    segments.Add(new AdvancedSupportEngine.SupportSegment
                    {
                        Part = part,
                        X1 = wp1.Position.X, Y1 = wp1.Position.Y, Z1 = wp1.Position.Z, R1 = wp1.Radius,
                        X2 = wp2.Position.X, Y2 = wp2.Position.Y, Z2 = wp2.Position.Z, R2 = wp2.Radius,
                    });
                }
            }

            result.Add(new AdvancedSupportEngine.AdvancedSupport
            {
                Id = id, Type = "v2", Preset = legacyPreset,
                ContactX = pinhead.ContactPoint.X, ContactY = pinhead.ContactPoint.Y, ContactZ = pinhead.ContactPoint.Z,
                NormalX = pinhead.Direction.X, NormalY = pinhead.Direction.Y, NormalZ = pinhead.Direction.Z,
                BaseX = route.route?.Path.Last().Position.X ?? pinhead.JunctionPoint.X,
                BaseY = route.route?.Path.Last().Position.Y ?? pinhead.JunctionPoint.Y,
                BaseZ = route.route?.Path.Last().Position.Z ?? 0,
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
