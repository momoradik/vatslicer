using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class ResinBottleManagerTests
{
    [Fact]
    public void SufficientBottle_Recommended()
    {
        var bottles = new[]
        {
            new ResinBottleManager.Bottle { Id = "a", Name = "Bottle A", ResinType = "Standard", CapacityMl = 500, UsedMl = 100 },
            new ResinBottleManager.Bottle { Id = "b", Name = "Bottle B", ResinType = "Standard", CapacityMl = 500, UsedMl = 450 },
        };
        var rec = ResinBottleManager.Recommend(bottles, 50);
        rec.RecommendedBottleId.Should().Be("a");
        rec.NeedNewBottle.Should().BeFalse();
    }

    [Fact]
    public void InsufficientAll_NeedNew()
    {
        var bottles = new[]
        {
            new ResinBottleManager.Bottle { Id = "a", Name = "A", ResinType = "Standard", CapacityMl = 500, UsedMl = 480 },
        };
        var rec = ResinBottleManager.Recommend(bottles, 50);
        rec.NeedNewBottle.Should().BeTrue();
    }

    [Fact]
    public void WrongResinType_NeedNew()
    {
        var bottles = new[]
        {
            new ResinBottleManager.Bottle { Id = "a", Name = "A", ResinType = "ABS-Like", CapacityMl = 500, UsedMl = 0 },
        };
        var rec = ResinBottleManager.Recommend(bottles, 50, "Flexible");
        rec.NeedNewBottle.Should().BeTrue();
    }

    [Fact]
    public void EmptyList_NeedNew()
    {
        var rec = ResinBottleManager.Recommend(Array.Empty<ResinBottleManager.Bottle>(), 50);
        rec.NeedNewBottle.Should().BeTrue();
    }
}
