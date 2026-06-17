using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes a 2D support density grid over the build plate.
/// Each cell contains the number of supports whose bases fall within it.
/// Used for visualizing support distribution and identifying sparse regions.
/// </summary>
public static class SupportDensityMapper
{
    public sealed record DensityMap
    {
        public required float[,] Grid { get; init; }
        public required int CellsX { get; init; }
        public required int CellsY { get; init; }
        public required float CellSizeMm { get; init; }
        public required float MaxDensity { get; init; }
        public required float AvgDensity { get; init; }
        public required int TotalSupports { get; init; }
    }

    public static DensityMap Compute(
        IReadOnlyList<Vector3> basePositions,
        float plateWidthMm, float plateDepthMm,
        float cellSizeMm = 5f)
    {
        int cx = Math.Max(1, (int)Math.Ceiling(plateWidthMm / cellSizeMm));
        int cy = Math.Max(1, (int)Math.Ceiling(plateDepthMm / cellSizeMm));
        var grid = new float[cx, cy];

        foreach (var pos in basePositions)
        {
            int ix = Math.Clamp((int)(pos.X / cellSizeMm), 0, cx - 1);
            int iy = Math.Clamp((int)(pos.Y / cellSizeMm), 0, cy - 1);
            grid[ix, iy] += 1;
        }

        float maxD = 0, totalD = 0;
        for (int x = 0; x < cx; x++)
        for (int y = 0; y < cy; y++)
        {
            if (grid[x, y] > maxD) maxD = grid[x, y];
            totalD += grid[x, y];
        }

        return new DensityMap
        {
            Grid = grid, CellsX = cx, CellsY = cy,
            CellSizeMm = cellSizeMm,
            MaxDensity = maxD,
            AvgDensity = (cx * cy > 0) ? totalD / (cx * cy) : 0,
            TotalSupports = basePositions.Count,
        };
    }
}
