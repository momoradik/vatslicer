using System.Diagnostics;
using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Performance regression gate tests.
/// These verify that key operations complete within time budgets.
/// Failing these indicates a performance regression that needs investigation.
/// </summary>
public class PerformanceRegressionTests
{
    private static StlMesh CreateMediumModel()
    {
        // Build a model with ~500 triangles and overhangs
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

        // Multi-level structure with overhangs
        AddBox(-20, -20, 0, 20, 20, 5);   // base plate
        AddBox(-3, -3, 5, 3, 3, 25);       // pillar
        AddBox(-25, -25, 23, 25, 25, 25);   // large overhang
        AddBox(-10, -10, 30, 10, 10, 35);   // upper block
        AddBox(-15, -15, 33, 15, 15, 35);   // upper overhang
        // Add some small features
        for (int i = 0; i < 5; i++)
            AddBox(-30 + i * 12, -30, 15 + i, -28 + i * 12, -28, 17 + i);

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
    public void SupportGeneration_CompletesWithin5Seconds()
    {
        var mesh = CreateMediumModel();
        var sw = Stopwatch.StartNew();

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
        });

        sw.Stop();

        result.ValidSupports.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(15000,
            "support generation on a medium model must complete within 15 seconds");
    }

    [Fact]
    public void BvhBuild_CompletesWithin500ms()
    {
        var mesh = CreateMediumModel();
        var sw = Stopwatch.StartNew();

        var bvh = Resin.Spatial.AabbBvh.Build(mesh);

        sw.Stop();

        bvh.TriangleCount.Should().Be(mesh.TriangleCount);
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "BVH build on a medium model must complete within 500ms");
    }

    [Fact]
    public void MeshValidation_CompletesWithin200ms()
    {
        var mesh = CreateMediumModel();
        var sw = Stopwatch.StartNew();

        var result = MeshValidator.Validate(mesh);

        sw.Stop();

        result.TriangleCount.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "mesh validation on a medium model must complete within 200ms");
    }
}
