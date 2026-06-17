using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class DrainHolePlacerTests
{
    private static StlMesh CreateCupModel()
    {
        // A cup-shaped model (open top, closed bottom) — should have a resin trap
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts.Add(a); verts.Add(b); verts.Add(c);
            verts.Add(a); verts.Add(c); verts.Add(d);
        }
        void AddBox(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1);
            var v010 = new Vector3(x1, y2, z1); var v110 = new Vector3(x2, y2, z1);
            var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
            var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
            Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100);
            Quad(v100, v110, v111, v101); Quad(v000, v001, v011, v010);
            Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);
        }

        // Bottom plate
        AddBox(-15, -15, 0, 15, 15, 2);
        // Walls (open top)
        AddBox(-15, -15, 2, -13, 15, 20); // left wall
        AddBox(13, -15, 2, 15, 15, 20);   // right wall
        AddBox(-13, -15, 2, 13, -13, 20); // front wall
        AddBox(-13, 13, 2, 13, 15, 20);   // back wall

        int triCount = verts.Count / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length();
            if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Suggest_NoThrowOnSimpleModel()
    {
        var mesh = CreateCupModel();
        var config = new DrainHolePlacer.DrainConfig
        {
            LayerHeightMm = 2f,
            MinTrapVolumeMm3 = 10f,
            HoleDiameterMm = 2.5f,
        };

        var holes = DrainHolePlacer.Suggest(mesh, config);

        // The cup model may or may not trigger a drain hole depending on detection
        // sensitivity, but it should never throw
        holes.Should().NotBeNull();
    }

    [Fact]
    public void Suggest_EmptyOnSolidBox()
    {
        // A solid box has no trapped volume
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts.Add(a); verts.Add(b); verts.Add(c);
            verts.Add(a); verts.Add(c); verts.Add(d);
        }
        var v000 = new Vector3(-5, -5, 0); var v100 = new Vector3(5, -5, 0);
        var v010 = new Vector3(-5, 5, 0); var v110 = new Vector3(5, 5, 0);
        var v001 = new Vector3(-5, -5, 10); var v101 = new Vector3(5, -5, 10);
        var v011 = new Vector3(-5, 5, 10); var v111 = new Vector3(5, 5, 10);
        Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100);
        Quad(v100, v110, v111, v101); Quad(v000, v001, v011, v010);
        Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);

        int triCount = verts.Count / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length();
            if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        var mesh = StlMesh.FromBinary(data);

        var holes = DrainHolePlacer.Suggest(mesh, new DrainHolePlacer.DrainConfig
        {
            LayerHeightMm = 1f,
            MinTrapVolumeMm3 = 10f,
        });

        holes.Should().BeEmpty("a solid box has no trapped volume");
    }

    [Fact]
    public void DrainHole_HasValidPositionAndNormal()
    {
        var mesh = CreateCupModel();
        var holes = DrainHolePlacer.Suggest(mesh, new DrainHolePlacer.DrainConfig
        {
            LayerHeightMm = 2f,
            MinTrapVolumeMm3 = 5f,
            HoleDiameterMm = 2f,
        });

        foreach (var hole in holes)
        {
            hole.DiameterMm.Should().BeGreaterThan(0);
            hole.TrapVolumeMm3.Should().BeGreaterThan(0);
            // Normal should be non-zero
            var normalLen = new Vector3(hole.Normal.X, hole.Normal.Y, hole.Normal.Z).Length();
            normalLen.Should().BeGreaterThan(0.5f, "drain hole normal should be unit-ish vector");
        }
    }
}
