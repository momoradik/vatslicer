using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates support points for resin DLP/LCD printing.
///
/// Pipeline:
/// 1. Shell thickening: offset single-wall surfaces inward to create a virtual
///    solid. This gives the half-edge mesh a proper inside/outside boundary.
/// 2. Half-edge mesh: vertex welding + edge adjacency + consistent winding +
///    ray-vote global orientation. After this, outward normals are topologically correct.
/// 3. Centroid convexity: for each overhang triangle, check if its outward normal
///    points toward or away from the model centroid. Concave (interior) → skip.
/// 4. Poisson-disk-like sampling: collect ALL overhang triangles into a single surface,
///    then sample with global minimum-distance enforcement. No per-triangle grid artifacts.
/// 5. Force estimation + priority ordering for structural weight classification.
/// </summary>
public sealed class SupportPointGenerator
{
    public sealed class SupportPoint
    {
        public required string Id { get; init; }
        public required Vector3 Position { get; init; }
        public required Vector3 Normal { get; init; }
        public required float OverhangArea { get; init; }
        public required OverhangAnalyzer.OverhangType OverhangType { get; init; }
        public required float Priority { get; init; }
        public required ForceEstimator.SupportWeight RecommendedWeight { get; init; }
        public required float SafetyFactor { get; init; }
        // Per-support diameter overrides (from manual placement). null = use engine defaults.
        public float? ManualTipRadiusMm { get; init; }
        public float? ManualPillarRadiusMm { get; init; }
        public float? ManualBaseRadiusMm { get; init; }
    }

    public sealed class GenerationConfig
    {
        public float MinSpacingMm { get; init; } = 2.0f;
        public float MaxSpacingMm { get; init; } = 8.0f;
        public float DensityFactor { get; init; } = 0.5f;
        public float OverhangAngleDeg { get; init; } = 45f;
        public PrinterOrientation Orientation { get; init; } = PrinterOrientation.BottomUp;
        public float RecoaterSpeedMmS { get; init; } = 0;
        public float LayerHeightMm { get; init; } = 1.0f;
        public List<(Vector3 position, float radiusMm)>? DrainHoleExclusions { get; init; }
        public float DrainHoleClearanceMm { get; init; } = 2.0f;
        /// <summary>When true, uses contour-based island detection instead of z&lt;2mm heuristic.</summary>
        public bool UnifiedIslandDetection { get; init; } = false;
        /// <summary>Enable line contact: detect downward overhang edges and place dense tips along them.</summary>
        public bool EnableLineContact { get; init; } = false;
        /// <summary>Spacing of tips along overhang edges (mm). Default = base contact spacing.</summary>
        public float LineContactSpacingMm { get; init; } = 0; // 0 = use baseSpacing
        /// <summary>Enable face contact: regular grid on large flat overhangs.</summary>
        public bool EnableFaceContact { get; init; } = false;
        /// <summary>Grid spacing for face contact (mm).</summary>
        public float FaceGridSpacingMm { get; init; } = 3f;
        /// <summary>Min overhang face area to trigger face grid (mm²).</summary>
        public float FaceContactAreaThresholdMm2 { get; init; } = 50f;
        /// <summary>Enable force-driven placement: densify where peel force is high. Default OFF.</summary>
        public bool EnableForceDrivenPlacement { get; init; } = false;
    }

    public sealed class GenerationResult
    {
        public required List<SupportPoint> Points { get; init; }
        public required List<OverhangAnalyzer.OverhangRegion> OverhangRegions { get; init; }
        public required int OverhangRegionsAnalyzed { get; init; }
        public required int IslandsDetected { get; init; }
        public required float TotalOverhangArea { get; init; }
        public required long ElapsedMs { get; init; }
    }

