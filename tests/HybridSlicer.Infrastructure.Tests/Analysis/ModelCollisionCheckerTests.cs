using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ModelCollisionCheckerTests
{
    [Fact]
    public void NoOverlap_NoCollisions()
    {
        var models = new[]
        {
            new ModelCollisionChecker.ModelBounds { Id = "a", Min = new(0,0,0), Max = new(10,10,10) },
            new ModelCollisionChecker.ModelBounds { Id = "b", Min = new(20,0,0), Max = new(30,10,10) },
        };
        var result = ModelCollisionChecker.Check(models);
        result.HasCollisions.Should().BeFalse();
    }

    [Fact]
    public void Overlapping_Detected()
    {
        var models = new[]
        {
            new ModelCollisionChecker.ModelBounds { Id = "a", Min = new(0,0,0), Max = new(10,10,10) },
            new ModelCollisionChecker.ModelBounds { Id = "b", Min = new(5,5,5), Max = new(15,15,15) },
        };
        var result = ModelCollisionChecker.Check(models);
        result.HasCollisions.Should().BeTrue();
        result.Collisions[0].OverlapVolumeMm3.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WithGap_DetectsNearMisses()
    {
        var models = new[]
        {
            new ModelCollisionChecker.ModelBounds { Id = "a", Min = new(0,0,0), Max = new(10,10,10) },
            new ModelCollisionChecker.ModelBounds { Id = "b", Min = new(11,0,0), Max = new(20,10,10) },
        };
        // Without gap: no collision (1mm apart)
        ModelCollisionChecker.Check(models, gapMm: 0).HasCollisions.Should().BeFalse();
        // With 2mm gap requirement: collision (only 1mm apart)
        ModelCollisionChecker.Check(models, gapMm: 2).HasCollisions.Should().BeTrue();
    }
}
