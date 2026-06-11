using System.Numerics;
using System.Runtime.CompilerServices;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Axis-Aligned Bounding Box Bounding Volume Hierarchy.
///
/// Binary tree over mesh triangles for O(log n) spatial queries.
/// Built using Surface Area Heuristic (SAH) for optimal split planes.
///
/// Supports:
/// - RayCast: find first triangle hit along a ray
/// - BeamCast: cast N rays in a cone pattern, return minimum hit distance
/// - IsInside: point-in-mesh test (odd intersection count)
/// - ClosestPoint: nearest point on mesh surface
/// - ClosestTriangle: nearest triangle to a point
///
/// Performance target: 100k triangles, 10k queries &lt; 50ms.
/// </summary>
public sealed class AabbBvh
{
    // ── Node storage (flat array for cache coherence) ─────────────────────

    // Each node: 2 floats for AABB min (6 floats), 2 for AABB max (6 floats),
    // plus child/triangle indices. Stored as struct-of-arrays for SIMD potential.

    private readonly float[] _minX, _minY, _minZ;
    private readonly float[] _maxX, _maxY, _maxZ;
    private readonly int[] _left;     // left child index (or -1 for leaf)
    private readonly int[] _right;    // right child index (or -1 for leaf)
    private readonly int[] _triStart; // first triangle index in leaf
    private readonly int[] _triCount; // number of triangles in leaf
    private int _nodeCount;

    // Triangle data (reordered during build for locality)
    private readonly Vector3[] _v0, _v1, _v2; // triangle vertices
    private readonly int[] _triIndices;         // original triangle indices

    // Mesh reference
    private readonly int _totalTriangles;

    // Thread-safe node allocation for parallel build
    private int AllocNode() => System.Threading.Interlocked.Increment(ref _nodeCount) - 1;

    private const int MAX_LEAF_TRIS = 4;
    private const int SAH_BINS = 12;

    // ── Construction ─────────────────────────────────────────────────────

    private AabbBvh(int maxNodes, int triCount)
    {
        _minX = new float[maxNodes]; _minY = new float[maxNodes]; _minZ = new float[maxNodes];
        _maxX = new float[maxNodes]; _maxY = new float[maxNodes]; _maxZ = new float[maxNodes];
        _left = new int[maxNodes]; _right = new int[maxNodes];
        _triStart = new int[maxNodes]; _triCount = new int[maxNodes];
        _nodeCount = 0;
        _totalTriangles = triCount;

        _v0 = new Vector3[triCount];
        _v1 = new Vector3[triCount];
        _v2 = new Vector3[triCount];
        _triIndices = new int[triCount];
    }

    /// <summary>
    /// Build a BVH from an StlMesh. O(n log n) construction.
    /// </summary>
    public static AabbBvh Build(StlMesh mesh)
    {
        int triCount = mesh.TriangleCount;
        int maxNodes = triCount * 2 + 1;
        var bvh = new AabbBvh(maxNodes, triCount);

        // Copy triangle data and compute centroids
        var centroids = new Vector3[triCount];
        for (int i = 0; i < triCount; i++)
        {
            bvh._v0[i] = mesh.Vertices[i * 3];
            bvh._v1[i] = mesh.Vertices[i * 3 + 1];
            bvh._v2[i] = mesh.Vertices[i * 3 + 2];
            bvh._triIndices[i] = i;
            centroids[i] = (bvh._v0[i] + bvh._v1[i] + bvh._v2[i]) / 3f;
        }

        // Build tree recursively
        bvh.BuildNode(0, triCount, centroids);
        return bvh;
    }