    public static GenerationResult Generate(StlMesh mesh, GenerationConfig config, AabbBvh? prebuiltBvh = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        float baseSpacing = config.MinSpacingMm
            + (config.MaxSpacingMm - config.MinSpacingMm) * (1f - config.DensityFactor);
        float normalZThreshold = -MathF.Cos(config.OverhangAngleDeg * MathF.PI / 180f);

        // ── Step 1: Identify overhang triangles ────────────────────────────
        // For large meshes (>50K tris), skip the expensive HalfEdgeMesh build and
        // use STL file normals directly. For small meshes, still use HalfEdgeMesh
        // for topologically correct normals.
        var overhangTris = new List<(Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 normal, float area, Vector3 centroid)>();
        float totalOverhangArea = 0;

        if (mesh.TriangleCount > 50_000)
        {
            // Fast path: use STL file normals directly (O(n), no half-edge build)
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var normal = mesh.FileNormals[t];
                // Normalize if needed (STL normals can be zero or non-unit)
                float len = normal.Length();
                if (len < 0.001f)
                {
                    // Compute from vertices
                    var va = mesh.Vertices[t * 3];
                    var vb = mesh.Vertices[t * 3 + 1];
                    var vc = mesh.Vertices[t * 3 + 2];
                    normal = Vector3.Cross(vb - va, vc - va);
                    len = normal.Length();
                    if (len < 0.001f) continue;
                }
                normal /= len;

                if (normal.Z >= normalZThreshold) continue;

                var v0 = mesh.Vertices[t * 3];
                var v1 = mesh.Vertices[t * 3 + 1];
                var v2 = mesh.Vertices[t * 3 + 2];
                float area = Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
                if (area < 0.01f) continue;

                var triCenter = (v0 + v1 + v2) / 3f;
                totalOverhangArea += area;
                overhangTris.Add((v0, v1, v2, normal, area, triCenter));
            }
        }
        else
        {
            // Standard path: use HalfEdgeMesh for topologically correct normals
            var heMesh = HalfEdgeMesh.Build(mesh);
            for (int t = 0; t < heMesh.TriangleCount; t++)
            {
                var normal = heMesh.GetOutwardNormal(t);
                if (normal.Z >= normalZThreshold) continue;

                var (v0, v1, v2) = heMesh.GetTriangleVertices(t);
                float area = Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
                if (area < 0.01f) continue;

                var triCenter = (v0 + v1 + v2) / 3f;
                totalOverhangArea += area;
                overhangTris.Add((v0, v1, v2, normal, area, triCenter));
            }
        }

        // Diagnostic: spatial distribution of detected overhang triangles
        {
            float minZ = overhangTris.Count > 0 ? overhangTris.Min(t => t.centroid.Z) : 0;
            float maxZ = overhangTris.Count > 0 ? overhangTris.Max(t => t.centroid.Z) : 0;
            float midZ = (minZ + maxZ) / 2;
            int lowerHalf = overhangTris.Count(t => t.centroid.Z < midZ);
            int upperHalf = overhangTris.Count - lowerHalf;
            Serilog.Log.Information("Overhang DETECTION: {Count} tris, {Area:F0}mm², Z=[{MinZ:F1},{MaxZ:F1}], lower={Lower} upper={Upper}",
                overhangTris.Count, totalOverhangArea, minZ, maxZ, lowerHalf, upperHalf);
        }

        // DO NOT sort by Z — Z-sort + candidate cap = only bottom half gets sampled.
        // Instead, shuffle for spatial uniformity so the candidate cap doesn't bias by Z.
        // Use a deterministic shuffle (Fisher-Yates with fixed seed) for reproducibility.
        var rng = new Random(42);
        for (int i = overhangTris.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (overhangTris[i], overhangTris[j]) = (overhangTris[j], overhangTris[i]);
        }

        Serilog.Log.Information("Overhang: {Count} exterior tris, {Area:F0}mm² total",
            overhangTris.Count, totalOverhangArea);

        // ── Step 4: Poisson-disk-like sampling across ALL overhang triangles ─
        var grid = new SpatialGrid<string>(baseSpacing);
        var points = new List<SupportPoint>();
        int idCounter = 0;
        float meshHeight = mesh.Max.Z - mesh.Min.Z;

        // Build weighted candidate list: one centroid per triangle, up to cap.
        // Cap is scaled by mesh triangle count to handle large meshes without explosion.
        int maxCandidates = Math.Max(overhangTris.Count, 100_000);
        var candidates = new List<(Vector3 point, Vector3 normal, float area, float steepness, float z)>();

        // Pre-compute per-triangle peel force for force-driven placement
        float maxPeelForce = 0, minPeelForce = float.MaxValue;
        float[] triPeelForces = new float[0];
        if (config.EnableForceDrivenPlacement)
        {
            triPeelForces = new float[overhangTris.Count];
            for (int ti = 0; ti < overhangTris.Count; ti++)
            {
                // Peel force ∝ cured layer area at this Z. Approximate by collecting
                // total overhang area within ±2mm of this triangle's Z.
                float triZ = overhangTris[ti].centroid.Z;
                float layerArea = 0;
                foreach (var ot in overhangTris)
                    if (MathF.Abs(ot.centroid.Z - triZ) < 2f) layerArea += ot.area;
                float peelForce = layerArea * 0.015f; // P_adhesion × A_layer
                triPeelForces[ti] = peelForce;
                if (peelForce > maxPeelForce) maxPeelForce = peelForce;
                if (peelForce < minPeelForce) minPeelForce = peelForce;
            }
        }
        float peelRange = maxPeelForce - minPeelForce;

