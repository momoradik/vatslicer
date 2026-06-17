using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Bitwise island detection using the OccupancyBitstack.
///
/// Island[L] = LayerBits[L] AND NOT Dilate(LayerBits[L-1], r)
/// where r = ceil(tan(maxSelfSupportAngle) * layerHeight / cellSize)
///
/// A connected component in Island[L] that has ≥ minIslandCells pixels
/// is a floating island that needs a support at its centroid.
///
/// This replaces per-layer polygon boolean operations with pure bitwise
/// operations — O(nx*ny) per layer, total O(nx*ny*nz).
/// </summary>
public static class BitwiseIslandDetector
{
    public sealed record IslandResult
    {
        public required int LayerIndex { get; init; }
        public required float ZMm { get; init; }
        public required Vector3 Centroid { get; init; }
        public required int CellCount { get; init; }
        public required float AreaMm2 { get; init; }
    }

    /// <summary>
    /// Detect floating islands at all layers using bitwise operations.
    /// </summary>
    public static List<IslandResult> Detect(
        OccupancyBitstack bitstack,
        float maxSelfSupportAngleDeg = 45f,
        int minIslandCells = 4,
        float meshMinZ = 0f)
    {
        var results = new List<IslandResult>();
        int nx = bitstack.CellsX, ny = bitstack.CellsY, nz = bitstack.Layers;
        float cellSize = bitstack.CellSize;
        float layerHeight = bitstack.LayerHeight;

        // Dilation radius: r = ceil(tan(angle) * layerHeight / cellSize)
        float tanAngle = MathF.Tan(maxSelfSupportAngleDeg * MathF.PI / 180f);
        int dilateR = Math.Max(1, (int)MathF.Ceiling(tanAngle * layerHeight / cellSize));

        // Process layer by layer
        var prevLayer = new bool[nx * ny]; // dilated previous layer
        var currLayer = new bool[nx * ny];
        var islandMask = new bool[nx * ny];

        for (int lz = 1; lz < nz; lz++)
        {
            // Extract current layer bits
            int word = lz >> 6;
            ulong bitMask = 1UL << (lz & 63);
            for (int cy = 0; cy < ny; cy++)
            for (int cx = 0; cx < nx; cx++)
                currLayer[cy * nx + cx] = bitstack.IsOccupied(new Vector3(
                    cx * cellSize + meshMinZ, cy * cellSize, lz * layerHeight + meshMinZ));

            // Extract and dilate previous layer
            int prevWord = (lz - 1) >> 6;
            ulong prevBitMask = 1UL << ((lz - 1) & 63);
            Array.Clear(prevLayer);
            for (int cy = 0; cy < ny; cy++)
            for (int cx = 0; cx < nx; cx++)
            {
                bool occ = bitstack.IsOccupied(new Vector3(
                    cx * cellSize + meshMinZ, cy * cellSize, (lz - 1) * layerHeight + meshMinZ));
                if (!occ) continue;
                // Dilate: set all cells within dilateR
                for (int dy = -dilateR; dy <= dilateR; dy++)
                for (int dx = -dilateR; dx <= dilateR; dx++)
                {
                    int nx2 = cx + dx, ny2 = cy + dy;
                    if (nx2 >= 0 && nx2 < nx && ny2 >= 0 && ny2 < ny)
                        prevLayer[ny2 * nx + nx2] = true;
                }
            }

            // Island = current AND NOT dilatedPrev
            int islandCells = 0;
            float sumX = 0, sumY = 0;
            for (int cy = 0; cy < ny; cy++)
            for (int cx = 0; cx < nx; cx++)
            {
                int idx = cy * nx + cx;
                islandMask[idx] = currLayer[idx] && !prevLayer[idx];
                if (islandMask[idx])
                {
                    islandCells++;
                    sumX += cx;
                    sumY += cy;
                }
            }

            if (islandCells >= minIslandCells)
            {
                float z = meshMinZ + (lz + 0.5f) * layerHeight;
                results.Add(new IslandResult
                {
                    LayerIndex = lz,
                    ZMm = z,
                    Centroid = new Vector3(
                        (sumX / islandCells) * cellSize + meshMinZ,
                        (sumY / islandCells) * cellSize,
                        z),
                    CellCount = islandCells,
                    AreaMm2 = islandCells * cellSize * cellSize,
                });
            }
        }

        return results;
    }
}
