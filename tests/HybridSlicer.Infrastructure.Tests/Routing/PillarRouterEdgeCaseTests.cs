using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class PillarRouterEdgeCaseTests
{
    private static AabbBvh CreateFarBvh()
    {
        var data = new byte[84 + 50];
        BitConverter.GetBytes((uint)1).CopyTo(data, 80);
        int off = 84 + 12;
        for (int v = 0; v < 3; v++) {
            BitConverter.GetBytes(1000f + v).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
            BitConverter.GetBytes(1000f).CopyTo(data, off); off += 4;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void Route_AtZ1_VeryShort_StillProducesPath()
    {
        var bvh = CreateFarBvh();
        var route = PillarRouter.Route(new Vector3(0, 0, 1), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.Path.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void Route_AtZ0_MinimalPath()
    {
        var bvh = CreateFarBvh();
        var route = PillarRouter.Route(new Vector3(0, 0, 0.5f), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.Path.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void Route_VeryTall_StillCompletes()
    {
        var bvh = CreateFarBvh();
        var route = PillarRouter.Route(new Vector3(0, 0, 500), 0.5f, bvh,
            new PillarRouter.RoutingConfig());

        route.ReachesGround.Should().BeTrue();
        route.TotalLength.Should().BeGreaterThan(400);
    }

    [Fact]
    public void Route_DifferentRadii_ProducesDifferentWideningPaths()
    {
        var bvh = CreateFarBvh();
        var thin = PillarRouter.Route(new Vector3(0, 0, 50), 0.3f, bvh,
            new PillarRouter.RoutingConfig { WideningFactor = 0.02f });
        var thick = PillarRouter.Route(new Vector3(0, 0, 50), 1.5f, bvh,
            new PillarRouter.RoutingConfig { WideningFactor = 0.02f });

        var thinMaxR = thin.Path.Max(w => w.Radius);
        var thickMaxR = thick.Path.Max(w => w.Radius);
        thickMaxR.Should().BeGreaterThan(thinMaxR);
    }

    [Fact]
    public void Route_ZeroWidening_ConstantRadius()
    {
        var bvh = CreateFarBvh();
        var route = PillarRouter.Route(new Vector3(0, 0, 30), 0.5f, bvh,
            new PillarRouter.RoutingConfig { WideningFactor = 0f });

        var pillarWps = route.Path.Where(w => w.Type == "pillar").ToList();
        if (pillarWps.Count >= 2)
        {
            var radii = pillarWps.Select(w => w.Radius).Distinct().ToList();
            radii.Count.Should().Be(1, "zero widening = constant pillar radius");
        }
    }
}