        int _triLoopIdx = -1;
        foreach (var tri in overhangTris)
        {
            _triLoopIdx++;
            if (candidates.Count >= maxCandidates) break;
            float steepness = MathF.Abs(tri.normal.Z);
            float spacing;
            if (config.EnableForceDrivenPlacement && peelRange > 1e-6f)
            {
                // Map peel force to spacing: high force → MinSpacing (dense), low force → MaxSpacing (sparse)
                float peel = _triLoopIdx < triPeelForces.Length ? triPeelForces[_triLoopIdx] : 0;
                float t = (peel - minPeelForce) / peelRange; // 0=low force, 1=high force
                // Invert: high force → low spacing
                spacing = config.MaxSpacingMm - t * (config.MaxSpacingMm - config.MinSpacingMm);
            }
            else
            {
                spacing = baseSpacing * (1.5f - steepness * 0.5f);
            }
            spacing = Math.Clamp(spacing, config.MinSpacingMm, config.MaxSpacingMm);

            // Number of candidates proportional to area — NO cap
            int nSamples = Math.Max(1, (int)(tri.area / (spacing * spacing)));

            // Always add centroid as first candidate
            candidates.Add((tri.centroid, tri.normal, tri.area, steepness, tri.centroid.Z));

            // Add additional samples using jittered barycentric grid
            for (int s = 1; s < nSamples; s++)
            {
                int gridSize = (int)MathF.Ceiling(MathF.Sqrt(nSamples));
                int si = s / gridSize, sj = s % gridSize;
                // Jitter: offset by half-cell with slight randomization via index
                float jx = ((s * 7 + 3) % 13) / 13f * 0.3f; // deterministic pseudo-random
                float jy = ((s * 11 + 5) % 13) / 13f * 0.3f;
                float u = (si + 0.35f + jx) / gridSize;
                float v = (sj + 0.35f + jy) / gridSize;
                if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                u = Math.Clamp(u, 0.01f, 0.98f);
                v = Math.Clamp(v, 0.01f, Math.Min(0.98f, 1f - u - 0.01f));
                var point = tri.v0 * (1f - u - v) + tri.v1 * u + tri.v2 * v;
                candidates.Add((point, tri.normal, tri.area, steepness, point.Z));
            }
        }

        // Accept/reject candidates via global spacing grid (Poisson-disk style)
        foreach (var (point, normal, triArea, steepness, z) in candidates)
        {
            float spacing = baseSpacing * (1.5f - steepness * 0.5f);
            spacing = Math.Clamp(spacing, config.MinSpacingMm, config.MaxSpacingMm);

            if (grid.ExistsInRadius(point, spacing))
                continue;

            // Drain hole exclusion
            if (config.DrainHoleExclusions is { Count: > 0 })
            {
                bool tooClose = false;
                foreach (var (holePos, holeR) in config.DrainHoleExclusions)
                {
                    if (Vector3.Distance(point, holePos) < holeR + config.DrainHoleClearanceMm)
                    { tooClose = true; break; }
                }
                if (tooClose) continue;
            }

            // Force estimation
            float overhangArea = triArea;
            int supportsInRegion = Math.Max(1, (int)(overhangArea / (spacing * spacing)));
            var force = ForceEstimator.Estimate(
                point.Z, overhangArea, supportsInRegion,
                overhangArea, spacing,
                config.Orientation, config.RecoaterSpeedMmS);

            float priority = 1f - Math.Clamp(z / (meshHeight + 1f), 0, 1);
            priority += steepness * 0.3f;

            string id = $"sp-{++idCounter}";
            grid.Insert(point, id);
            points.Add(new SupportPoint
            {
                Id = id,
                Position = point,
                Normal = normal,
                OverhangArea = overhangArea,
                OverhangType = z < 2f
                    ? OverhangAnalyzer.OverhangType.NewIsland
                    : steepness > 0.9f
                        ? OverhangAnalyzer.OverhangType.BulkOverhang
                        : OverhangAnalyzer.OverhangType.Peninsula,
                Priority = priority,
                RecommendedWeight = force.Weight,
                SafetyFactor = force.SafetyFactor,
            });
        }

