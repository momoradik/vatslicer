using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class BitwiseIslandDetectorTests
{
    private static StlMesh BuildModel()
    {
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(a); verts.Add(c); verts.Add(d); }
        void AddBox(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1); var v010 = new Vector3(x1, y2, z1);
            var v110 = new Vector3(x2, y2, z1); var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
            var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
            Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100); Quad(v100, v110, v111, v101);
            Quad(v000, v001, v011, v010); Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);
        }
        // Pillar to plate
        AddBox(-2, -2, 0, 2, 2, 8);
        // Floating island (disconnected from pillar)
        AddBox(-5, -5, 20, 5, 5, 22);

        int tc = verts.Count / 3;
        var d = new byte[84 + tc * 50]; BitConverter.GetBytes((uint)tc).CopyTo(d, 80); int o = 84;
        for (int t = 0; t < tc; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float l = n.Length(); if (l > 1e-6f) n /= l; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(d, o); BitConverter.GetBytes(n.Y).CopyTo(d, o + 4);
            BitConverter.GetBytes(n.Z).CopyTo(d, o + 8); o += 12;
            for (int v = 0; v < 3; v++)
            { BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(d, o); BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(d, o + 4);
              BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(d, o + 8); o += 12; }
            o += 2;
        }
        return StlMesh.FromBinary(d);
    }

    [Fact]
    public void FloatingIsland_Detected()
    {
        var mesh = BuildModel();
        var bitstack = OccupancyBitstack.Build(mesh, 0.5f, 0.5f);
        var islands = BitwiseIslandDetector.Detect(bitstack, meshMinZ: mesh.Min.Z);
        islands.Should().NotBeEmpty("floating block at z=20 is disconnected from pillar at z=0-8");
    }

    [Fact]
    public void Islands_HavePositiveArea()
    {
        var mesh = BuildModel();
        var bitstack = OccupancyBitstack.Build(mesh, 0.5f, 0.5f);
        var islands = BitwiseIslandDetector.Detect(bitstack, meshMinZ: mesh.Min.Z);
        foreach (var island in islands)
        {
            island.AreaMm2.Should().BeGreaterThan(0);
            island.CellCount.Should().BeGreaterThan(0);
        }
    }
}
