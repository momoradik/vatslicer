using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

/// <summary>
/// Validates physics-driven support sizing against known reference values
/// and ensures dimensional relationships are physically correct.
/// </summary>
public class SupportSizerPhysicsTests
{
    [Fact]
    public void TipRadius_IncreasesWithPeelForce()
    {
        // Use large areas so physics values exceed the R_TIP_MIN floor
        var small = SupportSizer.Size(20f, 100f, 2, true);
        var large = SupportSizer.Size(20f, 5000f, 2, true);

        large.TipRadius.Should().BeGreaterOrEqualTo(small.TipRadius,
            "larger layer area means more peel force per support → thicker tip");
        large.Force.Should().BeGreaterThan(small.Force);
    }

    [Fact]
    public void PillarRadius_IncreasesWithHeight()
    {
        // Use extreme heights so stiffness term exceeds the floor
        var short_ = SupportSizer.Size(5f, 100f, 5, true);
        var tall = SupportSizer.Size(200f, 100f, 5, true);

        tall.PillarRadius.Should().BeGreaterThan(short_.PillarRadius,
            "taller pillar needs thicker shaft for stiffness");
    }

    [Fact]
    public void BaseRadius_LargerThanPillar()
    {
        var sizing = SupportSizer.Size(20f, 100f, 5, true);

        sizing.BaseRadius.Should().BeGreaterThan(sizing.PillarRadius,
            "base must flare wider than pillar for adhesion");
    }

    [Fact]
    public void BaseRadius_ZeroWhenNotGrounded()
    {
        var sizing = SupportSizer.Size(10f, 50f, 3, rootsOnPlate: false);

        sizing.BaseRadius.Should().Be(0, "non-grounded support has no base");
        sizing.BaseHeight.Should().Be(0);
    }

    [Fact]
    public void MoreSupportsPerLayer_ThinnierEach()
    {
        // More supports share the load → each can be thinner
        var few = SupportSizer.Size(20f, 200f, 5, true);
        var many = SupportSizer.Size(20f, 200f, 50, true);

        many.TipRadius.Should().BeLessOrEqualTo(few.TipRadius,
            "more supports sharing the load → each carries less force");
    }

    [Fact]
    public void HigherPAdh_ThickerSupports()
    {
        // Use large area + few supports so physics values exceed floors
        var lowAdh = SupportSizer.Size(20f, 2000f, 2, true, pAdh: 0.005f);
        var highAdh = SupportSizer.Size(20f, 2000f, 2, true, pAdh: 0.05f);

        highAdh.TipRadius.Should().BeGreaterOrEqualTo(lowAdh.TipRadius,
            "higher adhesion pressure means more force per support");
        highAdh.Force.Should().BeGreaterThan(lowAdh.Force);
    }

    [Fact]
    public void ManualOverrides_WinOverPhysics()
    {
        var ov = new SupportSizer.ManualOverrides
        {
            TipRadiusMm = 0.8f,
            PillarRadiusMm = 1.5f,
        };

        var sizing = SupportSizer.Size(20f, 100f, 5, true, ov: ov);

        sizing.TipRadius.Should().Be(0.8f, "manual override should win");
        sizing.PillarRadius.Should().Be(1.5f, "manual override should win");
        sizing.RecommendedTipRadius.Should().NotBe(0.8f,
            "recommended should be the physics value, not the override");
    }

    [Fact]
    public void ContactDepth_DefaultsToConstant()
    {
        var sizing = SupportSizer.Size(10f, 50f, 3, true);
        sizing.ContactDepth.Should().Be(SupportSizer.CONTACT_DEPTH);
    }

    [Fact]
    public void ContactSphereRadius_LargerThanTip()
    {
        var sizing = SupportSizer.Size(15f, 100f, 5, true);
        sizing.ContactSphereRadius.Should().BeGreaterThan(sizing.TipRadius,
            "contact sphere is visually larger than the tip neck");
    }

    [Fact]
    public void Force_IsPositive()
    {
        var sizing = SupportSizer.Size(10f, 100f, 5, true);
        sizing.Force.Should().BeGreaterThan(0);
    }
}
