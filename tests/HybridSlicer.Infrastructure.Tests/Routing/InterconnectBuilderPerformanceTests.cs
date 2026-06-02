using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class InterconnectBuilderPerformanceTests
{
    [Fact]
    public void Build_100Pillars_Under2s()
    {
        var bases = Enumerable.Range(0, 100)
            .Select(i => new Vector3((i % 10) * 3f, (i / 10) * 3f, 0)).ToList();
        var tops = Enumerable.Repeat(80f, 100).ToList();
        var radii = Enumerable.Repeat(0.5f, 100).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var conn = InterconnectBuilder.Build(bases, tops, radii, null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 5f });
        sw.Stop();

        conn.Should().NotBeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public void Build_ConnectionCount_ScalesReasonably()
    {
        var small = Enumerable.Range(0, 10).Select(i => new Vector3(i * 3f, 0, 0)).ToList();
        var large = Enumerable.Range(0, 50).Select(i => new Vector3(i * 3f, 0, 0)).ToList();

        var smallConn = InterconnectBuilder.Build(small,
            Enumerable.Repeat(50f, 10).ToList(),
            Enumerable.Repeat(0.5f, 10).ToList(), null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 5f });

        var largeConn = InterconnectBuilder.Build(large,
            Enumerable.Repeat(50f, 50).ToList(),
            Enumerable.Repeat(0.5f, 50).ToList(), null,
            new InterconnectBuilder.InterconnectConfig { MaxConnectionDistMm = 5f });

        largeConn.Count.Should().BeGreaterThan(smallConn.Count);
    }
}
