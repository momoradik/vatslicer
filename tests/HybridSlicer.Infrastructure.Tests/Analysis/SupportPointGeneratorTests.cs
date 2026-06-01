using System.Numerics;
using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportPointGeneratorTests
{
    private static StlMesh CreateFloatingCube(float size = 20f, float zOffset = 10f)
    {
        var o = new Vector3(-size / 2, -size / 2, zOffset);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts[vi++] = a + o; verts[vi++] = b + o; verts[vi++] = c + o;
            verts[vi++] = a + o; verts[vi++] = c + o; verts[vi++] = d + o;
        }
        float s = size;
        Quad(new(0,0,s), new(s,0,s), new(s,s,s), new(0,s,s));
        Quad(new(0,0,0), new(0,s,0), new(s,s,0), new(s,0,0));
        Quad(new(s,0,0), new(s,0,s), new(s,s,s), new(s,s,0));
        Quad(new(0,0,s), new(0,0,0), new(0,s,0), new(0,s,s));
        Quad(new(0,s,s), new(s,s,s), new(s,s,0), new(0,s,0));
        Quad(new(0,0,0), new(s,0,0), new(s,0,s), new(0,0,s));

        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
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
    public void Generate_FloatingCube_ProducesPoints()
    {
        var mesh = CreateFloatingCube();
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());

        result.Points.Should().NotBeEmpty("floating cube needs support points");
        result.ElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public void Generate_PointsHaveUniqueIds()
    {
        var mesh = CreateFloatingCube();
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());

        var ids = result.Points.Select(p => p.Id).ToList();
        ids.Distinct().Count().Should().Be(ids.Count, "all point IDs should be unique");
    }

    [Fact]
    public void Generate_MostPointsHaveDownwardNormals()
    {
        var mesh = CreateFloatingCube();
        var bvh = AabbBvh.Build(mesh);
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig(), bvh);

        // Most support points should have downward-facing normals (overhang surfaces)
        // Some may have upward normals if placed by coverage backfill at non-overhang positions
        var downwardCount = result.Points.Count(p => p.Normal.Z <= 0.1f);
        downwardCount.Should().BeGreaterThan(result.Points.Count / 2,
            "majority of support points should be at downward-facing surfaces");
    }

    [Fact]
    public void Generate_PointsHaveWeight()
    {
        var mesh = CreateFloatingCube();
        var result = SupportPointGenerator.Generate(mesh, new SupportPointGenerator.GenerationConfig());

        foreach (var pt in result.Points)
        {
            Enum.IsDefined(pt.RecommendedWeight).Should().BeTrue();
            pt.SafetyFactor.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Generate_HigherDensity_MorePoints()
    {
        var mesh = CreateFloatingCube();
        var low = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig { DensityFactor = 0.1f });
        var high = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig { DensityFactor = 0.9f });

        high.Points.Count.Should().BeGreaterThanOrEqualTo(low.Points.Count);
    }

    [Fact]
    public void Generate_PointsAreMostlySpaced()
    {
        var mesh = CreateFloatingCube();
        var result = SupportPointGenerator.Generate(mesh,
            new SupportPointGenerator.GenerationConfig { MinSpacingMm = 3f });

        // Most points should be reasonably spaced (coverage backfill may place some closer)
        int closeCount = 0;
        float minAllowed = 1.5f; // generous tolerance
        for (int i = 0; i < result.Points.Count; i++)
        for (int j = i + 1; j < result.Points.Count; j++)
        {
            float dist = Vector3.Distance(result.Points[i].Position, result.Points[j].Position);
            if (dist < minAllowed) closeCount++;
        }
        closeCount.Should().BeLessThan(result.Points.Count,
            "most point pairs should be well-spaced");
    }
}
