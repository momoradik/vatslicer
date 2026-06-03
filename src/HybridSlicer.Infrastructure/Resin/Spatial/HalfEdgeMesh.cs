using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Half-edge mesh built from STL triangle soup.
///
/// Provides proper mesh topology that STL files lack:
/// - Vertex welding: merges duplicate vertices within epsilon
/// - Edge adjacency: for each edge, the two triangles that share it
/// - Consistent face winding: all outward normals point AWAY from solid
/// - Exterior face classification: identifies which faces are on the outside
///
/// This is the standard approach used by PrusaSlicer (TriangleMesh) and
/// ChiTuBox for inside/outside determination. Without it, hollow shells
/// cannot be correctly supported because interior ceilings are
/// indistinguishable from exterior overhangs by normal direction alone.
///
/// After winding consistency:
/// - Exterior bottom face: outward normal = (0,0,-1) → overhang ✓
/// - Interior ceiling: outward normal = (0,0,+1) → NOT overhang ✓
///   (the "outward" direction from the solid shell wall points UP
///    through the shell into the air above, not down into the cavity)
/// </summary>
public sealed class HalfEdgeMesh
{
    /// <summary>Welded vertex positions.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Triangle vertex indices (3 per triangle, into Positions array).</summary>
    public int[] Indices { get; }

    /// <summary>Per-triangle outward normal (consistent winding).</summary>
    public Vector3[] FaceNormals { get; }

    /// <summary>Per-triangle flag: true if this face was flipped to achieve consistent winding.</summary>
    public bool[] WasFlipped { get; }

    /// <summary>Per-triangle neighbor lists (triangles sharing an edge).</summary>
    public List<int>[] TriangleNeighbors { get; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;

    private HalfEdgeMesh(Vector3[] positions, int[] indices, Vector3[] normals, bool[] flipped, List<int>[] neighbors)
    {
        Positions = positions;
        Indices = indices;
        FaceNormals = normals;
        WasFlipped = flipped;
        TriangleNeighbors = neighbors;
    }

    /// <summary>
    /// Build a half-edge mesh from raw STL triangle soup.
    ///
    /// Steps:
    /// 1. Weld vertices: merge positions within epsilon using spatial hash grid
    /// 2. Build edge adjacency: map each directed edge to its triangle
    /// 3. Orient faces consistently: BFS from a seed face, flip neighbors if winding disagrees
    /// 4. Determine global orientation: the majority of the mesh volume should be positive
    ///    (if negative, all normals are inverted → flip everything)
    /// </summary>
    public static HalfEdgeMesh Build(StlMesh mesh, float weldEpsilon = 0.001f)
    {
        int triCount = mesh.TriangleCount;

        // ── Step 1: Vertex welding ────────────────────────────────────────
        // Hash grid for O(1) duplicate vertex detection
        float cellSize = weldEpsilon * 10f;
        var vertexMap = new Dictionary<long, List<int>>();
        var positions = new List<Vector3>();
        var indices = new int[triCount * 3];

        for (int t = 0; t < triCount; t++)
        {
            for (int v = 0; v < 3; v++)
            {
                var pos = mesh.Vertices[t * 3 + v];
                int idx = FindOrAddVertex(pos, positions, vertexMap, cellSize, weldEpsilon);
                indices[t * 3 + v] = idx;
            }
        }

        var posArray = positions.ToArray();

        // ── Step 2: Build edge adjacency ──────────────────────────────────
        // Map each undirected edge (min,max vertex index) to its triangle(s)
        var edgeToTris = new Dictionary<long, List<(int triIndex, bool forward)>>();

        for (int t = 0; t < triCount; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = indices[t * 3 + e];
                int b = indices[t * 3 + (e + 1) % 3];
                long edgeKey = MakeEdgeKey(a, b);
                bool forward = a < b;

                if (!edgeToTris.TryGetValue(edgeKey, out var list))
                {
                    list = new List<(int, bool)>(2);
                    edgeToTris[edgeKey] = list;
                }
                list.Add((t, forward));
            }
        }

        // ── Step 3: Orient faces consistently (BFS) ──────────────────────
        var oriented = new bool[triCount];
        var flipped = new bool[triCount];
        var queue = new Queue<int>();

        // Build triangle adjacency from edge map
        var triNeighbors = new List<(int neighbor, bool sameWinding)>[triCount];
        for (int t = 0; t < triCount; t++)
            triNeighbors[t] = new List<(int, bool)>();

        foreach (var (_, tris) in edgeToTris)
        {
            if (tris.Count != 2) continue; // boundary or non-manifold edge
            var (t0, f0) = tris[0];
            var (t1, f1) = tris[1];
            // Two triangles sharing an edge should have OPPOSITE directed edges
            // (one goes A→B, the other B→A). If both go the same direction,
            // their winding is inconsistent.
            bool consistent = f0 != f1;
            triNeighbors[t0].Add((t1, consistent));
            triNeighbors[t1].Add((t0, consistent));
        }

        // BFS from each unoriented triangle (handles disconnected components)
        for (int seed = 0; seed < triCount; seed++)
        {
            if (oriented[seed]) continue;
            oriented[seed] = true;
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (var (neighbor, consistent) in triNeighbors[current])
                {
                    if (oriented[neighbor]) continue;
                    oriented[neighbor] = true;

                    if (!consistent)
                    {
                        // Neighbor has inconsistent winding — flip it
                        // (swap two vertex indices to reverse winding)
                        int ni = neighbor * 3;
                        (indices[ni + 1], indices[ni + 2]) = (indices[ni + 2], indices[ni + 1]);
                        flipped[neighbor] = !flipped[current];
                    }
                    else
                    {
                        flipped[neighbor] = flipped[current];
                    }

                    queue.Enqueue(neighbor);
                }
            }
        }

