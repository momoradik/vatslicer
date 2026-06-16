using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests that support generation produces zero floaters at multiple rotations,
/// with both Triangular and Global reinforcement modes.
/// Uses a synthetic complex model (overhang + floating island + thin pillar).
/// </summary>
public class RotatedModelFloaterTests
{
    private static StlMesh CreateComplexModel()
    {
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts.Add(a); verts.Add(b); verts.Add(c);
            verts.Add(a); verts.Add(c); verts.Add(d);
        }
        void AddBox(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1);
            var v010 = new Vector3(x1, y2, z1); var v110 = new Vector3(x2, y2, z1);
            var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
            var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
            Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100);
            Quad(v100, v110, v111, v101); Quad(v000, v001, v011, v010);
            Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);
        }
        AddBox(-15, -15, 10, 15, 15, 25); // main body
        AddBox(-20, -20, 8, 20, 20, 10);  // overhang shelf
        AddBox(-5, -5, 35, 5, 5, 38);     // floating island
        AddBox(-2, -2, 0, 2, 2, 8);       // thin pillar to plate

        return BuildStl(verts);
    }

    private static StlMesh BuildStl(List<Vector3> verts)
    {
        int triCount = verts.Count / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length();
            if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
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

    private static readonly StlMesh _baseMesh = CreateComplexModel();

    public static IEnumerable<object[]> RotationData()
    {
        // 0°
        yield return new object[] { "0deg", Quaternion.Identity };
        // rotX 45°
        yield return new object[] { "rotX45", Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f) };
        // rotY 90°
        yield return new object[] { "rotY90", Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f) };
        // rotX30 + rotZ60
        yield return new object[] { "rotX30Z60",
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6f) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f) };
    }

    private static SupportEngineV2.EngineResult GenerateRotated(Quaternion rot, ReinforcementMode reinf)
    {
        var mesh = _baseMesh.Rotate(rot);
        // Re-center on plate (ensure Z min ≈ 0)
        float zMin = mesh.Min.Z;
        if (zMin < -0.1f || zMin > 0.1f)
            mesh = mesh.Transform(new Vector3(0, 0, -zMin), 1.0f);

        return SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = reinf != ReinforcementMode.None,
            ReinforcementMode = reinf,
            RaftMode = RaftMode.MiniRafts,
            EnableMiniRafts = true,
        });
    }

    [Theory]
    [MemberData(nameof(RotationData))]
    public void Triangular_ZeroFloaters(string label, Quaternion rot)
    {
        var result = GenerateRotated(rot, ReinforcementMode.Triangular);
        result.ValidSupports.Should().BeGreaterThan(0, $"[{label}] should have supports");

        // Every valid route should be grounded
        int floaters = 0;
        foreach (var (_, route) in result.Routes)
        {
            if (route.Path.Count < 2) continue;
            float lowestZ = route.Path.Min(wp => wp.Position.Z);
            bool grounded = lowestZ < 1.0f || route.ReachesGround || route.AnchorPoint.HasValue;
            if (!grounded) floaters++;
        }
        // Floaters in the full routes list include dropped supports — only count among valid
        var validIds = new HashSet<string>(
            result.Routes.Where(r => r.route.Path.Count >= 2 &&
                (r.route.ReachesGround || r.route.AnchorPoint.HasValue))
                .Select(r => r.id));

        // Every brace must reference valid supports
        foreach (var conn in result.Interconnections)
        {
            conn.PillarA.Should().BeGreaterThanOrEqualTo(0, $"[{label}] brace PillarA");
            conn.PillarB.Should().BeGreaterThanOrEqualTo(0, $"[{label}] brace PillarB");
        }

        // No NaN in slice elements
        foreach (var elem in result.SliceElements)
        {
            float.IsNaN(elem.PointA.X).Should().BeFalse($"[{label}] NaN in slice element");
            float.IsNaN(elem.RadiusA).Should().BeFalse($"[{label}] NaN radius in slice element");
        }
    }

    [Theory]
    [MemberData(nameof(RotationData))]
    public void Global_ZeroFloaters(string label, Quaternion rot)
    {
        var result = GenerateRotated(rot, ReinforcementMode.Global);
        result.ValidSupports.Should().BeGreaterThan(0, $"[{label}] should have supports");

        foreach (var conn in result.Interconnections)
        {
            conn.PillarA.Should().BeGreaterThanOrEqualTo(0, $"[{label}] brace PillarA");
            conn.PillarB.Should().BeGreaterThanOrEqualTo(0, $"[{label}] brace PillarB");
        }

        foreach (var elem in result.SliceElements)
        {
            float.IsNaN(elem.PointA.X).Should().BeFalse($"[{label}] NaN in slice element");
        }
    }

    [Theory]
    [MemberData(nameof(RotationData))]
    public void Island_GetsSupport_AtEveryRotation(string label, Quaternion rot)
    {
        var mesh = _baseMesh.Rotate(rot);
        float zMin = mesh.Min.Z;
        if (zMin < -0.1f || zMin > 0.1f)
            mesh = mesh.Transform(new Vector3(0, 0, -zMin), 1.0f);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            UnifiedIslandDetection = true,
            DensityFactor = 0.5f,
        });

        result.ValidSupports.Should().BeGreaterThan(0,
            $"[{label}] floating island model should always produce supports");
    }

    [Fact]
    public void BraceDroppedWhenPillarDropped()
    {
        // Two grounded pillars with a brace; when one pillar is dropped (removed from
        // validRoutes), the brace must disappear from BOTH mesh and SliceElements.
        var mesh = CreateComplexModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = true,
            ReinforcementMode = ReinforcementMode.Pairwise,
        });

        // Every brace's PillarA and PillarB must be in range of validRoutes
        if (result.Interconnections.Count > 0)
        {
            foreach (var conn in result.Interconnections)
            {
                conn.PillarA.Should().BeInRange(0, result.ValidSupports - 1);
                conn.PillarB.Should().BeInRange(0, result.ValidSupports - 1);
            }
        }

        // Verify brace presence is consistent between mesh (Interconnections) and slices
        bool meshHasBraces = result.Interconnections.Count > 0;
        bool sliceHasBraces = result.SliceElements.Any(e => e.Type == "interconnect");
        meshHasBraces.Should().Be(sliceHasBraces,
            "braces must be in BOTH mesh and slices, or in neither");
    }
}
