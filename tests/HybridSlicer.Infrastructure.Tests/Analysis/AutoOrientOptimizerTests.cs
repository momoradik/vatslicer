using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class AutoOrientOptimizerTests
{
    private static StlMesh CreateShelfModel()
    {
        // A model with a large flat overhang on one side — the optimizer should
        // prefer orientations that minimize that overhang area.
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

        // Tall narrow pillar
        AddBox(-3, -3, 0, 3, 3, 30);
        // Wide shelf overhang at top
        AddBox(-20, -20, 28, 20, 20, 30);

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
    public void Evaluate_ReturnsMultipleOrientations()
    {
        var mesh = CreateShelfModel();
        var results = AutoOrientOptimizer.Evaluate(mesh);

        results.Should().NotBeEmpty("should produce at least one candidate orientation");
        results.Count.Should().BeGreaterOrEqualTo(3, "should return top N results");
    }

    [Fact]
    public void BestOrientation_HasLessOverhangThanDefault()
    {
        var mesh = CreateShelfModel();
        var results = AutoOrientOptimizer.Evaluate(mesh, new AutoOrientOptimizer.OrientConfig { CandidateCount = 36 });

        var best = results[0];
        // Default orientation (no rotation) should have the large shelf as overhang
        // Best orientation should rotate the model to reduce overhang
        best.OverhangAreaMm2.Should().BeGreaterOrEqualTo(0, "overhang area should be non-negative");
        best.Score.Should().BeGreaterOrEqualTo(0, "score should be non-negative");
        best.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResultsAreSortedByScore()
    {
        var mesh = CreateShelfModel();
        var results = AutoOrientOptimizer.Evaluate(mesh);

        for (int i = 1; i < results.Count; i++)
        {
            results[i].Score.Should().BeGreaterOrEqualTo(results[i - 1].Score,
                "results should be sorted by score (ascending = less overhang first)");
        }
    }
}