    private int BuildNode(int start, int end, Vector3[] centroids)
    {
        int nodeIdx = AllocNode();
        int count = end - start;

        // Compute AABB for this range
        float mnX = float.MaxValue, mnY = float.MaxValue, mnZ = float.MaxValue;
        float mxX = float.MinValue, mxY = float.MinValue, mxZ = float.MinValue;
        for (int i = start; i < end; i++)
        {
            mnX = Math.Min(mnX, Math.Min(_v0[i].X, Math.Min(_v1[i].X, _v2[i].X)));
            mnY = Math.Min(mnY, Math.Min(_v0[i].Y, Math.Min(_v1[i].Y, _v2[i].Y)));
            mnZ = Math.Min(mnZ, Math.Min(_v0[i].Z, Math.Min(_v1[i].Z, _v2[i].Z)));
            mxX = Math.Max(mxX, Math.Max(_v0[i].X, Math.Max(_v1[i].X, _v2[i].X)));
            mxY = Math.Max(mxY, Math.Max(_v0[i].Y, Math.Max(_v1[i].Y, _v2[i].Y)));
            mxZ = Math.Max(mxZ, Math.Max(_v0[i].Z, Math.Max(_v1[i].Z, _v2[i].Z)));
        }
        _minX[nodeIdx] = mnX; _minY[nodeIdx] = mnY; _minZ[nodeIdx] = mnZ;
        _maxX[nodeIdx] = mxX; _maxY[nodeIdx] = mxY; _maxZ[nodeIdx] = mxZ;

        // Leaf node?
        if (count <= MAX_LEAF_TRIS)
        {
            _left[nodeIdx] = -1;
            _right[nodeIdx] = -1;
            _triStart[nodeIdx] = start;
            _triCount[nodeIdx] = count;
            return nodeIdx;
        }

        // Find best split using Surface Area Heuristic
        int bestAxis = -1, bestBin = -1;
        float bestCost = float.MaxValue;
        float parentArea = SurfaceArea(mnX, mnY, mnZ, mxX, mxY, mxZ);

        for (int axis = 0; axis < 3; axis++)
        {
            float axMin = axis == 0 ? mnX : axis == 1 ? mnY : mnZ;
            float axMax = axis == 0 ? mxX : axis == 1 ? mxY : mxZ;
            if (axMax - axMin < 1e-8f) continue;

            // Bin centroids
            var binCount = new int[SAH_BINS];
            var binMin = new Vector3[SAH_BINS];
            var binMax = new Vector3[SAH_BINS];
            for (int b = 0; b < SAH_BINS; b++)
            {
                binMin[b] = new Vector3(float.MaxValue);
                binMax[b] = new Vector3(float.MinValue);
            }

            float scale = SAH_BINS / (axMax - axMin);
            for (int i = start; i < end; i++)
            {
                float cv = axis == 0 ? centroids[i].X : axis == 1 ? centroids[i].Y : centroids[i].Z;
                int bin = Math.Clamp((int)((cv - axMin) * scale), 0, SAH_BINS - 1);
                binCount[bin]++;
                binMin[bin] = Vector3.Min(binMin[bin], Vector3.Min(_v0[i], Vector3.Min(_v1[i], _v2[i])));
                binMax[bin] = Vector3.Max(binMax[bin], Vector3.Max(_v0[i], Vector3.Max(_v1[i], _v2[i])));
            }

            // Sweep to find best split
            var leftMin = new Vector3[SAH_BINS]; var leftMax = new Vector3[SAH_BINS];
            var leftCount = new int[SAH_BINS];
            var rightMin = new Vector3[SAH_BINS]; var rightMax = new Vector3[SAH_BINS];
            var rightCount = new int[SAH_BINS];

            // Left sweep
            var cMin = new Vector3(float.MaxValue);
            var cMax = new Vector3(float.MinValue);
            int cCount = 0;
            for (int b = 0; b < SAH_BINS; b++)
            {
                cCount += binCount[b];
                if (binCount[b] > 0) { cMin = Vector3.Min(cMin, binMin[b]); cMax = Vector3.Max(cMax, binMax[b]); }
                leftMin[b] = cMin; leftMax[b] = cMax; leftCount[b] = cCount;
            }

            // Right sweep
            cMin = new Vector3(float.MaxValue);
            cMax = new Vector3(float.MinValue);
            cCount = 0;
            for (int b = SAH_BINS - 1; b >= 0; b--)
            {
                cCount += binCount[b];
                if (binCount[b] > 0) { cMin = Vector3.Min(cMin, binMin[b]); cMax = Vector3.Max(cMax, binMax[b]); }
                rightMin[b] = cMin; rightMax[b] = cMax; rightCount[b] = cCount;
            }

            // Evaluate SAH cost for each split position
            for (int b = 0; b < SAH_BINS - 1; b++)
            {
                if (leftCount[b] == 0 || rightCount[b + 1] == 0) continue;
                float leftArea = SurfaceArea(leftMin[b].X, leftMin[b].Y, leftMin[b].Z, leftMax[b].X, leftMax[b].Y, leftMax[b].Z);
                float rightArea = SurfaceArea(rightMin[b + 1].X, rightMin[b + 1].Y, rightMin[b + 1].Z, rightMax[b + 1].X, rightMax[b + 1].Y, rightMax[b + 1].Z);
                float cost = 1.0f + (leftArea * leftCount[b] + rightArea * rightCount[b + 1]) / parentArea;
                if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestBin = b; }
            }
        }

