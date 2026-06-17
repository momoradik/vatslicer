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
    /// Apply a rotation (quaternion) to all vertices and normals. Returns a new mesh.
    /// </summary>
    public StlMesh Rotate(Quaternion rotation)
    {
        var newVerts = new Vector3[Vertices.Length];
        var newNormals = new Vector3[FileNormals.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < Vertices.Length; i++)
        {
            var v = Vector3.Transform(Vertices[i], rotation);
            newVerts[i] = v;
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        for (int i = 0; i < FileNormals.Length; i++)
            newNormals[i] = Vector3.Transform(FileNormals[i], rotation);

        return new StlMesh(newVerts, newNormals, min, max);
    }

    /// <summary>
    /// Auto-detect format and parse: binary STL, ASCII STL, or OBJ.
    /// </summary>
    public static StlMesh FromFile(byte[] data, string? fileName = null)
    {
        // Check for ASCII STL: starts with "solid " (but binary STL can too if header has "solid")
        // Heuristic: if first non-whitespace is "solid" AND file doesn't match binary STL size, try ASCII
        string ext = (fileName ?? "").ToLowerInvariant();
        if (ext.EndsWith(".obj"))
            return FromObj(System.Text.Encoding.UTF8.GetString(data));
        if (ext.EndsWith(".3mf"))
            return From3mf(data);

        // Check if it looks like ASCII STL
        bool looksAscii = false;
        if (data.Length > 5)
        {
            var header = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(80, data.Length)).TrimStart();
            looksAscii = header.StartsWith("solid", StringComparison.OrdinalIgnoreCase);
            if (looksAscii && data.Length >= 84)
            {
                // Double-check: if binary size matches, it's binary (binary STL can start with "solid" in header)
                var triCount = BitConverter.ToUInt32(data, 80);
                if (84 + triCount * 50 == (ulong)data.Length)
                    looksAscii = false;
            }
        }

        if (looksAscii || ext.EndsWith(".stla"))
            return FromAsciiStl(System.Text.Encoding.UTF8.GetString(data));

        return FromBinary(data);
    }

    /// <summary>
    /// Parse an ASCII STL string.
    /// </summary>
    public static StlMesh FromAsciiStl(string text)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var currentNormal = Vector3.UnitZ;
        var facetVerts = new List<Vector3>(3);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                    currentNormal = new Vector3(float.Parse(parts[2]), float.Parse(parts[3]), float.Parse(parts[4]));
                facetVerts.Clear();
            }
            else if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    var v = new Vector3(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));
                    facetVerts.Add(v);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
            else if (line.StartsWith("endfacet", StringComparison.OrdinalIgnoreCase))
            {
                if (facetVerts.Count == 3)
                {
                    verts.AddRange(facetVerts);
                    normals.Add(currentNormal);
                }
                facetVerts.Clear();
            }
        }

        return new StlMesh(verts.ToArray(), normals.ToArray(), min, max);
    }

    /// <summary>
    /// Parse a Wavefront OBJ string (vertices + faces, no materials).
    /// </summary>
    public static StlMesh FromObj(string text)
    {
        var objVerts = new List<Vector3>();
        var triVerts = new List<Vector3>();
        var triNormals = new List<Vector3>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    var v = new Vector3(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));
                    objVerts.Add(v);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Parse face indices (1-based, may include /texcoord/normal)
                var indices = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    var idxStr = parts[i].Split('/')[0];
                    if (int.TryParse(idxStr, out int idx))
                        indices.Add(idx - 1); // OBJ is 1-based
                }
                // Triangulate face (fan from first vertex)
                for (int i = 1; i < indices.Count - 1; i++)
                {
                    var v0 = objVerts[Math.Clamp(indices[0], 0, objVerts.Count - 1)];
                    var v1 = objVerts[Math.Clamp(indices[i], 0, objVerts.Count - 1)];
                    var v2 = objVerts[Math.Clamp(indices[i + 1], 0, objVerts.Count - 1)];
                    triVerts.Add(v0); triVerts.Add(v1); triVerts.Add(v2);
                    var normal = Vector3.Cross(v1 - v0, v2 - v0);
                    float len = normal.Length();
                    triNormals.Add(len > 1e-8f ? normal / len : Vector3.UnitZ);
                }
            }
        }

        return new StlMesh(triVerts.ToArray(), triNormals.ToArray(), min, max);
    }

    /// <summary>
    /// Parse a 3MF file (ZIP containing 3D/3dmodel.model XML).
    /// </summary>
    public static StlMesh From3mf(byte[] data)
    {
        using var ms = new System.IO.MemoryStream(data);
        using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

        // Find the model file (typically 3D/3dmodel.model)
        var modelEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
        if (modelEntry == null)
            throw new InvalidOperationException("3MF archive does not contain a .model file");

        using var stream = modelEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

        // Parse vertices
        var verticesList = new List<Vector3>();
        var meshElement = doc.Descendants(ns + "mesh").FirstOrDefault();
        if (meshElement == null)
            throw new InvalidOperationException("3MF model has no mesh element");

        foreach (var vertex in meshElement.Descendants(ns + "vertex"))
        {
            float x = float.Parse(vertex.Attribute("x")?.Value ?? "0");
            float y = float.Parse(vertex.Attribute("y")?.Value ?? "0");
            float z = float.Parse(vertex.Attribute("z")?.Value ?? "0");
            verticesList.Add(new Vector3(x, y, z));
        }

        // Parse triangles
        var triVerts = new List<Vector3>();
        var triNormals = new List<Vector3>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var triangle in meshElement.Descendants(ns + "triangle"))
        {
            int v1 = int.Parse(triangle.Attribute("v1")?.Value ?? "0");
            int v2 = int.Parse(triangle.Attribute("v2")?.Value ?? "0");
            int v3 = int.Parse(triangle.Attribute("v3")?.Value ?? "0");

            if (v1 >= verticesList.Count || v2 >= verticesList.Count || v3 >= verticesList.Count)
                continue;

            var a = verticesList[v1]; var b = verticesList[v2]; var c = verticesList[v3];
            triVerts.Add(a); triVerts.Add(b); triVerts.Add(c);
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));

            var normal = Vector3.Cross(b - a, c - a);
            float len = normal.Length();
            triNormals.Add(len > 1e-8f ? normal / len : Vector3.UnitZ);
        }

        return new StlMesh(triVerts.ToArray(), triNormals.ToArray(), min, max);
    }

    /// <summary>
    /// Recompute face normals from vertex winding order (cross product of edges).
    /// For meshes with unreliable STL normals (flipped/zero), this provides
    /// consistent normals based on the actual geometry. Returns a new mesh.
    /// </summary>
    public StlMesh RecomputeNormals()
    {
        var newNormals = new Vector3[TriangleCount];
        for (int t = 0; t < TriangleCount; t++)
        {
            var v0 = Vertices[t * 3];
            var v1 = Vertices[t * 3 + 1];
            var v2 = Vertices[t * 3 + 2];
            var edge1 = v1 - v0;
            var edge2 = v2 - v0;
            var normal = Vector3.Cross(edge1, edge2);
            float len = normal.Length();
            newNormals[t] = len > 1e-8f ? normal / len : Vector3.UnitZ;
        }
        return new StlMesh(Vertices, newNormals, Min, Max);
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
