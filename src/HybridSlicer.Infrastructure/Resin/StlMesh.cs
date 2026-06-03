using System.Numerics;
using System.Runtime.InteropServices;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>
/// Lightweight triangle mesh loaded from binary STL.
/// Stored as flat arrays for cache-friendly iteration.
/// </summary>
public sealed class StlMesh
{
    public Vector3[] Vertices { get; }  // 3 vertices per triangle
    /// <summary>Per-triangle normals from the STL file header (outward-pointing from CAD).</summary>
    public Vector3[] FileNormals { get; }
    public int TriangleCount { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    internal StlMesh(Vector3[] vertices, Vector3[] fileNormals, Vector3 min, Vector3 max)
    {
        Vertices = vertices;
        FileNormals = fileNormals;
        TriangleCount = vertices.Length / 3;
        Min = min;
        Max = max;
    }

    /// <summary>
    /// Parse a binary STL from a byte array. Fast — no allocations beyond the vertex array.
    /// </summary>
    public static StlMesh FromBinary(byte[] data)
    {
        if (data.Length < 84)
            throw new InvalidOperationException("STL file too small to be valid.");

        var triCount = BitConverter.ToUInt32(data, 80);
        var expectedSize = 84 + triCount * 50;
        if ((ulong)data.Length < expectedSize)
            throw new InvalidOperationException($"STL claims {triCount} triangles but file is too short.");

        var vertices = new Vector3[triCount * 3];
        var fileNormals = new Vector3[triCount];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        var offset = 84;
        for (uint i = 0; i < triCount; i++)
        {
            // Read normal from STL header (outward-pointing from CAD software)
            var nx = BitConverter.ToSingle(data, offset);
            var ny = BitConverter.ToSingle(data, offset + 4);
            var nz = BitConverter.ToSingle(data, offset + 8);
            fileNormals[i] = new Vector3(nx, ny, nz);
            offset += 12;

            for (int v = 0; v < 3; v++)
            {
                var x = BitConverter.ToSingle(data, offset);
                var y = BitConverter.ToSingle(data, offset + 4);
                var z = BitConverter.ToSingle(data, offset + 8);
                offset += 12;

                var vert = new Vector3(x, y, z);
                vertices[i * 3 + v] = vert;
                min = Vector3.Min(min, vert);
                max = Vector3.Max(max, vert);
            }

            // Skip attribute byte count
            offset += 2;
        }

        return new StlMesh(vertices, fileNormals, min, max);
    }

    /// <summary>
    /// Apply a transform: translate by offset, scale uniformly.
    /// Returns a new mesh.
    /// </summary>
    public StlMesh Transform(Vector3 translate, float scale)
    {
        var newVerts = new Vector3[Vertices.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < Vertices.Length; i++)
        {
            var v = (Vertices[i] + translate) * scale;
            newVerts[i] = v;
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        return new StlMesh(newVerts, FileNormals, min, max); // normals unchanged by translate+scale
    }

    /// <summary>
    /// Find triangle indices that are overhang candidates at a given Z height.
    /// Returns indices of triangles that span Z and have downward-facing normals.
    /// </summary>
    public HashSet<int> FindOverhangTrianglesAtZ(float z, float zTolerance = 1.0f, float maxNormalZ = -0.1f)
    {
        var result = new HashSet<int>();
        for (int t = 0; t < TriangleCount; t++)
        {
            var v0 = Vertices[t * 3];
            var v1 = Vertices[t * 3 + 1];
            var v2 = Vertices[t * 3 + 2];

            float minZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
            float maxZ2 = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));

            // Triangle must span the Z height (within tolerance)
            if (minZ > z + zTolerance || maxZ2 < z - zTolerance) continue;

            // Normal must point downward
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            float len = normal.Length();
            if (len < 1e-8f) continue;
            normal /= len;

            if (normal.Z < maxNormalZ)
                result.Add(t);
        }
        return result;
    }
}
