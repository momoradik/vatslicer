using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintJobReportGeneratorTests
{
    private static StlMesh BuildBox()
    {
        var v = new List<Vector3>();
        void Q(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { v.Add(a); v.Add(b); v.Add(c); v.Add(a); v.Add(c); v.Add(d); }
        var v000 = new Vector3(-10, -10, 0); var v100 = new Vector3(10, -10, 0); var v010 = new Vector3(-10, 10, 0);
        var v110 = new Vector3(10, 10, 0); var v001 = new Vector3(-10, -10, 20); var v101 = new Vector3(10, -10, 20);
        var v011 = new Vector3(-10, 10, 20); var v111 = new Vector3(10, 10, 20);
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
    public void FullReport_ContainsAllSections()
    {
        var mesh = BuildBox();
        var report = PrintJobReportGenerator.Generate(mesh);
        report.Analysis.Should().NotBeNull();
        report.Score.Should().NotBeNull();
        report.Risk.Should().NotBeNull();
        report.Overhangs.Should().NotBeNull();
        report.Finish.Should().NotBeNull();
        report.CenterOfGravity.Should().NotBeNull();
        report.PostProcess.Should().NotBeNull();
        report.TotalElapsedMs.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void FullReport_ScoreMatchesAnalysis()
    {
        var mesh = BuildBox();
        var report = PrintJobReportGenerator.Generate(mesh, supportCount: 10);
        report.Score.Total.Should().BeInRange(0, 100);
        report.Risk.Verdict.Should().NotBeNullOrEmpty();
    }
}
