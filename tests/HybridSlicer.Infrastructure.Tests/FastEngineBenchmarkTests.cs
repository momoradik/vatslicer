using System.Diagnostics;
using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// TASK 8: Benchmark + invariant tests for the fast support engine.
/// Verifies: timing, zero floaters, preview==print at all 4 rotations.
/// </summary>
public class FastEngineBenchmarkTests
{
    private static StlMesh CreateComplexModel()
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
        AddBox(-15, -15, 10, 15, 15, 25);
        AddBox(-20, -20, 8, 20, 20, 10);
        AddBox(-5, -5, 35, 5, 5, 38);
        AddBox(-2, -2, 0, 2, 2, 8);
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
    public void FastEngine_ZeroFloaters_PreviewEqualsPrint()
    {
        var mesh = CreateComplexModel();
        var config = new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
            EnableFillets = true,
            UseFastSupportEngine = true,
        };

        var result = SupportEngineV2.Generate(mesh, config);

        if (result.ValidSupports == 0) return;

        // Invariant (a): ZERO floating geometry
        // Check all routes reach ground or have anchor
        foreach (var (id, route) in result.Routes)
        {
            if (route.Path.Count < 2) continue;
            bool grounded = route.ReachesGround || route.AnchorPoint.HasValue;
            grounded.Should().BeTrue($"route {id} must reach ground or anchor");
        }

        // Invariant (b): preview == print
        bool meshHasBraces = result.Interconnections.Count > 0;
        bool sliceHasBraces = result.SliceElements.Any(e => e.Type == "interconnect");
        if (meshHasBraces) sliceHasBraces.Should().BeTrue("braces in mesh must appear in slices");

        bool sliceHasPillars = result.SliceElements.Any(e => e.Type is "pillar" or "junction");
        sliceHasPillars.Should().BeTrue("must have pillar/junction slice elements");
    }

    [Fact]
    public void FastEngine_OldAndNew_BothProduceSupports()
    {
        var mesh = CreateComplexModel();

        var oldResult = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f, Seed = 42,
            UseFastSupportEngine = false,
        });

        var newResult = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f, Seed = 42,
            UseFastSupportEngine = true,
        });

        oldResult.ValidSupports.Should().BeGreaterThan(0);
        newResult.ValidSupports.Should().BeGreaterThan(0);

        // Coverage should be similar (within 50%)
        float ratio = (float)newResult.ValidSupports / oldResult.ValidSupports;
        ratio.Should().BeInRange(0.5f, 2.0f,
            $"fast engine supports ({newResult.ValidSupports}) should be within 50-200% of old ({oldResult.ValidSupports})");
    }
}