        // ── Step 4: Compute face normals from consistent winding ──────────
        var normals = new Vector3[triCount];
        for (int t = 0; t < triCount; t++)
        {
            var v0 = posArray[indices[t * 3]];
            var v1 = posArray[indices[t * 3 + 1]];
            var v2 = posArray[indices[t * 3 + 2]];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            float len = cross.Length();
            normals[t] = len > 1e-10f ? cross / len : Vector3.UnitZ;
        }

        // ── Step 5: Determine global orientation ──────────────────────────
        // Sample several triangles. For each, cast a ray along its normal from
        // its centroid. Count how many OTHER mesh triangles the ray intersects.
        // If odd → the normal points INWARD (enclosed). If even → OUTWARD.
        // Use majority vote across samples for robustness.
        int inwardVotes = 0, outwardVotes = 0;
        int sampleCount = Math.Min(triCount, 20);
        int sampleStep = Math.Max(1, triCount / sampleCount);

        for (int si = 0; si < sampleCount; si++)
        {
            int t = si * sampleStep;
            if (t >= triCount) break;

            var c0 = posArray[indices[t * 3]];
            var c1 = posArray[indices[t * 3 + 1]];
            var c2 = posArray[indices[t * 3 + 2]];
            var centroid = (c0 + c1 + c2) / 3f;
            var n = normals[t];
            if (n.LengthSquared() < 0.01f) continue;

            // Cast ray from centroid along normal, count mesh intersections
            // (simple brute-force — only done once during build)
            var rayOrigin = centroid + n * 0.01f; // small offset to avoid self-hit
            int hitCount = 0;
            for (int ot = 0; ot < triCount; ot++)
            {
                if (ot == t) continue;
                var a = posArray[indices[ot * 3]];
                var b = posArray[indices[ot * 3 + 1]];
                var c = posArray[indices[ot * 3 + 2]];
                if (RayTriangleIntersect(rayOrigin, n, a, b, c) > 0.001f)
                    hitCount++;
            }

            if (hitCount % 2 == 1)
                inwardVotes++; // odd hits = normal points inward
            else
                outwardVotes++; // even hits = normal points outward
        }

        if (inwardVotes > outwardVotes)
        {
            // Majority says normals point inward — flip all faces
            for (int t = 0; t < triCount; t++)
            {
                int ni = t * 3;
                (indices[ni + 1], indices[ni + 2]) = (indices[ni + 2], indices[ni + 1]);
                normals[t] = -normals[t];
                flipped[t] = !flipped[t];
            }
        }

        // Build plain neighbor lists for curvature analysis
        var plainNeighbors = new List<int>[triCount];
        for (int t = 0; t < triCount; t++)
            plainNeighbors[t] = new List<int>();
        foreach (var (_, tris) in edgeToTris)
        {
            if (tris.Count != 2) continue;
            plainNeighbors[tris[0].triIndex].Add(tris[1].triIndex);
            plainNeighbors[tris[1].triIndex].Add(tris[0].triIndex);
        }

