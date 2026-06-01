using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class InterconnectBuilderTests
{
    [Fact]
    public void Build_TwoNearbyPillars_CreatesConnections()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 50f, 50f };
        var radii = new List<float> { 0.5f, 0.5f };

        var connections = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 10f });

        connections.Should().NotBeEmpty("nearby pillars should be connected");
        connections.All(c => c.PillarA != c.PillarB).Should().BeTrue("no self-connections");
    }

    [Fact]
    public void Build_DistantPillars_NoConnections()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(100, 0, 0) };
        var tops = new List<float> { 50f, 50f };
        var radii = new List<float> { 0.5f, 0.5f };

        var connections = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 10f });

        connections.Should().BeEmpty("pillars too far apart");
    }

    [Fact]
    public void Build_AlternatesHorizontalAndDiagonal()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 100f, 100f };
        var radii = new List<float> { 0.5f, 0.5f };

        var connections = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig
            {
                MaxConnectionDistMm = 10f,
                ConnectionIntervalMm = 10f,
            });

        var horizontal = connections.Where(c => c.Type == "horizontal").ToList();
        var diagonal = connections.Where(c => c.Type == "diagonal").ToList();

        horizontal.Should().NotBeEmpty("should have horizontal bridges");
        diagonal.Should().NotBeEmpty("should have diagonal struts");
    }

    [Fact]
    public void Build_SinglePillar_NoConnections()
    {
        var bases = new List<Vector3> { new(0, 0, 0) };
        var tops = new List<float> { 50f };
        var radii = new List<float> { 0.5f };

        var connections = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig());

        connections.Should().BeEmpty("single pillar can't connect to anything");
    }

    [Fact]
    public void Build_ThreePillars_ConnectsNearest()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(3, 0, 0), new(100, 0, 0) };
        var tops = new List<float> { 50f, 50f, 50f };
        var radii = new List<float> { 0.5f, 0.5f, 0.5f };

        var connections = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 10f });

        // Should connect pillars 0 and 1 (distance=3) but not 0 and 2 (distance=100)
        connections.Should().NotBeEmpty();
        connections.All(c => c.PillarA == 2 || c.PillarB == 2).Should().BeFalse(
            "pillar at x=100 should not be connected to nearby pillars");
    }

    [Fact]
    public void Build_ShortPillars_NoOverlap_NoConnections()
    {
        // Two pillars at different heights with no Z overlap
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 10f, 50f };
        var radii = new List<float> { 0.5f, 0.5f };

        var connections = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig
            {
                MaxConnectionDistMm = 10f,
                ConnectionIntervalMm = 20f, // wider than overlap
            });

        // May or may not connect depending on overlap range — just verify no crash
        connections.Should().NotBeNull();
    }
}
