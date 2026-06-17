using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates calibration test models for tuning printer settings.
/// Creates STL meshes of standard test geometries:
/// - XY resolution test (thin walls + gaps)
/// - Z accuracy test (stepped wedge)
/// - Exposure test (pillars at different diameters)
/// </summary>
public static class CalibrationPrintGenerator
{
    /// <summary>
    /// Generate an XY resolution test model: series of walls and gaps at decreasing sizes.
    /// </summary>
    public static StlMesh GenerateXYResolutionTest(
        float minFeatureMm = 0.1f,
        float maxFeatureMm = 1.0f,
        int steps = 5,
        float heightMm = 5f)
    {
        var verts = new List<Vector3>();
        float x = 0;

        for (int i = 0; i < steps; i++)
        {
            float size = maxFeatureMm - (maxFeatureMm - minFeatureMm) * i / (steps - 1);
            // Wall
            AddBox(verts, x, 0, 0, x + size, 5, heightMm);
            x += size + 1; // gap
            // Gap (empty space of same width)
            x += size;
        }

        return BuildMesh(verts);
    }

    /// <summary>
    /// Generate a stepped wedge for Z accuracy testing.
    /// Each step is 1mm taller, 2mm wide.
    /// </summary>
    public static StlMesh GenerateZAccuracyTest(int steps = 10, float stepHeightMm = 1f)
    {
        var verts = new List<Vector3>();
        for (int i = 0; i < steps; i++)
        {
            float z = (i + 1) * stepHeightMm;
            AddBox(verts, i * 3, 0, 0, i * 3 + 2, 5, z);
        }
        return BuildMesh(verts);
    }

    private static void AddBox(List<Vector3> v, float x1, float y1, float z1, float x2, float y2, float z2)
    {
        void Q(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { v.Add(a); v.Add(b); v.Add(c); v.Add(a); v.Add(c); v.Add(d); }
        var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1); var v010 = new Vector3(x1, y2, z1);
        var v110 = new Vector3(x2, y2, z1); var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
        var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
        Q(v001, v101, v111, v011); Q(v000, v010, v110, v100); Q(v100, v110, v111, v101);
        Q(v000, v001, v011, v010); Q(v010, v011, v111, v110); Q(v000, v100, v101, v001);
    }

    private static StlMesh BuildMesh(List<Vector3> verts)
    {
        int tc = verts.Count / 3;
        var d = new byte[84 + tc * 50];
        BitConverter.GetBytes((uint)tc).CopyTo(d, 80);
        int o = 84;
        for (int t = 0; t < tc; t++)
        {
            var a = verts[t * 3]; var b = verts[t * 3 + 1]; var c = verts[t * 3 + 2];
            var n = Vector3.Cross(b - a, c - a);
            float l = n.Length(); if (l > 1e-6f) n /= l; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(d, o); BitConverter.GetBytes(n.Y).CopyTo(d, o + 4); BitConverter.GetBytes(n.Z).CopyTo(d, o + 8); o += 12;
            for (int i = 0; i < 3; i++) { BitConverter.GetBytes(verts[t * 3 + i].X).CopyTo(d, o); BitConverter.GetBytes(verts[t * 3 + i].Y).CopyTo(d, o + 4); BitConverter.GetBytes(verts[t * 3 + i].Z).CopyTo(d, o + 8); o += 12; }
            o += 2;
        }
        return StlMesh.FromBinary(d);
    }
}
