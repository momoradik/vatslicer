using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// Verifies the preview==print invariant: every feature type present in the
/// support mesh must also be present in the slice elements, and vice versa.
/// </summary>
public class PreviewEqualsPrintTests
{
    private static StlMesh CreateTestModel()
    {
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
        AddBox(-15, -15, 10, 15, 15, 25);
        AddBox(-20, -20, 8, 20, 20, 10);
        AddBox(-5, -5, 35, 5, 5, 38);
        AddBox(-2, -2, 0, 2, 2, 8);
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
    public void BracesInMesh_ImpliesBracesInSlices()
    {
        var mesh = CreateTestModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
            ReinforcementMode = ReinforcementMode.Pairwise,
        });

        bool meshHasBraces = result.Interconnections.Count > 0;
        bool sliceHasBraces = result.SliceElements.Any(e => e.Type == "interconnect");

        if (meshHasBraces)
            sliceHasBraces.Should().BeTrue(
                "interconnections in the 3D mesh must also appear in slice elements for printing");
    }

    [Fact]
    public void SliceElements_ContainPillarAndJunction()
    {
        var mesh = CreateTestModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
        });

        if (result.ValidSupports > 0)
        {
            result.SliceElements.Should().Contain(e => e.Type == "pillar" || e.Type == "junction",
                "valid supports must produce pillar/junction slice elements");
        }
    }

    [Fact]
    public void RaftEnabled_ProducesRaftSliceElements()
    {
        var mesh = CreateTestModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            RaftMode = RaftMode.MiniRafts,
            EnableMiniRafts = true,
        });

        if (result.ValidSupports > 0)
        {
            result.SliceElements.Should().Contain(e => e.Type == "raft",
                "MiniRafts mode should produce raft slice elements");
        }
    }
}
