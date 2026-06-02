using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class PinheadOptimizerStressTests
{
    private static AabbBvh CreateFarBvh()
    {
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12;
        for (int v = 0; v < 3; v++) {
            BitConverter.GetBytes(1000f+v).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void Optimize_100Pinheads_Under1s()
    {
        var bvh = CreateFarBvh();
        var cfg = new PinheadOptimizer.PinheadConfig();
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            var contact = new Vector3(rng.NextSingle() * 100, rng.NextSingle() * 100, 50);
            var normal = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, -1));
            PinheadOptimizer.Optimize(contact, normal, bvh, cfg);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(0.5f, 0, -0.866f)]
    [InlineData(-0.3f, 0.4f, -0.866f)]
    [InlineData(0, -0.7f, -0.714f)]
    public void Optimize_VariousNormals_AllValid(float nx, float ny, float nz)
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(0, 0, 50),
            new Vector3(nx, ny, nz),
            bvh,
            new PinheadOptimizer.PinheadConfig());

        ph.IsValid.Should().BeTrue();
        ph.Direction.Z.Should().BeLessThan(0);
    }

    [Fact]
    public void Optimize_ResultDimensions_AllPositive()
    {
        var bvh = CreateFarBvh();
        var ph = PinheadOptimizer.Optimize(
            new Vector3(10, 20, 30),
            new Vector3(0, 0, -1),
            bvh,
            new PinheadOptimizer.PinheadConfig { PinRadiusMm = 0.3f, BackRadiusMm = 0.6f, WidthMm = 1.5f });

        ph.PinRadius.Should().BeGreaterThan(0);
        ph.BackRadius.Should().BeGreaterThan(0);
        ph.Width.Should().BeGreaterThan(0);
        ph.Clearance.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Optimize_JunctionBelowContact_Always()
    {
        var bvh = CreateFarBvh();
        var rng = new Random(123);

        for (int i = 0; i < 20; i++)
        {
            var contact = new Vector3(rng.NextSingle() * 50, rng.NextSingle() * 50, 50);
            var normal = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, -1));
            var ph = PinheadOptimizer.Optimize(contact, normal, bvh, new PinheadOptimizer.PinheadConfig());

            ph.JunctionPoint.Z.Should().BeLessThan(ph.ContactPoint.Z);
        }
    }
}
