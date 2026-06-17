using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

/// <summary>
/// Battery of quick tests that verify every analysis engine produces non-null,
/// non-crashing results on a standard test model.
/// </summary>
public class ComprehensiveAnalysisBatteryTests
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

    [Fact] public void ElephantFoot_NoThrow() => ElephantFootCompensator.Calculate(5, 30000, 2000, 0.05f).Should().NotBeNull();
    [Fact] public void SliceMemory_NoThrow() => SliceMemoryEstimator.Estimate(1920, 1080, 500).Should().NotBeNull();
    [Fact] public void PrintSpeed_NoThrow() => PrintSpeedOptimizer.Optimize(500, 5, 2000, 30000, 5, 120, 240, 1000, 200).Should().NotBeNull();
    [Fact] public void MinFeature_NoThrow() => MinimumFeatureSizeChecker.Check(0.5f, 0.3f, 0.05f).Should().NotBeNull();
    [Fact] public void ModelBounds_NoThrow() => ModelBoundsAnalyzer.Analyze(BuildBox(), 192, 120, 200).Should().NotBeNull();
    [Fact] public void PostProcess_NoThrow() => PostProcessingAdvisor.Generate().Should().NotBeNull();
    [Fact] public void ShelfLife_NoThrow() => ResinShelfLifeTracker.Estimate(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).Should().NotBeNull();
    [Fact] public void UVPower_NoThrow() => UVPowerDensityCalculator.Calculate(2000).Should().NotBeNull();
    [Fact] public void DimAccuracy_NoThrow() => DimensionalAccuracyPredictor.Predict(0.05f, 0.05f).Should().NotBeNull();
    [Fact] public void OptLayerHeight_NoThrow() => OptimalLayerHeightCalculator.Calculate(20f).Should().NotBeNull();
    [Fact] public void SupportEfficiency_NoThrow() => SupportEfficiencyAnalyzer.Analyze(500, 200, 20, 5000).Should().NotBeNull();
    [Fact] public void AAQuality_NoThrow() => AntiAliasingQualityEstimator.Estimate(0.05f, 0.05f).Should().NotBeNull();
    [Fact] public void SupportMaterial_NoThrow() => SupportMaterialEstimator.Estimate(5000, 1000).Should().NotBeNull();
    [Fact] public void PrintCost_NoThrow() => PrintCostCalculator.Calculate(10, 2).Should().NotBeNull();
    [Fact] public void ResinUsage_NoThrow() => ResinUsageForecaster.Predict(500, 100, 50).Should().NotBeNull();
    [Fact] public void Compatibility_NoThrow() => ResinCompatibilityChecker.Check(405, 405, 2000, 2000, 30000, 30000, 120).Should().NotBeNull();
    [Fact] public void Readiness_NoThrow() => PrintReadinessChecker.Check(true, true, true, true, true).Should().NotBeNull();
    [Fact] public void JobSummary_NoThrow() => PrintJobSummaryGenerator.Generate("t", "p", "r", 100, 30, 5, 5.5f, 0.25f, 90, "A", new(), "ready").Should().NotBeNull();
}
