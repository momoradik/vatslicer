using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportMaterialEstimatorTests
{
    [Fact]
    public void LowSupports_Excellent()
    {
        var b = SupportMaterialEstimator.Estimate(10000, 500);
        b.Efficiency.Should().Be("excellent");
        b.SupportPct.Should().BeLessThan(10);
    }

    [Fact]
    public void HighSupports_Poor()
    {
        var b = SupportMaterialEstimator.Estimate(5000, 8000);
        b.Efficiency.Should().Be("poor");
        b.SupportPct.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Cost_Calculated()
    {
        var b = SupportMaterialEstimator.Estimate(10000, 2000, 0.05f);
        b.SupportCostUsd.Should().BeApproximately(0.1f, 0.01f); // 2ml * $0.05
    }

    [Fact]
    public void TotalVolume_IsSum()
    {
        var b = SupportMaterialEstimator.Estimate(5000, 1000);
        b.TotalVolumeMl.Should().BeApproximately(6f, 0.01f);
    }
}
