using System.Numerics;
using System.Runtime.CompilerServices;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Column occupancy grid with per-cell Z bitsets for O(1) vertical clearance queries.
///
/// Data structure: (nx × ny) grid at cellSize resolution. Each cell stores
/// ulong[] where bit l = 1 iff the model occupies that cell at layer l.
///
/// Key operations:
///   ColumnClearToPlate: OR all bit-words for a column within a radius; clear iff PopCount==0
///   IsOccupied: test single bit at (cx, cy, layer)
///
/// Replaces AabbBvh.BeamCast for straight-down tests (the ~85% fast path).
/// Build time: O(triangles × layers_per_tri). Query time: O(1) per column.
/// </summary>
public sealed class OccupancyBitstack
{
    private readonly int _nx, _ny, _nz;
    private readonly int _wordsPerColumn;
    private readonly float _cellSize;
    private readonly float _layerHeight;
    private readonly float _minX, _minY, _minZ;
    private readonly ulong[][] _bits; // [cellIndex][wordIndex]

    public int CellsX => _nx;
    public int CellsY => _ny;
    public int Layers => _nz;
    public float CellSize => _cellSize;
    public float LayerHeight => _layerHeight;

    private OccupancyBitstack(int nx, int ny, int nz, float cellSize, float layerHeight,
        float minX, float minY, float minZ, ulong[][] bits)
    {
        _nx = nx; _ny = ny; _nz = nz;
        _wordsPerColumn = (nz + 63) / 64;
        _cellSize = cellSize; _layerHeight = layerHeight;
        _minX = minX; _minY = minY; _minZ = minZ;
        _bits = bits;
    }

    /// <summary>
    /// Build the occupancy bitstack from a triangle mesh.
    /// </summary>
    /// <param name="mesh">Source mesh.</param>
    /// <param name="cellSize">XY cell size in mm (smaller = more precise, more memory). Default 0.3mm.</param>
    /// <param name="layerHeight">Z layer height for bit quantization. Default 0.05mm.</param>
    public static OccupancyBitstack Build(StlMesh mesh, float cellSize = 0.3f, float layerHeight = 0.05f)
    {
        float minX = mesh.Min.X, minY = mesh.Min.Y, minZ = mesh.Min.Z;
        float maxX = mesh.Max.X, maxY = mesh.Max.Y, maxZ = mesh.Max.Z;

        int nx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellSize));
        int ny = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / cellSize));
        int nz = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / layerHeight));
        int wordsPerCol = (nz + 63) / 64;

        // Allocate bit arrays
        int totalCells = nx * ny;
        var bits = new ulong[totalCells][];
        for (int i = 0; i < totalCells; i++)
            bits[i] = new ulong[wordsPerCol];

        // Rasterize each triangle into the grid
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

            // Triangle bounding box in grid coords
            float triMinX = MathF.Min(v0.X, MathF.Min(v1.X, v2.X));
            float triMaxX = MathF.Max(v0.X, MathF.Max(v1.X, v2.X));
            float triMinY = MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y));
            float triMaxY = MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y));
            float triMinZ = MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z));
            float triMaxZ = MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z));

            int cxMin = Math.Clamp((int)((triMinX - minX) / cellSize), 0, nx - 1);
            int cxMax = Math.Clamp((int)((triMaxX - minX) / cellSize), 0, nx - 1);
            int cyMin = Math.Clamp((int)((triMinY - minY) / cellSize), 0, ny - 1);
            int cyMax = Math.Clamp((int)((triMaxY - minY) / cellSize), 0, ny - 1);
            int lzMin = Math.Clamp((int)((triMinZ - minZ) / layerHeight), 0, nz - 1);
            int lzMax = Math.Clamp((int)((triMaxZ - minZ) / layerHeight), 0, nz - 1);

            // Set bits for all cells the triangle bbox covers
            for (int cy = cyMin; cy <= cyMax; cy++)
            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                int cellIdx = cy * nx + cx;
                for (int lz = lzMin; lz <= lzMax; lz++)
                {
                    int word = lz >> 6; // lz / 64
                    int bit = lz & 63;  // lz % 64
                    bits[cellIdx][word] |= 1UL << bit;
                }
            }
        }

        return new OccupancyBitstack(nx, ny, nz, cellSize, layerHeight, minX, minY, minZ, bits);
    }

    /// <summary>
    /// Check if a column from z=0 to zTop is clear of model geometry within a radius.
    /// </summary>
    /// <param name="cx">Cell X index.</param>
    /// <param name="cy">Cell Y index.</param>
    /// <param name="zTopLayer">Top layer index (exclusive).</param>
    /// <param name="radCells">Radius in cells to check (0 = single column).</param>
    /// <returns>True if the column is completely clear.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ColumnClearToPlate(int cx, int cy, int zTopLayer, int radCells = 0)
    {
        // Check all cells within radius
        int cxMin = Math.Max(0, cx - radCells);
        int cxMax = Math.Min(_nx - 1, cx + radCells);
        int cyMin = Math.Max(0, cy - radCells);
        int cyMax = Math.Min(_ny - 1, cy + radCells);

        int topWord = Math.Min(zTopLayer >> 6, _wordsPerColumn - 1);

        for (int y = cyMin; y <= cyMax; y++)
        for (int x = cxMin; x <= cxMax; x++)
        {
            int cellIdx = y * _nx + x;
            var col = _bits[cellIdx];

            // Check all words up to zTopLayer
            for (int w = 0; w <= topWord; w++)
            {
                ulong mask = col[w];
                if (w == topWord && (zTopLayer & 63) != 0)
                    mask &= (1UL << (zTopLayer & 63)) - 1; // mask off bits above zTop
                if (mask != 0) return false; // occupied!
            }
        }

        return true;
    }

    /// <summary>
    /// Check if a world-space column is clear from z=0 to the given Z height.
    /// </summary>
    public bool ColumnClearToPlate(Vector3 position, float pillarRadius)
    {
        int cx = (int)((position.X - _minX) / _cellSize);
        int cy = (int)((position.Y - _minY) / _cellSize);
        int zTop = (int)((position.Z - _minZ) / _layerHeight);
        int radCells = Math.Max(0, (int)MathF.Ceiling(pillarRadius / _cellSize));

        if (cx < 0 || cx >= _nx || cy < 0 || cy >= _ny) return true; // outside grid = clear
        zTop = Math.Clamp(zTop, 0, _nz);

        return ColumnClearToPlate(cx, cy, zTop, radCells);
    }

    /// <summary>
    /// Test if a point is inside the model.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOccupied(Vector3 p)
    {
        int cx = (int)((p.X - _minX) / _cellSize);
        int cy = (int)((p.Y - _minY) / _cellSize);
        int lz = (int)((p.Z - _minZ) / _layerHeight);

        if (cx < 0 || cx >= _nx || cy < 0 || cy >= _ny || lz < 0 || lz >= _nz) return false;

        int cellIdx = cy * _nx + cx;
        int word = lz >> 6;
        int bit = lz & 63;
        return (_bits[cellIdx][word] & (1UL << bit)) != 0;
    }

    /// <summary>
    /// Convert world position to cell coordinates.
    /// </summary>
    public (int cx, int cy, int layer) WorldToCell(Vector3 p)
    {
        return (
            (int)((p.X - _minX) / _cellSize),
            (int)((p.Y - _minY) / _cellSize),
            (int)((p.Z - _minZ) / _layerHeight));
    }
}
