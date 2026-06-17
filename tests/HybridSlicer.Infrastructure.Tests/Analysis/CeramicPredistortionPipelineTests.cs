using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class CeramicPredistortionPipelineTests
{
    private static StlMesh BuildBox(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        var v = new List<Vector3>();
        void Q(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { v.Add(a); v.Add(b); v.Add(c); v.Add(a); v.Add(c); v.Add(d); }
        var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1); var v010 = new Vector3(x1, y2, z1);
        var v110 = new Vector3(x2, y2, z1); var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
        var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
        Q(v001, v101, v111, v011); Q(v000, v010, v110, v100); Q(v100, v110, v111, v101);
        Q(v000, v001, v011, v010); Q(v010, v011, v111, v110); Q(v000, v100, v101, v001);
        int tc = v.Count / 3; var d = new byte[84 + tc * 50]; BitConverter.GetBytes((uint)tc).CopyTo(d, 80); int o = 84;
        for (int t = 0; t < tc; t++)
        {
            var a = v[t * 3]; var b = v[t * 3 + 1]; var c = v[t * 3 + 2]; var n = Vector3.Cross(b - a, c - a);
            float l = n.Length(); if (l > 1e-6f) n /= l; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(d, o); BitConverter.GetBytes(n.Y).CopyTo(d, o + 4); BitConverter.GetBytes(n.Z).CopyTo(d, o + 8); o += 12;
            for (int i = 0; i < 3; i++) { BitConverter.GetBytes(v[t * 3 + i].X).CopyTo(d, o); BitConverter.GetBytes(v[t * 3 + i].Y).CopyTo(d, o + 4); BitConverter.GetBytes(v[t * 3 + i].Z).CopyTo(d, o + 8); o += 12; }
            o += 2;
        }
        return StlMesh.FromBinary(d);
    }

    [Fact]
    public void UniformShrinkage_ProducesLargerMesh()
    {
        var nominal = BuildBox(-10, -10, 0, 10, 10, 20);
        var result = CeramicPredistortionPipeline.Predistort(nominal,
            new CeramicPredistortionPipeline.PredistortionConfig { ShrinkageXPct = 15, ShrinkageYPct = 15, ShrinkageZPct = 18 });

        var predistorted = result.PredistortedMesh;
        float nominalWidth = nominal.Max.X - nominal.Min.X;
        float predistortedWidth = predistorted.Max.X - predistorted.Min.X;

        predistortedWidth.Should().BeGreaterThan(nominalWidth,
            "predistorted mesh should be larger to compensate for shrinkage");
    }

    [Fact]
    public void Converges_WithinIterations()
    {
        var nominal = BuildBox(-5, -5, 0, 5, 5, 10);
        var result = CeramicPredistortionPipeline.Predistort(nominal,
            new CeramicPredistortionPipeline.PredistortionConfig { MaxIterations = 10, ToleranceMm = 1f });

        result.IterationsUsed.Should().BeGreaterThan(0);
        result.PredistortedMesh.TriangleCount.Should().Be(nominal.TriangleCount);
    }

    [Fact]
    public void ZeroShrinkage_MeshUnchanged()
    {
        var nominal = BuildBox(-5, -5, 0, 5, 5, 10);
        var result = CeramicPredistortionPipeline.Predistort(nominal,
            new CeramicPredistortionPipeline.PredistortionConfig { ShrinkageXPct = 0, ShrinkageYPct = 0, ShrinkageZPct = 0 });

        float nomW = nominal.Max.X - nominal.Min.X;
        float resW = result.PredistortedMesh.Max.X - result.PredistortedMesh.Min.X;
        resW.Should().BeApproximately(nomW, 0.1f, "zero shrinkage → no change");
    }

    [Fact]
    public void HigherShrinkage_BiggerPredistortion()
    {
        var nominal = BuildBox(-10, -10, 0, 10, 10, 20);
        var low = CeramicPredistortionPipeline.Predistort(nominal,
            new CeramicPredistortionPipeline.PredistortionConfig { ShrinkageXPct = 5, ShrinkageYPct = 5, ShrinkageZPct = 5 });
        var high = CeramicPredistortionPipeline.Predistort(nominal,
            new CeramicPredistortionPipeline.PredistortionConfig { ShrinkageXPct = 20, ShrinkageYPct = 20, ShrinkageZPct = 25 });

        float lowW = low.PredistortedMesh.Max.X - low.PredistortedMesh.Min.X;
        float highW = high.PredistortedMesh.Max.X - high.PredistortedMesh.Min.X;

        highW.Should().BeGreaterThan(lowW, "more shrinkage → bigger predistortion");
    }

    [Fact]
    public void WithSpatialDeviation_AppliesPerVertex()
    {
        var nominal = BuildBox(-5, -5, 0, 5, 5, 10);
        // Synthetic deviation: push all vertices +1mm in X
        var deviations = new Vector3[nominal.Vertices.Length];
        for (int i = 0; i < deviations.Length; i++)
            deviations[i] = new Vector3(1f, 0, 0);

        var result = CeramicPredistortionPipeline.Predistort(nominal,
            new CeramicPredistortionPipeline.PredistortionConfig { Alpha = 1f, MaxIterations = 1 },
            new CeramicPredistortionPipeline.DeviationField { PerVertexDisplacements = deviations });

        // Predistortion should push vertices in -X (inverse of measured deviation)
        result.PredistortedMesh.Max.X.Should().BeLessThan(nominal.Max.X + 0.1f,
            "spatial predistortion should move vertices opposite to deviation");
    }
}
