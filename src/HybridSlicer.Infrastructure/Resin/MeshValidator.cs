using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>
/// Validates and optionally repairs triangle meshes for resin slicing.
/// Conservative: only repairs issues that are safe to fix automatically.
/// </summary>
public static class MeshValidator
{
    public sealed class ValidationResult
    {
        public bool IsValid { get; init; }
        public int TriangleCount { get; init; }
        public int DegenerateTriangles { get; init; }
        public int NanInfVertices { get; init; }
        public int FlippedNormals { get; init; }
        public int NonManifoldEdges { get; init; }
        public int OpenEdges { get; init; }
        public bool BoundsValid { get; init; }
        public float VolumeMm3 { get; init; }
        public bool Repaired { get; init; }
        public int TrianglesRemoved { get; init; }
        public int NormalsFixed { get; init; }
        public List<string> Warnings { get; init; } = [];
        public List<string> Errors { get; init; } = [];
    }

    /// <summary>
    /// Validate a mesh and return a detailed report. Does not modify the mesh.
    /// </summary>
    public static ValidationResult Validate(StlMesh mesh)
    {
        int degenerateCount = 0;
        int nanInfCount = 0;
        int flippedNormals = 0;
        var warnings = new List<string>();
        var errors = new List<string>();

        // Check for NaN/Inf vertices
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
            if (float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) ||
                float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z))
                nanInfCount++;
        }

        // Check for degenerate triangles (zero area)
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            var area = cross.Length() * 0.5f;
            if (area < 1e-10f)
                degenerateCount++;
        }

        // Compute signed volume (positive = consistent outward normals)
        float signedVolume = 0;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            signedVolume += Vector3.Dot(v0, Vector3.Cross(v1, v2)) / 6.0f;
        }

        if (signedVolume < 0)
            flippedNormals = mesh.TriangleCount; // all normals inverted

        // Edge analysis for manifold/open edge detection
        var edgeCounts = new Dictionary<(long, long), int>();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                var vi0 = QuantizeVertex(mesh.Vertices[t * 3 + e]);
                var vi1 = QuantizeVertex(mesh.Vertices[t * 3 + (e + 1) % 3]);
                var key = vi0 < vi1 ? (vi0, vi1) : (vi1, vi0);
                edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
            }
        }

        int openEdges = edgeCounts.Count(e => e.Value == 1);
        int nonManifoldEdges = edgeCounts.Count(e => e.Value > 2);

        // Bounds check
        var size = mesh.Max - mesh.Min;
        bool boundsValid = size.X > 0 && size.Y > 0 && size.Z > 0 &&
                           size.X < 10000 && size.Y < 10000 && size.Z < 10000;

        // Build warnings/errors
        if (nanInfCount > 0)
            errors.Add($"{nanInfCount} vertices have NaN/Infinity values — mesh is corrupted.");
        if (degenerateCount > 0)
            warnings.Add($"{degenerateCount} degenerate (zero-area) triangles detected.");
        if (flippedNormals > 0)
            warnings.Add("Mesh normals appear inverted (negative volume). Auto-repair can fix this.");
        if (openEdges > 0)
            warnings.Add($"{openEdges} open (boundary) edges — mesh is not watertight.");
        if (nonManifoldEdges > 0)
            warnings.Add($"{nonManifoldEdges} non-manifold edges — some edges are shared by 3+ triangles.");
        if (!boundsValid)
            errors.Add("Mesh bounds are invalid or extremely large.");
        if (mesh.TriangleCount == 0)
            errors.Add("Mesh contains no triangles.");

        bool isValid = errors.Count == 0 && nanInfCount == 0;

        return new ValidationResult
        {
            IsValid = isValid,
            TriangleCount = mesh.TriangleCount,
            DegenerateTriangles = degenerateCount,
            NanInfVertices = nanInfCount,
            FlippedNormals = flippedNormals,
            NonManifoldEdges = nonManifoldEdges,
            OpenEdges = openEdges,
            BoundsValid = boundsValid,
            VolumeMm3 = MathF.Abs(signedVolume),
            Warnings = warnings,
            Errors = errors,
        };
    }

    /// <summary>
    /// Attempt safe automatic repair. Returns a new mesh if repairs were made, or the original if none needed.
    /// </summary>
    public static (StlMesh mesh, ValidationResult result) ValidateAndRepair(byte[] stlData)
    {
        var mesh = StlMesh.FromBinary(stlData);
        var initial = Validate(mesh);

        if (initial.IsValid && initial.Warnings.Count == 0)
            return (mesh, initial); // clean mesh, no work needed

        int removed = 0;
        int normalsFixed = 0;
        var vertices = mesh.Vertices.ToArray(); // copy for modification

        // Repair 1: Remove degenerate triangles
        if (initial.DegenerateTriangles > 0)
        {
            var cleanVerts = new List<Vector3>();
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var v0 = vertices[t * 3];
                var v1 = vertices[t * 3 + 1];
                var v2 = vertices[t * 3 + 2];
                var cross = Vector3.Cross(v1 - v0, v2 - v0);
                if (cross.Length() * 0.5f >= 1e-10f)
                {
                    cleanVerts.Add(v0); cleanVerts.Add(v1); cleanVerts.Add(v2);
                }
                else
                    removed++;
            }
            vertices = cleanVerts.ToArray();
        }

        // Repair 2: Fix flipped normals (flip all triangles if volume is negative)
        if (initial.FlippedNormals > 0 && vertices.Length >= 9)
        {
            float sv = 0;
            int triCount = vertices.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                sv += Vector3.Dot(vertices[t * 3], Vector3.Cross(vertices[t * 3 + 1], vertices[t * 3 + 2])) / 6.0f;
            }

            if (sv < 0)
            {
                // Swap v1 and v2 of every triangle to flip winding
                for (int t = 0; t < triCount; t++)
                {
                    (vertices[t * 3 + 1], vertices[t * 3 + 2]) = (vertices[t * 3 + 2], vertices[t * 3 + 1]);
                }
                normalsFixed = triCount;
            }
        }

        // Repair 3: Clamp NaN/Inf to zero (last resort, prevents crashes)
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            if (float.IsNaN(v.X) || float.IsInfinity(v.X)) v = new Vector3(0, v.Y, v.Z);
            if (float.IsNaN(v.Y) || float.IsInfinity(v.Y)) v = new Vector3(v.X, 0, v.Z);
            if (float.IsNaN(v.Z) || float.IsInfinity(v.Z)) v = new Vector3(v.X, v.Y, 0);
            vertices[i] = v;
        }

        // Rebuild mesh from repaired vertices
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }

        // Recompute normals from vertex winding for repaired mesh
        int triCount2 = vertices.Length / 3;
        var repairedNormals = new Vector3[triCount2];
        for (int t = 0; t < triCount2; t++)
        {
            var e1 = vertices[t * 3 + 1] - vertices[t * 3];
            var e2 = vertices[t * 3 + 2] - vertices[t * 3];
            var n = Vector3.Cross(e1, e2);
            float nl = n.Length();
            repairedNormals[t] = nl > 1e-8f ? n / nl : Vector3.UnitZ;
        }
        var repaired = new StlMesh(vertices, repairedNormals, min, max);
        var finalResult = Validate(repaired);

        return (repaired, new ValidationResult
        {
            IsValid = finalResult.IsValid,
            TriangleCount = repaired.TriangleCount,
            DegenerateTriangles = finalResult.DegenerateTriangles,
            NanInfVertices = finalResult.NanInfVertices,
            FlippedNormals = finalResult.FlippedNormals,
            NonManifoldEdges = finalResult.NonManifoldEdges,
            OpenEdges = finalResult.OpenEdges,
            BoundsValid = finalResult.BoundsValid,
            VolumeMm3 = finalResult.VolumeMm3,
            Repaired = removed > 0 || normalsFixed > 0,
            TrianglesRemoved = removed,
            NormalsFixed = normalsFixed,
            Warnings = finalResult.Warnings,
            Errors = finalResult.Errors,
        });
    }

    // Quantize vertex to integer key for edge matching (10μm precision)
    private static long QuantizeVertex(Vector3 v)
    {
        long x = (long)(v.X * 100);
        long y = (long)(v.Y * 100);
        long z = (long)(v.Z * 100);
        return x * 1_000_000_000L * 1_000_000_000L + y * 1_000_000_000L + z;
    }

}
