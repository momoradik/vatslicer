using System.Numerics;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates support points by iterating mesh triangles using STL file normals
/// for inside/outside classification.
///
/// STL files store outward-pointing normals in each triangle's header. These normals
/// come directly from the CAD software and correctly distinguish:
/// - Exterior surfaces: outward normal points AWAY from the solid
/// - Interior surfaces (hollow shells): outward normal points INTO the shell wall
///
/// For overhang detection, an exterior bottom face has outward normal pointing DOWN (Z&lt;0).
/// An interior ceiling has outward normal pointing UP (Z&gt;0) because "outward" from the
/// shell wall at the ceiling goes upward through the wall, not downward into the cavity.
///
/// This is the simplest correct approach — no half-edge mesh, no ray casting, no winding
/// analysis. The STL file already contains the answer.
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

        // Overhang threshold: file normal Z must be below this
        float normalZThreshold = -MathF.Cos(config.OverhangAngleDeg * MathF.PI / 180f);

        var grid = new SpatialGrid<string>(baseSpacing);
        var points = new List<SupportPoint>();
        int idCounter = 0;
        float totalOverhangArea = 0;
        int exteriorOverhangs = 0;

        // ── Build half-edge mesh for topology-based inside/outside ─────────
        // The half-edge mesh welds vertices, builds edge adjacency, makes face
        // winding consistent, and determines global orientation via ray-intersection
        // voting. After this, outward normals correctly distinguish exterior
        // overhangs from interior ceilings — even for non-watertight thin shells.
        var heMesh = HalfEdgeMesh.Build(mesh);

        var overhangTris = new List<(int triIndex, Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 normal, float area, Vector3 centroid)>();

        for (int t = 0; t < heMesh.TriangleCount; t++)
        {
            var normal = heMesh.GetOutwardNormal(t);

            // Check overhang: topology-consistent outward normal must point downward
            if (normal.Z >= normalZThreshold) continue;

            var (v0, v1, v2) = heMesh.GetTriangleVertices(t);
            float area = Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
            if (area < 0.01f) continue;

            var centroid = (v0 + v1 + v2) / 3f;
            totalOverhangArea += area;
            exteriorOverhangs++;

            overhangTris.Add((t, v0, v1, v2, normal, area, centroid));
        }

        // ── Filter: keep only the LOWEST overhang at each XY position ─────
        // For single-wall shells, both the exterior bottom and interior ceiling
        // have downward normals. The exterior is always the LOWER surface.
        // Group overhang triangles by XY grid cell and keep only the lowest per cell.
        var xyGrid = new Dictionary<long, float>(); // grid cell → lowest Z
        float xyCellSize = 1.0f; // 1mm grid for fine XY resolution
        float xyInv = 1f / xyCellSize;

        // First pass: find lowest Z per XY cell
        foreach (var tri in overhangTris)
        {
            int gx = (int)MathF.Floor(tri.centroid.X * xyInv);
            int gy = (int)MathF.Floor(tri.centroid.Y * xyInv);
            long key = ((long)gx << 32) | (uint)gy;
            if (!xyGrid.TryGetValue(key, out float lowestZ) || tri.centroid.Z < lowestZ)
                xyGrid[key] = tri.centroid.Z;
        }

        // Second pass: keep only triangles within 3mm of the lowest Z at their XY
        int beforeFilter = overhangTris.Count;
        overhangTris = overhangTris.Where(tri =>
        {
            int gx = (int)MathF.Floor(tri.centroid.X * xyInv);
            int gy = (int)MathF.Floor(tri.centroid.Y * xyInv);
            long key = ((long)gx << 32) | (uint)gy;
            float lowestZ = xyGrid[key];
            return tri.centroid.Z <= lowestZ + 3f; // within 3mm of lowest
        }).ToList();

        Serilog.Log.Information("Overhang filter: {Before} → {After} (lowest-surface filter)",
            beforeFilter, overhangTris.Count);

        // Sort by Z (lowest = most critical)
        overhangTris.Sort((a, b) => a.centroid.Z.CompareTo(b.centroid.Z));

        // ── Sample support points on each triangle ────────────────────────
        float meshHeight = mesh.Max.Z - mesh.Min.Z;

        foreach (var tri in overhangTris)
        {
            float steepness = MathF.Abs(tri.normal.Z);
            float spacing = baseSpacing * (1.5f - steepness * 0.5f);
            spacing = Math.Clamp(spacing, config.MinSpacingMm, config.MaxSpacingMm);

            int samples = Math.Max(1, (int)(tri.area / (spacing * spacing)));
            samples = Math.Min(samples, 20);

            for (int s = 0; s < samples; s++)
            {
                Vector3 point;
                if (samples == 1)
                {
                    point = tri.centroid;
                }
                else
                {
                    int gridSize = (int)MathF.Ceiling(MathF.Sqrt(samples));
                    int si = s / gridSize, sj = s % gridSize;
                    float u = (si + 0.5f) / gridSize;
                    float v = (sj + 0.5f) / gridSize;
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    point = tri.v0 * (1f - u - v) + tri.v1 * u + tri.v2 * v;
                }

                if (grid.ExistsInRadius(point, spacing))
                    continue;

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

                float overhangArea = tri.area * samples;
                int supportsInRegion = Math.Max(1, (int)(overhangArea / (spacing * spacing)));
                var force = ForceEstimator.Estimate(
                    point.Z, overhangArea, supportsInRegion,
                    overhangArea, spacing,
                    config.Orientation, config.RecoaterSpeedMmS);

                float priority = 1f - Math.Clamp(point.Z / (meshHeight + 1f), 0, 1);
                priority += steepness * 0.3f;

                string id = $"sp-{++idCounter}";
                grid.Insert(point, id);
                points.Add(new SupportPoint
                {
                    Id = id,
                    Position = point,
                    Normal = tri.normal,
                    OverhangArea = overhangArea,
                    OverhangType = point.Z < 2f
                        ? OverhangAnalyzer.OverhangType.NewIsland
                        : steepness > 0.9f
                            ? OverhangAnalyzer.OverhangType.BulkOverhang
                            : OverhangAnalyzer.OverhangType.Peninsula,
                    Priority = priority,
                    RecommendedWeight = force.Weight,
                    SafetyFactor = force.SafetyFactor,
                });
            }
        }

        sw.Stop();
        return new GenerationResult
        {
            Points = points,
            OverhangRegions = new List<OverhangAnalyzer.OverhangRegion>(),
            OverhangRegionsAnalyzed = exteriorOverhangs,
            IslandsDetected = overhangTris.Count(t => t.centroid.Z < 2f),
            TotalOverhangArea = totalOverhangArea,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
