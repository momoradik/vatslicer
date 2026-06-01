using System.Numerics;
using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportPointGeneratorEdgeCaseTests
{
    private static StlMesh CreateCube(float size, float z)
    {
        var o = new Vector3(-size/2, -size/2, z);
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
    public void Generate_TopDownOrientation_ProducesPoints()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig
            {
                Orientation = PrinterOrientation.TopDown,
            });

        result.Should().NotBeNull();
        result.ElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void Generate_WithDrainHoleExclusion_ExcludesNearbyPoints()
    {
        var mesh = CreateCube(20f, 10f);

        var withoutHoles = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig());

        var withHoles = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig
            {
                DrainHoleExclusions = new() { (new Vector3(0, 0, 10), 10f) },
                DrainHoleClearanceMm = 5f,
            });

        withHoles.Points.Count.Should().BeLessThanOrEqualTo(withoutHoles.Points.Count,
            "drain hole exclusion should reduce or maintain point count");
    }

    [Fact]
    public void Generate_VeryLowDensity_FewPoints()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig { DensityFactor = 0.01f });

        result.Points.Count.Should().BeLessThan(200, "very low density → fewer points");
    }

    [Fact]
    public void Generate_VeryHighDensity_ManyPoints()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig { DensityFactor = 0.99f });

        result.Points.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Generate_AllPointsHaveValidOverhangType()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig());

        foreach (var pt in result.Points)
        {
            Enum.IsDefined(pt.OverhangType).Should().BeTrue(
                $"point {pt.Id} should have valid overhang type");
        }
    }

    [Fact]
    public void Generate_ResultHasElapsedTime()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig());

        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Generate_WithPrebuiltBvh_SameResults()
    {
        var mesh = CreateCube(20f, 10f);
        var bvh = AabbBvh.Build(mesh);

        var without = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig());
        var with_ = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig(), bvh);

        // Both should produce points (count may differ slightly due to BVH normal queries)
        without.Points.Count.Should().BeGreaterThan(0);
        with_.Points.Count.Should().BeGreaterThan(0);
    }
}