        // If no good split found, make a leaf
        if (bestAxis == -1 || bestCost >= count)
        {
            _left[nodeIdx] = -1;
            _right[nodeIdx] = -1;
            _triStart[nodeIdx] = start;
            _triCount[nodeIdx] = count;
            return nodeIdx;
        }

        // Partition triangles by the best split
        float splitAxisMin = bestAxis == 0 ? mnX : bestAxis == 1 ? mnY : mnZ;
        float splitAxisMax = bestAxis == 0 ? mxX : bestAxis == 1 ? mxY : mxZ;
        float splitScale = SAH_BINS / (splitAxisMax - splitAxisMin);

        int mid = start;
        for (int i = start; i < end; i++)
        {
            float cv = bestAxis == 0 ? centroids[i].X : bestAxis == 1 ? centroids[i].Y : centroids[i].Z;
            int bin = Math.Clamp((int)((cv - splitAxisMin) * splitScale), 0, SAH_BINS - 1);
            if (bin <= bestBin)
            {
                // Swap to left partition
                Swap(i, mid, centroids);
                mid++;
            }
        }

        // Fallback: if partition degenerated, split in half
        if (mid == start || mid == end)
            mid = (start + end) / 2;

        _triStart[nodeIdx] = -1;
        _triCount[nodeIdx] = 0;
        _left[nodeIdx] = BuildNode(start, mid, centroids);
        _right[nodeIdx] = BuildNode(mid, end, centroids);

