using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Merges multiple IndexedTriangleSets into a single watertight mesh.
/// Performs vertex welding, degenerate removal, and basic manifold validation.
/// </summary>
public static class MeshMerger
{
    /// <summary>
    /// Merge result with validation info.
    /// </summary>
    public sealed class MergeResult
    {
        public required IndexedTriangleSet Mesh { get; init; }
        public required int OriginalVertices { get; init; }
        public required int WeldedVertices { get; init; }
        public required int OriginalFaces { get; init; }
        public required int FinalFaces { get; init; }
        public required int DegenerateFacesRemoved { get; init; }
        public required int NonManifoldEdges { get; init; }
    }

    /// <summary>
    /// Merge all meshes into a single mesh, weld vertices, remove degenerates.
    /// </summary>
    public static MergeResult MergeAll(IEnumerable<IndexedTriangleSet> meshes, float weldEpsilon = 0.001f)
    {
        var combined = new IndexedTriangleSet();
        foreach (var m in meshes)
            combined.Merge(m);

        int origVerts = combined.VertexCount;
        int origFaces = combined.FaceCount;

        // Weld duplicate vertices
        combined.WeldVertices(weldEpsilon);

        int degRemoved = origFaces - combined.FaceCount;

        // Count non-manifold edges (edges shared by != 2 faces)
        int nonManifold = CountNonManifoldEdges(combined);

        return new MergeResult
        {
            Mesh = combined,
            OriginalVertices = origVerts,
            WeldedVertices = combined.VertexCount,
            OriginalFaces = origFaces,
            FinalFaces = combined.FaceCount,
            DegenerateFacesRemoved = degRemoved,
            NonManifoldEdges = nonManifold,
        };
    }

    /// <summary>
    /// Count edges that are not shared by exactly 2 faces (non-manifold).
    /// </summary>
    public static int CountNonManifoldEdges(IndexedTriangleSet mesh)
    {
        var edgeCounts = new Dictionary<long, int>();
        foreach (var (a, b, c) in mesh.Faces)
        {
            IncrementEdge(edgeCounts, a, b);
            IncrementEdge(edgeCounts, b, c);
            IncrementEdge(edgeCounts, c, a);
        }

        int nonManifold = 0;
        foreach (var count in edgeCounts.Values)
        {
            if (count != 2) nonManifold++;
        }
        return nonManifold;
    }

    private static void IncrementEdge(Dictionary<long, int> edgeCounts, int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        long key = ((long)lo << 32) | (uint)hi;
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
    }
}
