using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Resin.Routing;

/// <summary>
/// Magics-style down-projection support: projects the overhang silhouette
/// straight down to the build plate and fills it with a hatch/lattice pattern.
///
/// Unlike pillar supports (individual cylinders), projection supports create
/// a continuous wall/lattice structure under overhangs. Better for:
/// - Large flat overhangs (full-face support)
/// - Thin shelves that need uniform support
/// - Parts where individual pillar marks are unacceptable
///
/// The hatch pattern can be: grid, hex, or concentric.
/// </summary>
public static class ProjectionSupportBuilder
{
    public sealed record ProjectionConfig
    {
        public float WallThicknessMm { get; init; } = 0.4f;
        public float HatchSpacingMm { get; init; } = 2.0f;
        public string Pattern { get; init; } = "grid"; // grid, hex, lines
        public float ContactGapMm { get; init; } = 0.1f; // gap between support top and model
        public float BaseThicknessMm { get; init; } = 0.3f;
    }

    public sealed record ProjectionResult
    {
        public required List<AnalyticalSupportSlicer.SupportElement> SliceElements { get; init; }
        public required IndexedTriangleSet Mesh { get; init; }
        public required int WallCount { get; init; }
        public required float VolumeMm3 { get; init; }
    }

    /// <summary>
    /// Build projection supports for overhang regions.
    /// </summary>
    /// <param name="overhangPoints">Points on the model surface that need support.</param>
    /// <param name="baseZ">Build plate Z height.</param>
    /// <param name="config">Projection configuration.</param>
    public static ProjectionResult Build(
        IReadOnlyList<(Vector3 position, Vector3 normal)> overhangPoints,
        float baseZ,
        ProjectionConfig? config = null)
    {
        config ??= new ProjectionConfig();
        var elements = new List<AnalyticalSupportSlicer.SupportElement>();
        var mesh = new IndexedTriangleSet();
        float totalVolume = 0;
        int wallCount = 0;

        if (overhangPoints.Count == 0)
            return new ProjectionResult { SliceElements = elements, Mesh = mesh, WallCount = 0, VolumeMm3 = 0 };

        // Group overhang points into a grid
        float gridSize = config.HatchSpacingMm;
        var grid = new Dictionary<(int gx, int gy), List<Vector3>>();

        foreach (var (pos, _) in overhangPoints)
        {
            int gx = (int)MathF.Floor(pos.X / gridSize);
            int gy = (int)MathF.Floor(pos.Y / gridSize);
            var key = (gx, gy);
            if (!grid.ContainsKey(key)) grid[key] = new();
            grid[key].Add(pos);
        }

        // For each occupied grid cell, build a vertical wall/pillar from baseZ to overhang
        foreach (var (key, points) in grid)
        {
            float cx = (key.gx + 0.5f) * gridSize;
            float cy = (key.gy + 0.5f) * gridSize;
            float topZ = points.Max(p => p.Z) - config.ContactGapMm;
            float botZ = baseZ + config.BaseThicknessMm;

            if (topZ <= botZ) continue;

            float wallR = config.WallThicknessMm / 2;

            // Grid pattern: vertical walls in X and Y directions
            if (config.Pattern == "grid" || config.Pattern == "lines")
            {
                // X-aligned wall segment
                var wallA = new Vector3(cx - gridSize / 2, cy, botZ);
                var wallB = new Vector3(cx + gridSize / 2, cy, botZ);
                var wallC = new Vector3(cx - gridSize / 2, cy, topZ);
                var wallD = new Vector3(cx + gridSize / 2, cy, topZ);

                // Slice element (thin frustum as wall cross-section)
                elements.Add(new AnalyticalSupportSlicer.SupportElement
                {
                    PointA = new Vector3(cx, cy, botZ),
                    PointB = new Vector3(cx, cy, topZ),
                    RadiusA = wallR,
                    RadiusB = wallR,
                    Type = "projection-wall",
                });

                // Mesh: oriented frustum for the wall
                var wallMesh = SupportMesher.OrientedFrustum(
                    new Vector3(cx, cy, botZ), new Vector3(cx, cy, topZ),
                    wallR, wallR, 4);
                mesh.Merge(wallMesh);

                float height = topZ - botZ;
                totalVolume += MathF.PI * wallR * wallR * height;
                wallCount++;
            }

            // Base pad
            elements.Add(new AnalyticalSupportSlicer.SupportElement
            {
                PointA = new Vector3(cx, cy, baseZ),
                PointB = new Vector3(cx, cy, botZ),
                RadiusA = wallR * 2,
                RadiusB = wallR,
                Type = "raft",
            });
        }

        return new ProjectionResult
        {
            SliceElements = elements,
            Mesh = mesh,
            WallCount = wallCount,
            VolumeMm3 = totalVolume,
        };
    }
}
