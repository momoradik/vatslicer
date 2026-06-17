using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ShrinkageCompensatorTests
{
    [Fact]
    public void UniformShrinkage_ScalesUp()
    {
        var (sx, sy, sz) = ShrinkageCompensator.ComputeScaleFactors(
            new ShrinkageCompensator.CompensationConfig { ShrinkagePct = 3f });
        sx.Should().BeGreaterThan(1f);
        sy.Should().Be(sx, "uniform shrinkage → equal XYZ scale");
        sz.Should().Be(sx);
    }

    [Fact]
    public void ZeroShrinkage_NoScale()
    {
        var (sx, sy, sz) = ShrinkageCompensator.ComputeScaleFactors(
            new ShrinkageCompensator.CompensationConfig { ShrinkagePct = 0f });
        sx.Should().Be(1f);
        sy.Should().Be(1f);
        sz.Should().Be(1f);
    }

    [Fact]
    public void AnisotropicShrinkage_DifferentAxes()
    {
        var (sx, sy, sz) = ShrinkageCompensator.ComputeScaleFactors(
            new ShrinkageCompensator.CompensationConfig
            {
                ShrinkagePct = 0f, // ignored when per-axis set
                ShrinkageXPct = 1f, ShrinkageYPct = 2f, ShrinkageZPct = 5f,
            });
        sx.Should().BeApproximately(1.01f, 0.001f);
        sy.Should().BeApproximately(1.02f, 0.001f);
        sz.Should().BeApproximately(1.05f, 0.001f);
    }
}