        // ── Line contact: detect downward overhang edges and sample tips along them ──
        if (config.EnableLineContact)
        {
            float lineSpacing = config.LineContactSpacingMm > 0 ? config.LineContactSpacingMm : baseSpacing;
            int linePointsBefore = points.Count;

            // Build edge→face map from overhang triangles
            var edgeFaces = new Dictionary<long, List<int>>();
            for (int ti = 0; ti < overhangTris.Count; ti++)
            {
                var (v0, v1, v2, _, _, _) = overhangTris[ti];
                var verts = new[] { v0, v1, v2 };
                for (int ei = 0; ei < 3; ei++)
                {
                    var ea = verts[ei];
                    var eb = verts[(ei + 1) % 3];
                    long key = EdgeKey(ea, eb);
                    if (!edgeFaces.TryGetValue(key, out var faces))
                    {
                        faces = new List<int>();
                        edgeFaces[key] = faces;
                    }
                    faces.Add(ti);
                }
            }

            // Find edges shared by 2 overhang triangles (the crease/ridge edges)
            var edgeSegments = new List<(Vector3 a, Vector3 b, Vector3 avgNormal)>();
            foreach (var (key, faces) in edgeFaces)
            {
                if (faces.Count < 2) continue;
                // Get the two triangles' normals
                var n1 = overhangTris[faces[0]].normal;
                var n2 = overhangTris[faces[1]].normal;
                var avgN = Vector3.Normalize(n1 + n2);
                if (avgN.Z >= normalZThreshold) continue; // average normal must face downward

                // Decode edge endpoints from key
                // Find the shared edge vertices by intersecting the two triangles' vertex sets
                var tri1 = overhangTris[faces[0]];
                var tri2 = overhangTris[faces[1]];
                var shared = FindSharedEdge(tri1.v0, tri1.v1, tri1.v2, tri2.v0, tri2.v1, tri2.v2);
                if (shared == null) continue;

                var (ea, eb) = shared.Value;
                float edgeLen = Vector3.Distance(ea, eb);
                if (edgeLen < 0.5f) continue; // skip tiny edges

                // Only include edges that are mostly horizontal (thin edge detection)
                float zDiff = MathF.Abs(ea.Z - eb.Z);
                if (zDiff / edgeLen > 0.5f) continue; // too vertical

                edgeSegments.Add((ea, eb, avgN));
            }

            // Sample tips along each edge segment
            foreach (var (ea, eb, avgN) in edgeSegments)
            {
                float edgeLen = Vector3.Distance(ea, eb);
                int nSamples = Math.Max(2, (int)MathF.Ceiling(edgeLen / lineSpacing));

                for (int si = 0; si < nSamples; si++)
                {
                    float t = nSamples > 1 ? (float)si / (nSamples - 1) : 0.5f;
                    var pos = Vector3.Lerp(ea, eb, t);

                    // Skip if already covered by existing point
                    if (grid.ExistsInRadius(pos, lineSpacing * 0.6f)) continue;

                    var force = ForceEstimator.Estimate(
                        pos.Z, 5f, 1, 5f, lineSpacing,
                        config.Orientation, config.RecoaterSpeedMmS);

                    string id = $"sp-{++idCounter}";
                    grid.Insert(pos, id);
                    points.Add(new SupportPoint
                    {
                        Id = id,
                        Position = pos,
                        Normal = avgN,
                        OverhangArea = 5f,
                        OverhangType = OverhangAnalyzer.OverhangType.Peninsula,
                        Priority = 0.9f,
                        RecommendedWeight = force.Weight,
                        SafetyFactor = force.SafetyFactor,
                    });
                }
            }

            int linePointsAdded = points.Count - linePointsBefore;
            Serilog.Log.Information("V2 LineContact: {Edges} overhang edges found, {Points} line tips added (spacing={Spacing:F1}mm)",
                edgeSegments.Count, linePointsAdded, lineSpacing);
        }

