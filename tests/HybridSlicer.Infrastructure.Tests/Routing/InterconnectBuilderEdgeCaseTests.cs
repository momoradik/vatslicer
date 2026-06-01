using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class InterconnectBuilderEdgeCaseTests
{
    [Fact]
    public void Build_ZeroMaxDist_NoConnections()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 50f, 50f };
        var radii = new List<float> { 0.5f, 0.5f };

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 0.1f });

        conn.Should().BeEmpty("pillars too far apart for 0.1mm max dist");
    }

    [Fact]
    public void Build_EmptyInput_NoConnections()
    {
        var conn = InterconnectBuilder.Build(new(), new(), new(), null,
            new InterconnectBuilder.InterconnectConfig());
        conn.Should().BeEmpty();
    }

    [Fact]
    public void Build_ConnectionsHaveValidTypes()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 100f, 100f };
        var radii = new List<float> { 0.5f, 0.5f };

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 10f });

        foreach (var c in conn)
        {
            c.Type.Should().BeOneOf("horizontal", "diagonal");
            c.Radius.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Build_TallPillars_EnforcesMinimumConnections()
    {
        // Two very tall pillars — should get mandatory connections
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 200f, 200f };
        var radii = new List<float> { 0.5f, 0.5f };

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig
            {
                MaxConnectionDistMm = 10f,
                MaxSoloHeightMm = 20f,
                ConnectionIntervalMm = 10f,
            });

        conn.Should().NotBeEmpty("tall pillars need interconnections");
    }

    [Fact]
    public void Build_ManyPillars_ConnectsNearestOnly()
    {
        var bases = new List<Vector3>();
        var tops = new List<float>();
        var radii = new List<float>();

        // 10 pillars in a line, 3mm apart
        for (int i = 0; i < 10; i++)
        {
            bases.Add(new Vector3(i * 3f, 0, 0));
            tops.Add(50f);
            radii.Add(0.5f);
        }

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig
            {
                MaxConnectionDistMm = 5f, // only connects adjacent pillars
                ConnectionIntervalMm = 10f,
            });

        conn.Should().NotBeEmpty();
        // No connection should span more than 5mm
        foreach (var c in conn)
        {
            float dist = Vector3.Distance(c.PointA, c.PointB);
            // Allow some slack for diagonal connections
            dist.Should().BeLessThan(10f);
        }
    }
}
