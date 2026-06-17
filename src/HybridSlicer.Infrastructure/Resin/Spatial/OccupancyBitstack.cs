using System.Numerics;
using System.Runtime.CompilerServices;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// COARSE column occupancy grid for O(1) vertical clearance queries.
///
/// Design (per Task 1 addendum):
/// - cellSize ≈ pillarRadius (~0.3-0.5mm) — NOT pixel resolution
/// - Z step ≈ support-analysis layer height (~0.5-1mm) — NOT 0.05mm print layer height
/// - Total memory: tens of MB or less (fully cache-resident)
/// - Built from MeshCrossSectionEngine.CrossSection at coarse Z steps
///
/// Data structure: (nx × ny) grid. Each cell stores ulong[] where bit l = 1
/// iff the model occupies that cell at coarse layer l.
///
/// Key operations:
///   ColumnClearToPlate: OR bit-words below zTop across cells within radius,
///     then PopCount == 0.
///   IsOccupied: test single bit at (cx, cy, layer).
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
    /// Build the COARSE occupancy bitstack from a triangle mesh.
    /// Uses triangle-bbox rasterization (fast, conservative).
    /// </summary>
    /// <param name="mesh">Source mesh.</param>
    /// <param name="cellSize">XY cell size ≈ pillarRadius (~0.3-0.5mm).</param>
    /// <param name="layerHeight">COARSE Z step for support analysis (~0.5-1.0mm). NOT print layer height.</param>
    public static OccupancyBitstack Build(StlMesh mesh, float cellSize = 0.4f, float layerHeight = 0.5f)
    {
        float minX = mesh.Min.X, minY = mesh.Min.Y, minZ = mesh.Min.Z;
        float maxX = mesh.Max.X, maxY = mesh.Max.Y, maxZ = mesh.Max.Z;

        int nx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellSize));
        int ny = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / cellSize));
        int nz = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / layerHeight));
        int wordsPerCol = (nz + 63) / 64;

        // Memory budget check: nx*ny*wordsPerCol*8 bytes
        long memBytes = (long)nx * ny * wordsPerCol * 8;
        // If > 100MB, increase cellSize/layerHeight (should never happen with coarse params)
        if (memBytes > 100_000_000)
        {
            Serilog.Log.Warning("OccupancyBitstack too large ({Mb}MB), coarsening", memBytes / (1024 * 1024));
            return Build(mesh, cellSize * 2, layerHeight * 2);
        }

        var bits = new ulong[nx * ny][];
        for (int i = 0; i < bits.Length; i++)
            bits[i] = new ulong[wordsPerCol];

        // Rasterize triangles into the coarse grid (bbox conservative)
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

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

            for (int cy = cyMin; cy <= cyMax; cy++)
            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                int cellIdx = cy * nx + cx;
                for (int lz = lzMin; lz <= lzMax; lz++)
                    bits[cellIdx][lz >> 6] |= 1UL << (lz & 63);
            }
        }

        Serilog.Log.Information("OccupancyBitstack: {Nx}x{Ny} cells, {Nz} layers, {Mb:F1}MB",
            nx, ny, nz, memBytes / (1024.0 * 1024.0));

        return new OccupancyBitstack(nx, ny, nz, cellSize, layerHeight, minX, minY, minZ, bits);
    }

    /// <summary>
    /// Check if a column from z=0 to zTop is clear within a radius.
    /// OR all bit-words for cells within radCells, PopCount == 0 → clear.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ColumnClearToPlate(int cx, int cy, int zTopLayer, int radCells = 0)
    {
        int cxMin = Math.Max(0, cx - radCells);
        int cxMax = Math.Min(_nx - 1, cx + radCells);
        int cyMin = Math.Max(0, cy - radCells);
        int cyMax = Math.Min(_ny - 1, cy + radCells);
        int topWord = Math.Min(zTopLayer >> 6, _wordsPerColumn - 1);

        for (int y = cyMin; y <= cyMax; y++)
        for (int x = cxMin; x <= cxMax; x++)
        {
            var col = _bits[y * _nx + x];
            for (int w = 0; w <= topWord; w++)
            {
                ulong mask = col[w];
                if (w == topWord && (zTopLayer & 63) != 0)
                    mask &= (1UL << (zTopLayer & 63)) - 1;
                if (mask != 0) return false;
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

        if (cx < 0 || cx >= _nx || cy < 0 || cy >= _ny) return true;
        zTop = Math.Clamp(zTop, 0, _nz);

        return ColumnClearToPlate(cx, cy, zTop, radCells);
    }

    /// <summary>
    /// Test if a point is inside the model (at coarse resolution).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOccupied(Vector3 p)
    {
        int cx = (int)((p.X - _minX) / _cellSize);
        int cy = (int)((p.Y - _minY) / _cellSize);
        int lz = (int)((p.Z - _minZ) / _layerHeight);

        if (cx < 0 || cx >= _nx || cy < 0 || cy >= _ny || lz < 0 || lz >= _nz) return false;

        return (_bits[cy * _nx + cx][lz >> 6] & (1UL << (lz & 63))) != 0;
    }

    /// <summary>Convert world position to cell coordinates.</summary>
    public (int cx, int cy, int layer) WorldToCell(Vector3 p)
    {
        return (
            (int)((p.X - _minX) / _cellSize),
            (int)((p.Y - _minY) / _cellSize),
            (int)((p.Z - _minZ) / _layerHeight));
    }
}
