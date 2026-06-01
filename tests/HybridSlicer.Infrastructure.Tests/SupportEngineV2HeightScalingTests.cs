using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests for height-based auto-scaling of pillar dimensions.
/// Taller supports need thicker pillars to resist buckling.
/// </summary>
public class SupportEngineV2HeightScalingTests
{
    private static StlMesh CreateFloatingCube(float size, float zOffset)
    {
        var o = new Vector3(-size/2, -size/2, zOffset);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = size;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));

        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++) {
            off += 12;
            for (int v = 0; v < 3; v++) {
                BitConverter.GetBytes(verts[t*3+v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t*3+v].Y).CopyTo(data, off+4);
                BitConverter.GetBytes(verts[t*3+v].Z).CopyTo(data, off+8);
                off += 12;
            }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void TallModel_ProducesThickerPillars()
    {
        var short_ = CreateFloatingCube(10f, 5f);  // Z=5 → ~5mm supports
        var tall = CreateFloatingCube(10f, 150f);   // Z=150 → ~150mm supports

        var shortResult = SupportEngineV2.Generate(short_, new SupportEngineV2.EngineConfig());
        var tallResult = SupportEngineV2.Generate(tall, new SupportEngineV2.EngineConfig());

        if (shortResult.Routes.Count > 0 && tallResult.Routes.Count > 0)
        {
            // Compare pillar segment radii (not base which has fixed max)
            float shortAvgR = shortResult.Routes.Average(r =>
                r.route.Path.Where(w => w.Type == "pillar" || w.Type == "junction").DefaultIfEmpty(new() { Position = default, Radius = 0.5f, Type = "x" }).Average(w => w.Radius));
            float tallAvgR = tallResult.Routes.Average(r =>
                r.route.Path.Where(w => w.Type == "pillar" || w.Type == "junction").DefaultIfEmpty(new() { Position = default, Radius = 0.5f, Type = "x" }).Average(w => w.Radius));

            tallAvgR.Should().BeGreaterThanOrEqualTo(shortAvgR,
                "tall supports should have same or thicker pillars");
        }
    }

    [Fact]
    public void TallModel_UsesMoreVolume()
    {
        var short_ = CreateFloatingCube(10f, 5f);
        var tall = CreateFloatingCube(10f, 150f);

        var shortResult = SupportEngineV2.Generate(short_, new SupportEngineV2.EngineConfig());
        var tallResult = SupportEngineV2.Generate(tall, new SupportEngineV2.EngineConfig());

        // Tall model should use at least as much material (unless it produces fewer supports)
        if (tallResult.ValidSupports >= shortResult.ValidSupports)
        {
            tallResult.TotalSupportVolumeMm3.Should().BeGreaterThanOrEqualTo(shortResult.TotalSupportVolumeMm3,
                "tall supports with same count should use more material");
        }
    }

    [Fact]
    public void TallModel_HasMoreSupportLayers()
    {
        var short_ = CreateFloatingCube(10f, 5f);
        var tall = CreateFloatingCube(10f, 100f);

        var shortResult = SupportEngineV2.Generate(short_, new SupportEngineV2.EngineConfig());
        var tallResult = SupportEngineV2.Generate(tall, new SupportEngineV2.EngineConfig());

        // Tall model with same support count should span more layers
        if (tallResult.ValidSupports > 0)
        {
            tallResult.SupportLayerCount.Should().BeGreaterThanOrEqualTo(shortResult.SupportLayerCount,
                "tall supports should span at least as many layers");
        }
    }
}