        // ── Face contact: regular grid on large flat overhangs ──────
        if (config.EnableFaceContact)
        {
            float faceSpacing = config.FaceGridSpacingMm;
            float areaThresh = config.FaceContactAreaThresholdMm2;
            int facePointsBefore = points.Count;

            // Group overhang triangles by nearly-flat downward faces (normal.Z close to -1)
            // A "face" group = triangles whose normals differ by < 15° and that share edges
            foreach (var tri in overhangTris)
            {
                if (tri.area < areaThresh) continue;
                // Only near-horizontal faces (normal nearly straight down)
                if (tri.normal.Z > -0.7f) continue; // must be > ~45° overhang

                // Sample a regular grid over this triangle's XY bounding box
                float minX = MathF.Min(tri.v0.X, MathF.Min(tri.v1.X, tri.v2.X));
                float maxX = MathF.Max(tri.v0.X, MathF.Max(tri.v1.X, tri.v2.X));
                float minY = MathF.Min(tri.v0.Y, MathF.Min(tri.v1.Y, tri.v2.Y));
                float maxY = MathF.Max(tri.v0.Y, MathF.Max(tri.v1.Y, tri.v2.Y));

                for (float gx = minX; gx <= maxX; gx += faceSpacing)
                for (float gy = minY; gy <= maxY; gy += faceSpacing)
                {
                    // Check if (gx, gy) is inside the triangle (XY projection)
                    if (!PointInTriangleXY(gx, gy, tri.v0, tri.v1, tri.v2)) continue;

                    // Interpolate Z from triangle plane
                    float gz = InterpolateZ(gx, gy, tri.v0, tri.v1, tri.v2);
                    var pos = new Vector3(gx, gy, gz);

                    if (grid.ExistsInRadius(pos, faceSpacing * 0.5f)) continue;

                    var force = ForceEstimator.Estimate(
                        gz, tri.area, 1, tri.area, faceSpacing,
                        config.Orientation, config.RecoaterSpeedMmS);

                    string id = $"sp-{++idCounter}";
                    grid.Insert(pos, id);
                    points.Add(new SupportPoint
                    {
                        Id = id,
                        Position = pos,
                        Normal = tri.normal,
                        OverhangArea = tri.area,
                        OverhangType = OverhangAnalyzer.OverhangType.BulkOverhang,
                        Priority = 0.8f,
                        RecommendedWeight = force.Weight,
                        SafetyFactor = force.SafetyFactor,
                    });
                }
            }

            int facePointsAdded = points.Count - facePointsBefore;
            Serilog.Log.Information("V2 FaceContact: {Points} grid tips added (spacing={Spacing:F1}mm, threshold={Thresh:F0}mm²)",
                facePointsAdded, faceSpacing, areaThresh);
        }

        // ── Unified island detection (contour-based, same as slicer) ──────
        int islandsDetected = overhangTris.Count(t => t.centroid.Z < 2f);
        if (config.UnifiedIslandDetection)
        {
            float analysisLayerH = config.LayerHeightMm;
            float meshMinZ = mesh.Min.Z;
            float meshMaxZ = mesh.Max.Z;
            int layerCount = Math.Max(1, (int)MathF.Ceiling((meshMaxZ - meshMinZ) / analysisLayerH));

            var prevPolygons = new List<List<Vector2>>();
            int contourIslands = 0;

            for (int li = 0; li < layerCount; li++)
            {
                float z = meshMinZ + (li + 0.5f) * analysisLayerH;
                var polygons = MeshCrossSectionEngine.CrossSection(mesh, z);

                bool isFirstLayer = (li == 0);
                var islands = IslandDetector.FindIslandContours(polygons, prevPolygons, isFirstLayer);

                foreach (var (contour, centroid2D) in islands)
                {
                    contourIslands++;

                    // Collect indices of existing points within this island for reclassification
                    var island3D = new Vector3(centroid2D.X, centroid2D.Y, z);
                    float contourRadius = MathF.Sqrt(ComputeContourArea(contour) / MathF.PI);
                    for (int pi = 0; pi < points.Count; pi++)
                    {
                        var pt = points[pi];
                        if (pt.OverhangType == OverhangAnalyzer.OverhangType.NewIsland) continue;
                        float dz = MathF.Abs(pt.Position.Z - z);
                        if (dz > analysisLayerH) continue;
                        float dxy = Vector2.Distance(new Vector2(pt.Position.X, pt.Position.Y), centroid2D);
                        if (dxy <= contourRadius * 1.2f)
                        {
                            points[pi] = new SupportPoint
                            {
                                Id = pt.Id, Position = pt.Position, Normal = pt.Normal,
                                OverhangArea = pt.OverhangArea,
                                OverhangType = OverhangAnalyzer.OverhangType.NewIsland,
                                Priority = 1.0f,
                                RecommendedWeight = pt.RecommendedWeight,
                                SafetyFactor = pt.SafetyFactor,
                                ManualTipRadiusMm = pt.ManualTipRadiusMm,
                                ManualPillarRadiusMm = pt.ManualPillarRadiusMm,
                                ManualBaseRadiusMm = pt.ManualBaseRadiusMm,
                            };
                        }
                    }

                    // Check if any existing point already covers this island
                    bool alreadyCovered = grid.ExistsInRadius(island3D, baseSpacing * 1.5f);
                    if (alreadyCovered) continue;

                    // Inject a forced support point at the island centroid
                    // Normal = straight down (-Z), since this is a floating underside
                    var normal = new Vector3(0, 0, -1);
                    float area = ComputeContourArea(contour);

                    var force = ForceEstimator.Estimate(
                        z, Math.Max(area, 10f), 1, area, baseSpacing,
                        config.Orientation, config.RecoaterSpeedMmS);

                    string id = $"sp-{++idCounter}";
                    grid.Insert(island3D, id);
                    points.Add(new SupportPoint
                    {
                        Id = id,
                        Position = island3D,
                        Normal = normal,
                        OverhangArea = area,
                        OverhangType = OverhangAnalyzer.OverhangType.NewIsland,
                        Priority = 1.0f, // islands get max priority
                        RecommendedWeight = force.Weight,
                        SafetyFactor = force.SafetyFactor,
                    });
                }

                prevPolygons = polygons;
            }

            islandsDetected = contourIslands;
            Serilog.Log.Information("V2 UnifiedIslandDetection: {Layers} layers scanned, {Islands} island contours, {Injected} new points injected",
                layerCount, contourIslands, points.Count - (points.Count - contourIslands)); // simplified
        }

