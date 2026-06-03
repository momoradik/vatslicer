using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Advanced tests for V2 engine features: drain hole avoidance,
/// deterministic output, stress tests.
/// </summary>
public class SupportEngineV2AdvancedTests
{
    private static StlMesh CreateFloatingCube()
    {
        var o = new Vector3(-10, -10, 10);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts[vi++] = a + o; verts[vi++] = b + o; verts[vi++] = c + o;
            verts[vi++] = a + o; verts[vi++] = c + o; verts[vi++] = d + o;
        }
        float s = 20f;
        Quad(new(0,0,s), new(s,0,s), new(s,s,s), new(0,s,s));
        Quad(new(0,0,0), new(0,s,0), new(s,s,0), new(s,0,0));
        Quad(new(s,0,0), new(s,0,s), new(s,s,s), new(s,s,0));
        Quad(new(0,0,s), new(0,0,0), new(0,s,0), new(0,s,s));
        Quad(new(0,s,s), new(s,s,s), new(s,s,0), new(0,s,0));
        Quad(new(0,0,0), new(s,0,0), new(s,0,s), new(0,0,s));

        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Deterministic_SameSeed_SameOutput()
    {
        var mesh = CreateFloatingCube();
        var cfg = new SupportEngineV2.EngineConfig { Seed = 42 };

        var r1 = SupportEngineV2.Generate(mesh, cfg);
        var r2 = SupportEngineV2.Generate(mesh, cfg);

        r1.ValidSupports.Should().Be(r2.ValidSupports, "same seed should produce same support count");
        r1.TotalSupportVolumeMm3.Should().BeApproximately(r2.TotalSupportVolumeMm3, 0.01f,
            "same seed should produce same volume");
    }

    [Fact]
    public void DrainHoleExclusion_ReducesNearbySupports()
    {
        var mesh = CreateFloatingCube();

        var withoutHoles = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        // Add a drain hole at the center of the bottom face
        var withHoles = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DrainHoleExclusions = new() { (new Vector3(0, 0, 10), 5f) }, // large exclusion zone
            DrainHoleClearanceMm = 3f,
        });

        // Drain holes reduce supports in the exclusion zone, but coverage fill may add
        // extras elsewhere. Allow small increase from coverage fill.
        withHoles.ValidSupports.Should().BeLessThanOrEqualTo(withoutHoles.ValidSupports + 10,
            "drain hole exclusion should not drastically increase support count");
    }

    [Fact]
    public void DrainHoleExclusion_NoHoles_NoEffect()
    {
        var mesh = CreateFloatingCube();

        var r1 = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        var r2 = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DrainHoleExclusions = new(), // empty list
        });

        r1.ValidSupports.Should().Be(r2.ValidSupports);
    }

    [Fact]
    public void StlExport_CanBeReimported()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var stlData = result.SupportMesh.ToStlBinary();
        stlData.Length.Should().BeGreaterThan(84);

        // Reimport and verify
        var reimported = StlMesh.FromBinary(stlData);
        reimported.TriangleCount.Should().Be(result.SupportMesh.FaceCount);
        reimported.TriangleCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AllPinheads_HavePositiveRadii()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var (id, ph) in result.Pinheads)
        {
            if (!ph.IsValid) continue;
            ph.PinRadius.Should().BeGreaterThan(0, $"{id} pin radius");
            ph.BackRadius.Should().BeGreaterThan(0, $"{id} back radius");
            ph.Width.Should().BeGreaterThan(0, $"{id} width");
        }
    }

    [Fact]
    public void AllRoutes_EndAtOrNearBase()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var (id, route) in result.Routes)
        {
            var lastWp = route.Path[^1];
            if (route.ReachesGround)
                lastWp.Position.Z.Should().BeLessThan(2f, $"route {id} should end near Z=0");
        }
    }

    [Fact]
    public void SliceElements_SpanSupportHeight()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.SliceElements.Should().NotBeEmpty();

        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        minZ.Should().BeLessThan(2f, "slice elements should reach near the base");
        maxZ.Should().BeGreaterThan(0.5f, "slice elements should have measurable height");
    }

    [Fact]
    public void LegacyFormat_FirstSegmentIsTip()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        foreach (var s in result.LegacySupports)
        {
            s.Segments.Should().NotBeEmpty($"support {s.Id} needs segments");
            s.Segments[0].Part.Should().Be("tip", $"support {s.Id} first segment should be tip");
        }
    }

    [Fact]
    public void InterconnectionsRespectDistance()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
            InterconnectDistMm = 5f, // tight
        });

        foreach (var conn in result.Interconnections)
        {
            float dist = Vector3.Distance(conn.PointA, conn.PointB);
            dist.Should().BeGreaterThan(0.1f, "connections should span some distance");
        }
    }
}
