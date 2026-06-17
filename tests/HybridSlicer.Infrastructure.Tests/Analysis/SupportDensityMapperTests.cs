using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SupportDensityMapperTests
{
    [Fact]
    public void SingleSupport_InOneCell()
    {
        var bases = new[] { new Vector3(50, 50, 0) };
        var map = SupportDensityMapper.Compute(bases, 100, 100, 10f);
        map.MaxDensity.Should().Be(1);
        map.TotalSupports.Should().Be(1);
    }

    [Fact]
    public void MultipleSupports_SameCell_Accumulate()
    {
        var bases = new[] { new Vector3(5, 5, 0), new Vector3(6, 6, 0), new Vector3(7, 7, 0) };
        var map = SupportDensityMapper.Compute(bases, 100, 100, 10f);
        map.MaxDensity.Should().Be(3);
    }

    [Fact]
    public void EmptySupports_ZeroDensity()
    {
        var map = SupportDensityMapper.Compute(Array.Empty<Vector3>(), 100, 100, 10f);
        map.MaxDensity.Should().Be(0);
        map.TotalSupports.Should().Be(0);
    }
}
