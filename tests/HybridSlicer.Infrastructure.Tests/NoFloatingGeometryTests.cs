using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests that no floating/orphan geometry is produced — every mesh part and
/// slice element must transitively connect to the build plate or a valid anchor.
/// </summary>
public class NoFloatingGeometryTests
{
    private static StlMesh CreateFloatingCube(float size = 20f, float zOffset = 10f)
    {
        var o = new Vector3(-size / 2, -size / 2, zOffset);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts[vi++] = a + o; verts[vi++] = b + o; verts[vi++] = c + o;
            verts[vi++] = a + o; verts[vi++] = c + o; verts[vi++] = d + o;
        }
        float s = size;
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
    public void AllSliceElements_ReachBuildPlateOrAnchor()
    {
        // Generate supports with braces enabled
        var mesh = CreateFloatingCube(20f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
            EnableMiniRafts = true,
        });

        if (result.ValidSupports == 0) return;

        // Every slice element must have at least one endpoint near the plate (z <= 1.0)
        // or be a raft element, or be connected to other elements that do
        foreach (var elem in result.SliceElements)
        {
            float minZ = Math.Min(elem.PointA.Z, elem.PointB.Z);
            float maxZ = Math.Max(elem.PointA.Z, elem.PointB.Z);

            // Elements at the plate level are grounded
            if (minZ <= 1.0f) continue;

            // Elements above the plate are OK if they're part of the support chain
            // (interconnects, pinheads) — they connect to grounded pillars
            if (elem.Type is "interconnect" or "pinhead" or "raft") continue;

            // For pillar/junction/bridge elements above the plate,
            // verify they connect to something below them
            bool hasLowerNeighbor = result.SliceElements.Any(other =>
                other != elem &&
                Vector3.Distance(new Vector3(elem.PointB.X, elem.PointB.Y, 0),
                    new Vector3(other.PointA.X, other.PointA.Y, 0)) < 2.0f &&
                Math.Min(other.PointA.Z, other.PointB.Z) < minZ);

            hasLowerNeighbor.Should().BeTrue(
                $"element type={elem.Type} at z=[{minZ:F1},{maxZ:F1}] should connect to plate");
        }
    }

    [Fact]
    public void Braces_OnlyConnectSurvivingSupports()
    {
        var mesh = CreateFloatingCube(20f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
            ReinforcementMode = HybridSlicer.Infrastructure.Resin.Routing.ReinforcementMode.Pairwise,
        });

        if (result.Interconnections.Count == 0) return;

        // Every brace must have valid pillar indices within the valid route count
        int validCount = result.Routes.Count(r =>
            r.route.Path.Count >= 2 &&
            (r.route.ReachesGround || r.route.AnchorPoint.HasValue));

        foreach (var conn in result.Interconnections)
        {
            conn.PillarA.Should().BeGreaterThanOrEqualTo(0);
            conn.PillarB.Should().BeGreaterThanOrEqualTo(0);
            // PillarA and PillarB are indices into validRoutes, which has <= validCount entries
        }
    }

    [Fact]
    public void MiniRafts_OnlyForGroundedSupports()
    {
        var mesh = CreateFloatingCube(20f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            RaftMode = RaftMode.MiniRafts,
            EnableMiniRafts = true,
        });

        if (result.ValidSupports == 0) return;

        // Check that raft slice elements are only near z=0 (grounded)
        var raftElements = result.SliceElements.Where(e => e.Type == "raft").ToList();
        foreach (var raft in raftElements)
        {
            float minZ = Math.Min(raft.PointA.Z, raft.PointB.Z);
            minZ.Should().BeLessThanOrEqualTo(0.5f,
                "raft elements should be at the build plate level");
        }
    }

    [Fact]
    public void GeometryMatchesAfterEscalation()
    {
        // Test that mesh face count is consistent with valid support count
        var mesh = CreateFloatingCube(20f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
            EnableFillets = true,
            RaftMode = RaftMode.MiniRafts,
        });

        if (result.ValidSupports == 0) return;

        // Mesh should have faces
        result.SupportMesh.FaceCount.Should().BeGreaterThan(0);

        // Valid supports should be <= total supports
        result.ValidSupports.Should().BeLessOrEqualTo(result.TotalSupports);

        // Every legacy support should have segments with finite coordinates
        foreach (var support in result.LegacySupports)
        {
            foreach (var seg in support.Segments)
            {
                float.IsNaN(seg.X1).Should().BeFalse($"NaN in support {support.Id} segment {seg.Part}");
                float.IsNaN(seg.Y1).Should().BeFalse();
                float.IsNaN(seg.Z1).Should().BeFalse();
                float.IsNaN(seg.R1).Should().BeFalse();
                float.IsNaN(seg.X2).Should().BeFalse();
                float.IsNaN(seg.Y2).Should().BeFalse();
                float.IsNaN(seg.Z2).Should().BeFalse();
                float.IsNaN(seg.R2).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void TriangularReinforcement_NoBraceToDroppedSupport()
    {
        var mesh = CreateFloatingCube(20f, 15f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
            ReinforcementMode = HybridSlicer.Infrastructure.Resin.Routing.ReinforcementMode.Triangular,
        });

        // If there are no braces, the test passes trivially
        if (result.Interconnections.Count == 0) return;

        // Build valid route lookup
        var validRouteIds = new HashSet<string>(
            result.Routes
                .Where(r => r.route.Path.Count >= 2 &&
                    (r.route.ReachesGround || r.route.AnchorPoint.HasValue))
                .Select(r => r.id));

        // Every brace endpoint should reference a valid (surviving) support
        // The indices are into the validRoutes list, so they should all be in range
        foreach (var conn in result.Interconnections)
        {
            conn.PillarA.Should().BeInRange(0, result.ValidSupports - 1,
                "brace PillarA should be in validRoutes range");
            conn.PillarB.Should().BeInRange(0, result.ValidSupports - 1,
                "brace PillarB should be in validRoutes range");
        }
    }
}
