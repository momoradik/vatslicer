using System.Numerics;
using System.Runtime.CompilerServices;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// 3D uniform grid hash for fast spatial queries on point sets.
///
/// O(1) average insertion, O(k) nearest-neighbor query where k = points in nearby cells.
/// Used for support point deduplication, spacing checks, and neighbor finding.
///
/// Operations:
/// - Insert: add a point with an ID
/// - ExistsInRadius: check if any point exists within a radius
/// - FindInRadius: find all points within a radius
/// - NearestN: find N nearest points
/// - Clear: remove all points
/// </summary>
public sealed class SpatialGrid<T> where T : notnull
{
    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly Dictionary<long, List<(Vector3 pos, T id)>> _cells = new();
    private int _count;

    /// <summary>
    /// Create a spatial grid with the given cell size.
    /// Cell size should be approximately equal to the most common query radius.
    /// </summary>
    public SpatialGrid(float cellSize)
    {
        if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        _cellSize = cellSize;
        _invCellSize = 1f / cellSize;
    }

    public int Count => _count;

    // ── Hashing ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int x, int y, int z) CellCoords(Vector3 pos)
    {
        return (
            (int)MathF.Floor(pos.X * _invCellSize),
            (int)MathF.Floor(pos.Y * _invCellSize),
            (int)MathF.Floor(pos.Z * _invCellSize)
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long HashCell(int x, int y, int z)
    {
        // Combine 3 ints into a single long key using bit interleaving
        unchecked
        {
            long h = (long)x * 73856093L ^ (long)y * 19349669L ^ (long)z * 83492791L;
            return h;
        }
    }

    // ── Insert ───────────────────────────────────────────────────────────

    /// <summary>
    /// Insert a point with associated data.
    /// </summary>
    public void Insert(Vector3 position, T id)
    {
        var (cx, cy, cz) = CellCoords(position);
        long key = HashCell(cx, cy, cz);
        if (!_cells.TryGetValue(key, out var list))
        {
            list = new List<(Vector3, T)>(4);
            _cells[key] = list;
        }
        list.Add((position, id));
        _count++;
    }

    // ── ExistsInRadius ───────────────────────────────────────────────────

    /// <summary>
    /// Check if any point exists within the given radius of the query point.
    /// Returns true as soon as one is found (early exit).
    /// </summary>
    public bool ExistsInRadius(Vector3 point, float radius)
    {
        float r2 = radius * radius;
        int cellRadius = (int)MathF.Ceiling(radius * _invCellSize);
        var (cx, cy, cz) = CellCoords(point);

        for (int dx = -cellRadius; dx <= cellRadius; dx++)
        for (int dy = -cellRadius; dy <= cellRadius; dy++)
        for (int dz = -cellRadius; dz <= cellRadius; dz++)
        {
            long key = HashCell(cx + dx, cy + dy, cz + dz);
            if (!_cells.TryGetValue(key, out var list)) continue;
            foreach (var (pos, _) in list)
            {
                if (Vector3.DistanceSquared(point, pos) <= r2)
                    return true;
            }
        }
        return false;
    }

    // ── FindInRadius ─────────────────────────────────────────────────────

    /// <summary>
    /// Find all points within the given radius.
    /// Returns list of (id, distance) pairs sorted by distance.
    /// </summary>
    public List<(T id, float distance)> FindInRadius(Vector3 point, float radius)
    {
        var results = new List<(T id, float distance)>();
        float r2 = radius * radius;
        int cellRadius = (int)MathF.Ceiling(radius * _invCellSize);
        var (cx, cy, cz) = CellCoords(point);

        for (int dx = -cellRadius; dx <= cellRadius; dx++)
        for (int dy = -cellRadius; dy <= cellRadius; dy++)
        for (int dz = -cellRadius; dz <= cellRadius; dz++)
        {
            long key = HashCell(cx + dx, cy + dy, cz + dz);
            if (!_cells.TryGetValue(key, out var list)) continue;
            foreach (var (pos, id) in list)
            {
                float d2 = Vector3.DistanceSquared(point, pos);
                if (d2 <= r2)
                    results.Add((id, MathF.Sqrt(d2)));
            }
        }

        results.Sort((a, b) => a.distance.CompareTo(b.distance));
        return results;
    }

    // ── NearestN ─────────────────────────────────────────────────────────

    /// <summary>
    /// Find the N nearest points to the query point.
    /// Searches outward from the query cell until N points are found.
    /// </summary>
    public List<(T id, float distance)> NearestN(Vector3 point, int n)
    {
        if (n <= 0 || _count == 0) return new List<(T, float)>();

        // Start with nearby cells and expand
        var results = new List<(T id, float distance)>();
        var (cx, cy, cz) = CellCoords(point);

        for (int ring = 0; ring <= 20; ring++) // max 20 cell rings outward
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                // Only process cells on the current ring border (new cells)
                if (Math.Abs(dx) != ring && Math.Abs(dy) != ring && Math.Abs(dz) != ring) continue;

                long key = HashCell(cx + dx, cy + dy, cz + dz);
                if (!_cells.TryGetValue(key, out var list)) continue;
                foreach (var (pos, id) in list)
                    results.Add((id, Vector3.Distance(point, pos)));
            }

            if (results.Count >= n && ring > 0) break;
        }

        results.Sort((a, b) => a.distance.CompareTo(b.distance));
        if (results.Count > n) results.RemoveRange(n, results.Count - n);
        return results;
    }

    // ── Bulk operations ──────────────────────────────────────────────────

    /// <summary>
    /// Remove all points.
    /// </summary>
    public void Clear()
    {
        _cells.Clear();
        _count = 0;
    }

    /// <summary>
    /// Get all stored points as (position, id) pairs.
    /// </summary>
    public IEnumerable<(Vector3 position, T id)> All()
    {
        foreach (var list in _cells.Values)
        foreach (var entry in list)
            yield return entry;
    }
}
