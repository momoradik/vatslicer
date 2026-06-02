using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class InterconnectBuilderStressTests
{
    [Fact]
    public void Build_50Pillars_CompletesInTime()
    {
        var bases = new List<Vector3>();
        var tops = new List<float>();
        var radii = new List<float>();

        for (int i = 0; i < 50; i++)
        {
            bases.Add(new Vector3((i % 10) * 5f, (i / 10) * 5f, 0));
            tops.Add(50f);
            radii.Add(0.5f);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 8f });
        sw.Stop();

        conn.Should().NotBeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void Build_AllConnectionsHavePositiveLength()
    {
        var bases = new List<Vector3>
        {
            new(0, 0, 0), new(5, 0, 0), new(10, 0, 0),
            new(0, 5, 0), new(5, 5, 0), new(10, 5, 0),
        };
        var tops = Enumerable.Repeat(80f, 6).ToList();
        var radii = Enumerable.Repeat(0.5f, 6).ToList();

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 8f });

        foreach (var c in conn)
        {
            float len = Vector3.Distance(c.PointA, c.PointB);
            len.Should().BeGreaterThan(0.1f, "connections should have positive length");
        }
    }

    [Fact]
    public void Build_NoSelfConnections()
    {
        var bases = Enumerable.Range(0, 20).Select(i => new Vector3(i * 3f, 0, 0)).ToList();
        var tops = Enumerable.Repeat(50f, 20).ToList();
        var radii = Enumerable.Repeat(0.5f, 20).ToList();

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 5f });

        foreach (var c in conn)
            c.PillarA.Should().NotBe(c.PillarB);
    }

    [Fact]
    public void Build_ConnectionZsWithinPillarRange()
    {
        var bases = new List<Vector3> { new(0, 0, 0), new(5, 0, 0) };
        var tops = new List<float> { 100f, 80f };
        var radii = new List<float> { 0.5f, 0.5f };

        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 10f });

        foreach (var c in conn)
        {
            c.PointA.Z.Should().BeGreaterThanOrEqualTo(0);
            c.PointB.Z.Should().BeGreaterThanOrEqualTo(0);
            c.PointA.Z.Should().BeLessThanOrEqualTo(100);
            c.PointB.Z.Should().BeLessThanOrEqualTo(100);
        }
    }
}
