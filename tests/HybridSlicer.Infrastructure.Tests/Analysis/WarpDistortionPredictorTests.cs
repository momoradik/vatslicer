using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class WarpDistortionPredictorTests
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
    public void SmallCube_LowRisk()
    {
        var mesh = BuildBox(-5, -5, 0, 5, 5, 10);
        var risk = WarpDistortionPredictor.Predict(mesh);
        risk.Level.Should().Be("low");
    }

    [Fact]
    public void LargeFlatPlate_HighRisk()
    {
        var mesh = BuildBox(-50, -50, 0, 50, 50, 2);
        var risk = WarpDistortionPredictor.Predict(mesh);
        risk.RiskScore.Should().BeGreaterThan(3);
        risk.RiskFactors.Should().NotBeEmpty();
    }

    [Fact]
    public void TallThin_HighRisk()
    {
        var mesh = BuildBox(-2, -2, 0, 2, 2, 50);
        var risk = WarpDistortionPredictor.Predict(mesh);
        risk.RiskScore.Should().BeGreaterThan(2);
    }

    [Fact]
    public void Mitigations_ProvidedForHighRisk()
    {
        var mesh = BuildBox(-40, -40, 0, 40, 40, 3);
        var risk = WarpDistortionPredictor.Predict(mesh, shrinkagePct: 4f);
        risk.Mitigations.Should().NotBeEmpty();
    }
}
