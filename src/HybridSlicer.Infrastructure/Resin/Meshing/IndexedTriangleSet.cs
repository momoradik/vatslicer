using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Indexed triangle mesh: vertices stored once, faces reference vertex indices.
/// This is the standard mesh representation used by PrusaSlicer for support geometry.
/// Supports merging, vertex welding, and STL export.
/// </summary>
public sealed class IndexedTriangleSet
{
    public List<Vector3> Vertices { get; } = new();
    public List<(int a, int b, int c)> Faces { get; } = new();

    public int VertexCount => Vertices.Count;
    public int FaceCount => Faces.Count;

    /// <summary>
    /// Add a vertex, return its index.
    /// </summary>
    public int AddVertex(Vector3 v)
    {
        int idx = Vertices.Count;
        Vertices.Add(v);
        return idx;
    }

    /// <summary>
    /// Add a face (triangle) from 3 vertex indices.
    /// </summary>
    public void AddFace(int a, int b, int c) => Faces.Add((a, b, c));

    /// <summary>
    /// Merge another mesh into this one, offsetting vertex indices.
    /// </summary>
    public void Merge(IndexedTriangleSet other)
    {
        int offset = Vertices.Count;
        Vertices.AddRange(other.Vertices);
        foreach (var (a, b, c) in other.Faces)
            Faces.Add((a + offset, b + offset, c + offset));
    }

    /// <summary>
    /// Apply a rotation (quaternion) and translation to all vertices.
    /// </summary>
    public void Transform(Quaternion rotation, Vector3 translation)
    {
        for (int i = 0; i < Vertices.Count; i++)
            Vertices[i] = Vector3.Transform(Vertices[i], rotation) + translation;
    }

    /// <summary>
    /// Weld duplicate vertices within epsilon distance.
    /// Reduces vertex count and produces cleaner meshes.
    /// </summary>
    public void WeldVertices(float epsilon = 0.001f)
    {
        if (Vertices.Count == 0) return;

        float eps2 = epsilon * epsilon;
        var remap = new int[Vertices.Count];
        var newVerts = new List<Vector3>();

        for (int i = 0; i < Vertices.Count; i++)
        {
            int merged = -1;
            // Search recent vertices (typically nearby in the mesh)
            for (int j = newVerts.Count - 1; j >= Math.Max(0, newVerts.Count - 200); j--)
            {
                if (Vector3.DistanceSquared(Vertices[i], newVerts[j]) < eps2)
                {
                    merged = j;
                    break;
                }
            }

            if (merged >= 0)
            {
                remap[i] = merged;
            }
            else
            {
                remap[i] = newVerts.Count;
                newVerts.Add(Vertices[i]);
            }
        }

        // Rebuild face list with remapped indices, removing degenerates
        var newFaces = new List<(int, int, int)>();
        foreach (var (a, b, c) in Faces)
        {
            int na = remap[a], nb = remap[b], nc = remap[c];
            if (na != nb && nb != nc && na != nc) // skip degenerate
                newFaces.Add((na, nb, nc));
        }

        Vertices.Clear();
        Vertices.AddRange(newVerts);
        Faces.Clear();
        Faces.AddRange(newFaces);
    }

    /// <summary>
    /// Export as binary STL byte array.
    /// </summary>
    public byte[] ToStlBinary()
    {
        int triCount = Faces.Count;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);

        int offset = 84;
        foreach (var (a, b, c) in Faces)
        {
            var v0 = Vertices[a];
            var v1 = Vertices[b];
            var v2 = Vertices[c];

            // Compute face normal
            var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
            if (float.IsNaN(normal.X)) normal = Vector3.UnitZ;

            BitConverter.GetBytes(normal.X).CopyTo(data, offset); offset += 4;
            BitConverter.GetBytes(normal.Y).CopyTo(data, offset); offset += 4;
            BitConverter.GetBytes(normal.Z).CopyTo(data, offset); offset += 4;

            foreach (var v in new[] { v0, v1, v2 })
            {
                BitConverter.GetBytes(v.X).CopyTo(data, offset); offset += 4;
                BitConverter.GetBytes(v.Y).CopyTo(data, offset); offset += 4;
                BitConverter.GetBytes(v.Z).CopyTo(data, offset); offset += 4;
            }
            offset += 2; // attribute byte count
        }

        return data;
    }
}