        return new HalfEdgeMesh(posArray, indices, normals, flipped, plainNeighbors);
    }

    /// <summary>
    /// Check if a triangle's outward normal indicates it's an exterior overhang.
    /// </summary>
    public bool IsExteriorOverhang(int triangleIndex, float normalZThreshold)
    {
        return FaceNormals[triangleIndex].Z < normalZThreshold;
    }

    /// <summary>
    /// Determine if a triangle is on a CONVEX surface (exterior) or CONCAVE (interior).
    ///
    /// For single-wall shells, both sides of the shell have the same winding-based normal.
    /// To distinguish convex (exterior) from concave (interior), we check the local curvature:
    ///
    /// For each neighbor triangle sharing an edge, compute the dihedral angle.
    /// If the neighbor's centroid is ABOVE the plane defined by this triangle's
    /// surface → the surface curves UPWARD at this edge → this triangle is on the
    /// CONCAVE (interior) side.
    ///
    /// If most neighbors' centroids are BELOW the plane → the surface curves DOWNWARD
    /// → this triangle is on the CONVEX (exterior) side.
    ///
    /// For overhangs (normal.Z &lt; 0), "above the plane" means the neighbor centroid
    /// is on the side OPPOSITE to where the support would go. Concave overhangs
    /// curve toward the model interior and shouldn't get supports.
    /// </summary>
    public bool IsConvexSurface(int triangleIndex)
    {
        var neighbors = TriangleNeighbors[triangleIndex];
        if (neighbors.Count == 0) return true;

        var normal = FaceNormals[triangleIndex];
        var (v0, v1, v2) = GetTriangleVertices(triangleIndex);
        var centroid = (v0 + v1 + v2) / 3f;

        // Use 2-ring neighborhood for more robust curvature estimation
        var visited = new HashSet<int> { triangleIndex };
        var ring1 = new List<int>(neighbors);
        foreach (int n in neighbors) visited.Add(n);

        // Expand to 2-ring
        var ring2 = new List<int>();
        foreach (int n in ring1)
        {
            foreach (int nn in TriangleNeighbors[n])
            {
                if (visited.Add(nn))
                    ring2.Add(nn);
            }
        }

        float totalDot = 0;
        int count = 0;

        // Check all neighbors in both rings
        foreach (int ni in ring1)
        {
            var (nv0, nv1, nv2) = GetTriangleVertices(ni);
            var nc = (nv0 + nv1 + nv2) / 3f;
            totalDot += Vector3.Dot(nc - centroid, normal);
            count++;
        }
        foreach (int ni in ring2)
        {
            var (nv0, nv1, nv2) = GetTriangleVertices(ni);
            var nc = (nv0 + nv1 + nv2) / 3f;
            totalDot += Vector3.Dot(nc - centroid, normal);
            count++;
        }

        if (count == 0) return true;

        // Method 1: Centroid displacement (existing)
        float avgDot = totalDot / count;

        // Method 2: Normal divergence — if neighborhood average normal
        // has HIGHER Z than this triangle, the surface curves upward = concave
        Vector3 avgNeighborNormal = Vector3.Zero;
        int nCount = 0;
        foreach (int ni in ring1) { avgNeighborNormal += FaceNormals[ni]; nCount++; }
        foreach (int ni in ring2) { avgNeighborNormal += FaceNormals[ni]; nCount++; }
        if (nCount > 0) avgNeighborNormal /= nCount;

        float normalDivergence = avgNeighborNormal.Z - normal.Z;
        // If neighbors' average Z is significantly higher than ours → concave
        // Only filter as concave when BOTH signals agree strongly
        bool centroidSaysConcave = avgDot > 0.1f;
        bool normalSaysConcave = normalDivergence > 0.15f;

        // Only reject if BOTH curvature signals say concave
        if (centroidSaysConcave && normalSaysConcave) return false;
        return true;
    }

    /// <summary>
    /// Get the outward normal for a triangle (consistent winding).
    /// </summary>
    public Vector3 GetOutwardNormal(int triangleIndex) => FaceNormals[triangleIndex];

    /// <summary>
    /// Get the original (pre-welding) triangle vertices from the source mesh.
    /// </summary>
    public (Vector3 v0, Vector3 v1, Vector3 v2) GetTriangleVertices(int triangleIndex)
    {
        return (
            Positions[Indices[triangleIndex * 3]],
            Positions[Indices[triangleIndex * 3 + 1]],
            Positions[Indices[triangleIndex * 3 + 2]]
        );
    }

    // ── Internal helpers ──────────────────────────────────────────────

    private static int FindOrAddVertex(Vector3 pos, List<Vector3> positions,
        Dictionary<long, List<int>> map, float cellSize, float epsilon)
    {
        float invCell = 1f / cellSize;
        int cx = (int)MathF.Floor(pos.X * invCell);
        int cy = (int)MathF.Floor(pos.Y * invCell);
        int cz = (int)MathF.Floor(pos.Z * invCell);
        float eps2 = epsilon * epsilon;

        // Search nearby cells for matching vertex
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            long key = HashCell(cx + dx, cy + dy, cz + dz);
            if (map.TryGetValue(key, out var list))
            {
                foreach (int idx in list)
                {
                    if (Vector3.DistanceSquared(positions[idx], pos) <= eps2)
                        return idx; // found existing vertex
                }
            }
        }

        // Add new vertex
        int newIdx = positions.Count;
        positions.Add(pos);
        long cellKey = HashCell(cx, cy, cz);
        if (!map.TryGetValue(cellKey, out var cellList))
        {
            cellList = new List<int>(4);
            map[cellKey] = cellList;
        }
        cellList.Add(newIdx);
        return newIdx;
    }

    private static long HashCell(int x, int y, int z)
    {
        unchecked
        {
            return (long)x * 73856093L ^ (long)y * 19349669L ^ (long)z * 83492791L;
        }
    }

    private static long MakeEdgeKey(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        return ((long)lo << 32) | (uint)hi;
    }

    /// <summary>
    /// Möller–Trumbore ray-triangle intersection. Returns distance > 0 if hit, -1 if miss.
    /// </summary>
    private static float RayTriangleIntersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float EPSILON = 1e-7f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h = Vector3.Cross(dir, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < EPSILON) return -1f;
        float f = 1f / a;
        var s = origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return -1f;
        var q = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(dir, q);
        if (v < 0f || u + v > 1f) return -1f;
        float t = f * Vector3.Dot(e2, q);
        return t > EPSILON ? t : -1f;
    }
}