        return nodeIdx;
    }

    private void Swap(int a, int b, Vector3[] centroids)
    {
        if (a == b) return;
        (_v0[a], _v0[b]) = (_v0[b], _v0[a]);
        (_v1[a], _v1[b]) = (_v1[b], _v1[a]);
        (_v2[a], _v2[b]) = (_v2[b], _v2[a]);
        (_triIndices[a], _triIndices[b]) = (_triIndices[b], _triIndices[a]);
        (centroids[a], centroids[b]) = (centroids[b], centroids[a]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SurfaceArea(float mnX, float mnY, float mnZ, float mxX, float mxY, float mxZ)
    {
        float dx = mxX - mnX, dy = mxY - mnY, dz = mxZ - mnZ;
        return 2f * (dx * dy + dy * dz + dz * dx);
    }

    // ── Ray-AABB intersection ────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RayIntersectsAABB(int node, Vector3 origin, Vector3 invDir, float tMax)
    {
        float t1 = (_minX[node] - origin.X) * invDir.X;
        float t2 = (_maxX[node] - origin.X) * invDir.X;
        float t3 = (_minY[node] - origin.Y) * invDir.Y;
        float t4 = (_maxY[node] - origin.Y) * invDir.Y;
        float t5 = (_minZ[node] - origin.Z) * invDir.Z;
        float t6 = (_maxZ[node] - origin.Z) * invDir.Z;

        float tmin = Math.Max(Math.Max(Math.Min(t1, t2), Math.Min(t3, t4)), Math.Min(t5, t6));
        float tmax = Math.Min(Math.Min(Math.Max(t1, t2), Math.Max(t3, t4)), Math.Max(t5, t6));

        return tmax >= Math.Max(0, tmin) && tmin < tMax;
    }

    // ── Ray-Triangle intersection (Moller-Trumbore) ──────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RayTriangleIntersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float EPSILON = 1e-8f;
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = Vector3.Cross(dir, edge2);
        float a = Vector3.Dot(edge1, h);
        if (a > -EPSILON && a < EPSILON) return float.MaxValue; // parallel

        float f = 1.0f / a;
        var s = origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0 || u > 1) return float.MaxValue;

        var q = Vector3.Cross(s, edge1);
        float v = f * Vector3.Dot(dir, q);
        if (v < 0 || u + v > 1) return float.MaxValue;

        float t = f * Vector3.Dot(edge2, q);
        return t > EPSILON ? t : float.MaxValue;
    }

    // ── Public API: RayCast ──────────────────────────────────────────────

    /// <summary>
    /// Cast a ray and find the nearest triangle hit.
    /// Returns null if no hit within maxDistance.
    /// </summary>
    public RayHit? RayCast(Vector3 origin, Vector3 direction, float maxDistance = float.MaxValue)
    {
        direction = Vector3.Normalize(direction);
        var invDir = new Vector3(
            MathF.Abs(direction.X) > 1e-8f ? 1f / direction.X : (direction.X >= 0 ? 1e30f : -1e30f),
            MathF.Abs(direction.Y) > 1e-8f ? 1f / direction.Y : (direction.Y >= 0 ? 1e30f : -1e30f),
            MathF.Abs(direction.Z) > 1e-8f ? 1f / direction.Z : (direction.Z >= 0 ? 1e30f : -1e30f)
        );

        float bestT = maxDistance;
        int bestTri = -1;

        // Stack-based traversal (no recursion, no allocation)
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0; // root

        while (sp > 0)
        {
            int node = stack[--sp];
            if (!RayIntersectsAABB(node, origin, invDir, bestT)) continue;

            if (_left[node] == -1) // leaf
            {
                for (int i = _triStart[node]; i < _triStart[node] + _triCount[node]; i++)
                {
                    float t = RayTriangleIntersect(origin, direction, _v0[i], _v1[i], _v2[i]);
                    if (t < bestT) { bestT = t; bestTri = i; }
                }
            }
            else
            {
                stack[sp++] = _left[node];
                stack[sp++] = _right[node];
            }
        }

        if (bestTri < 0) return null;

        var hitPoint = origin + direction * bestT;
        var edge1 = _v1[bestTri] - _v0[bestTri];
        var edge2 = _v2[bestTri] - _v0[bestTri];
        var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

        return new RayHit
        {
            Point = hitPoint,
            Normal = normal,
            Distance = bestT,
            TriangleIndex = _triIndices[bestTri],
        };
    }

    // ── Public API: BeamCast ─────────────────────────────────────────────

    /// <summary>
    /// Cast N rays distributed in a cone pattern around the center ray.
    /// Returns the minimum hit distance across all rays.
    /// Used for volumetric collision detection of cylindrical support elements.
    /// </summary>
    public float BeamCast(Vector3 origin, Vector3 direction, float radius, int numRays = 8, float maxDistance = float.MaxValue)
    {
        direction = Vector3.Normalize(direction);
        float minDist = RayCast(origin, direction, maxDistance)?.Distance ?? maxDistance;

        if (numRays <= 1 || radius < 1e-6f) return minDist;

        // Build coordinate frame perpendicular to direction
        var up = MathF.Abs(direction.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(direction, up));
        up = Vector3.Cross(right, direction);

        // Cast rays in a ring at the given radius
        for (int i = 0; i < numRays; i++)
        {
            float angle = 2f * MathF.PI * i / numRays;
            var offset = right * (MathF.Cos(angle) * radius) + up * (MathF.Sin(angle) * radius);
            var rayOrigin = origin + offset;
            var hit = RayCast(rayOrigin, direction, minDist);
            if (hit.HasValue && hit.Value.Distance < minDist)
                minDist = hit.Value.Distance;
        }

        return minDist;
    }

    // ── Public API: IsInside ─────────────────────────────────────────────

    /// <summary>
    /// Test whether a point is inside the mesh.
    /// Uses ray casting along +X axis, counting intersections.
    /// Odd count = inside.
    /// </summary>
    public bool IsInside(Vector3 point)
    {
        // Robust inside/outside test: cast 5 rays in different directions.
        // Take majority vote to handle edge cases (ray through edge/vertex).
        // Jitter rays slightly to avoid exact edge/vertex hits.
        int insideVotes = 0;
        var directions = new[]
        {
            new Vector3(1f, 0.0001f, 0.0002f),   // ~+X with tiny jitter
            new Vector3(0.0003f, 1f, 0.0001f),   // ~+Y with tiny jitter
            new Vector3(0.0002f, 0.0003f, 1f),   // ~+Z with tiny jitter
            new Vector3(-1f, 0.0001f, -0.0002f), // ~-X with tiny jitter
            new Vector3(0.577f, 0.577f, 0.577f),  // diagonal
        };

        foreach (var dir in directions)
        {
            int intersections = CountRayIntersections(point, Vector3.Normalize(dir));
            if ((intersections & 1) == 1) insideVotes++;
        }

        return insideVotes >= 3; // majority (3 of 5) says inside
    }

    private int CountRayIntersections(Vector3 point, Vector3 dir)
    {
        int intersections = 0;

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            int node = stack[--sp];

            // Prune nodes whose AABB can't be hit by the semi-infinite ray
            // The ray starts at `point` and goes in `dir` direction forever
            if (dir.X > 0.5f) // +X ray
            {
                if (point.Y < _minY[node] - 1e-5f || point.Y > _maxY[node] + 1e-5f ||
                    point.Z < _minZ[node] - 1e-5f || point.Z > _maxZ[node] + 1e-5f)
                    continue;
            }
            else if (dir.Y > 0.5f) // +Y ray
            {
                if (point.X < _minX[node] - 1e-5f || point.X > _maxX[node] + 1e-5f ||
                    point.Z < _minZ[node] - 1e-5f || point.Z > _maxZ[node] + 1e-5f)
                    continue;
            }
            else // +Z ray
            {
                if (point.X < _minX[node] - 1e-5f || point.X > _maxX[node] + 1e-5f ||
                    point.Y < _minY[node] - 1e-5f || point.Y > _maxY[node] + 1e-5f)
                    continue;
            }

            if (_left[node] == -1) // leaf
            {
                for (int i = _triStart[node]; i < _triStart[node] + _triCount[node]; i++)
                {
                    float t = RayTriangleIntersect(point, dir, _v0[i], _v1[i], _v2[i]);
                    if (t > 1e-6f && t < 1e30f) intersections++;
                }
            }
            else
            {
                stack[sp++] = _left[node];
                stack[sp++] = _right[node];
            }
        }

        return intersections;
    }

    // ── Public API: ClosestPoint ─────────────────────────────────────────

    /// <summary>
    /// Find the closest point on the mesh surface to the given query point.
    /// Returns null if mesh is empty.
    /// </summary>
    public ClosestPointResult? ClosestPoint(Vector3 point) => ClosestPoint(point, null);

    /// <summary>
    /// Find the closest point on the mesh surface, optionally restricted to allowed triangles.
    /// When allowedTriangles is non-null, only triangles whose original index is in the set
    /// are considered — this prevents wall triangles from stealing overhang queries.
    /// </summary>
    public ClosestPointResult? ClosestPoint(Vector3 point, HashSet<int>? allowedTriangles)
    {
        if (_totalTriangles == 0) return null;

        float bestDistSq = float.MaxValue;
        Vector3 bestPoint = default;
        Vector3 bestNormal = default;
        int bestTri = -1;

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            int node = stack[--sp];

            // Compute distance from point to AABB — prune if farther than best
            float dSq = DistSqToAABB(point, node);
            if (dSq >= bestDistSq) continue;

            if (_left[node] == -1) // leaf
            {
                for (int i = _triStart[node]; i < _triStart[node] + _triCount[node]; i++)
                {
                    // Skip triangles not in the allowed set
                    if (allowedTriangles != null && !allowedTriangles.Contains(_triIndices[i]))
                        continue;

                    var cp = ClosestPointOnTriangle(point, _v0[i], _v1[i], _v2[i]);
                    float d2 = Vector3.DistanceSquared(point, cp);
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        bestPoint = cp;
                        bestTri = i;
                        var e1 = _v1[i] - _v0[i];
                        var e2 = _v2[i] - _v0[i];
                        bestNormal = Vector3.Normalize(Vector3.Cross(e1, e2));
                    }
                }
            }
            else
            {
                // Visit closer child first for better pruning
                float dL = DistSqToAABB(point, _left[node]);
                float dR = DistSqToAABB(point, _right[node]);
                if (dL < dR)
                {
                    stack[sp++] = _right[node];
                    stack[sp++] = _left[node];
                }
                else
                {
                    stack[sp++] = _left[node];
                    stack[sp++] = _right[node];
                }
            }
        }

        if (bestTri < 0) return null;
        return new ClosestPointResult
        {
            Point = bestPoint,
            Normal = bestNormal,
            Distance = MathF.Sqrt(bestDistSq),
            TriangleIndex = _triIndices[bestTri],
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DistSqToAABB(Vector3 point, int node)
    {
        float dx = Math.Max(0, Math.Max(_minX[node] - point.X, point.X - _maxX[node]));
        float dy = Math.Max(0, Math.Max(_minY[node] - point.Y, point.Y - _maxY[node]));
        float dz = Math.Max(0, Math.Max(_minZ[node] - point.Z, point.Z - _maxZ[node]));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// Find the closest point on a triangle to a query point.
    /// Handles all regions: inside face, on edges, on vertices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v0 = d1 / (d1 - d3);
            return a + ab * v0;
        }

        var cp2 = p - c;
        float d5 = Vector3.Dot(ab, cp2), d6 = Vector3.Dot(ac, cp2);
        if (d6 >= 0 && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w0 = d2 / (d2 - d6);
            return a + ac * w0;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            float w0 = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return b + (c - b) * w0;
        }

        float denom = 1f / (va + vb + vc);
        float vf = vb * denom;
        float wf = vc * denom;
        return a + ab * vf + ac * wf;
    }

    // ── Public API: ClosestTriangle ──────────────────────────────────────

    /// <summary>
    /// Find the closest triangle to a point, returning the triangle index.
    /// </summary>
    public int ClosestTriangle(Vector3 point)
    {
        var result = ClosestPoint(point);
        return result?.TriangleIndex ?? -1;
    }

    // ── Public API: Counts ───────────────────────────────────────────────

    public int TriangleCount => _totalTriangles;
    public int NodeCount => _nodeCount;

    // ── Result types ─────────────────────────────────────────────────────

    public readonly struct RayHit
    {
        public required Vector3 Point { get; init; }
        public required Vector3 Normal { get; init; }
        public required float Distance { get; init; }
        public required int TriangleIndex { get; init; }
    }

    public readonly struct ClosestPointResult
    {
        public required Vector3 Point { get; init; }
        public required Vector3 Normal { get; init; }
        public required float Distance { get; init; }
        public required int TriangleIndex { get; init; }
    }
}