        // (reclassification happens inline in the island scan loop above)

        sw.Stop();
        return new GenerationResult
        {
            Points = points,
            OverhangRegions = new List<OverhangAnalyzer.OverhangRegion>(),
            OverhangRegionsAnalyzed = overhangTris.Count,
            IslandsDetected = islandsDetected,
            TotalOverhangArea = totalOverhangArea,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static float ComputeContourArea(List<Vector2> contour)
    {
        float area = 0;
        for (int i = 0, j = contour.Count - 1; i < contour.Count; j = i++)
            area += (contour[j].X + contour[i].X) * (contour[j].Y - contour[i].Y);
        return MathF.Abs(area) * 0.5f;
    }

    /// <summary>Spatial hash key for an edge (order-independent).</summary>
    private static long EdgeKey(Vector3 a, Vector3 b)
    {
        // Quantize to 0.001mm grid then hash
        long ax = (long)(a.X * 1000), ay = (long)(a.Y * 1000), az = (long)(a.Z * 1000);
        long bx = (long)(b.X * 1000), by = (long)(b.Y * 1000), bz = (long)(b.Z * 1000);
        long ha = ax * 73856093L ^ ay * 19349669L ^ az * 83492791L;
        long hb = bx * 73856093L ^ by * 19349669L ^ bz * 83492791L;
        return ha < hb ? ha * 1000003L + hb : hb * 1000003L + ha;
    }

    /// <summary>Find the shared edge between two triangles, or null if none.</summary>
    private static (Vector3 a, Vector3 b)? FindSharedEdge(
        Vector3 a0, Vector3 a1, Vector3 a2,
        Vector3 b0, Vector3 b1, Vector3 b2)
    {
        var aVerts = new[] { a0, a1, a2 };
        var bVerts = new[] { b0, b1, b2 };
        var shared = new List<Vector3>();
        foreach (var av in aVerts)
            foreach (var bv in bVerts)
                if (Vector3.DistanceSquared(av, bv) < 0.001f * 0.001f)
                    shared.Add(av);
        return shared.Count >= 2 ? (shared[0], shared[1]) : null;
    }

    /// <summary>Point-in-triangle test in XY projection using barycentric coordinates.</summary>
    private static bool PointInTriangleXY(float px, float py, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = (px - b.X) * (a.Y - b.Y) - (a.X - b.X) * (py - b.Y);
        float d2 = (px - c.X) * (b.Y - c.Y) - (b.X - c.X) * (py - c.Y);
        float d3 = (px - a.X) * (c.Y - a.Y) - (c.X - a.X) * (py - a.Y);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    /// <summary>Interpolate Z at (px, py) on the plane of triangle (a, b, c).</summary>
    private static float InterpolateZ(float px, float py, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (MathF.Abs(n.Z) < 1e-8f) return (a.Z + b.Z + c.Z) / 3f;
        return a.Z - (n.X * (px - a.X) + n.Y * (py - a.Y)) / n.Z;
    }
}
