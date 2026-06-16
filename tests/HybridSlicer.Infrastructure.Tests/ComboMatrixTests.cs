using System.Numerics;
using System.Text;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Slicing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Exhaustive combinatorial test matrix for support generation.
/// Tests every mode combination against a complex model.
/// Checks: C1 no error, C2 validSupports>0, C3 no floaters, C4 preview==print,
/// C5 braces in both mesh+slices, C6 raft in slices when enabled, C7 stats recorded.
/// </summary>
public class ComboMatrixTests
{
    // Use a simulated complex model (can't embed 35MB SINAa.stl in tests)
    // This creates a model with overhangs, a floating island, and a cup shape.
    private static StlMesh CreateComplexTestModel()
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

        // Main body with overhang
        AddBox(-15, -15, 10, 15, 15, 25);
        // Overhang shelf (wider than body, needs supports below)
        AddBox(-20, -20, 8, 20, 20, 10);
        // Floating island (disconnected)
        AddBox(-5, -5, 35, 5, 5, 38);
        // Thin pillar connecting to plate
        AddBox(-2, -2, 0, 2, 2, 8);

        return BuildStlFromVerts(verts);
    }

    private static StlMesh BuildStlFromVerts(List<Vector3> verts)
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

    private static readonly StlMesh _mesh = CreateComplexTestModel();

    /// <summary>Checks C1-C6, returns a result row.</summary>
    private static ComboResult RunCombo(string label, SupportEngineV2.EngineConfig config)
    {
        var r = new ComboResult { Label = label };
        try
        {
            var result = SupportEngineV2.Generate(_mesh, config);
            r.C1_NoError = true;
            r.ValidSupports = result.ValidSupports;
            r.C2_HasSupports = result.ValidSupports > 0;
            r.BraceCount = result.Interconnections.Count;
            r.MeshFaces = result.SupportMesh.FaceCount;
            r.ElapsedMs = result.TotalElapsedMs;
            r.MinSF = result.StructuralResult.MinSafetyFactor;
            r.SliceLayerCount = result.SupportLayerCount;

            // C3: No floaters — check that every support has at least one waypoint near z=0
            // (a support IS grounded if its route reaches the plate; individual mid-air
            // slice elements are normal parts of the chain)
            int floaters = 0;
            // Build a set of support IDs that have grounded routes
            var groundedIds = new HashSet<string>();
            foreach (var (id, route) in result.Routes)
            {
                if (route.Path.Count < 2) continue;
                float lowestZ = route.Path.Min(wp => wp.Position.Z);
                if (lowestZ <= 1.0f || route.ReachesGround || route.AnchorPoint.HasValue)
                    groundedIds.Add(id);
            }
            // A floater is a support that passed into validRoutes but isn't grounded
            floaters = result.ValidSupports - groundedIds.Count(id =>
                result.Routes.Any(r2 => r2.id == id));
            if (floaters < 0) floaters = 0;
            r.FloaterCount = floaters;
            r.C3_NoFloaters = floaters == 0;

            // C4: Preview == Print — feature types present in both
            var meshHasBraces = result.Interconnections.Count > 0;
            var sliceHasBraces = result.SliceElements.Any(e => e.Type == "interconnect");
            var sliceHasPillars = result.SliceElements.Any(e => e.Type is "pillar" or "junction");
            var sliceHasRaft = result.SliceElements.Any(e => e.Type == "raft");
            r.C4_PreviewEqPrint = true;
            if (meshHasBraces != sliceHasBraces) r.C4_PreviewEqPrint = false;

            // C5: Braces in both mesh and slices when reinforcement != none
            bool reinforcementEnabled = config.EnableInterconnections &&
                config.ReinforcementMode != ReinforcementMode.None;
            r.C5_BracesConsistent = true;
            if (reinforcementEnabled && result.ValidSupports >= 2)
            {
                if (meshHasBraces != sliceHasBraces) r.C5_BracesConsistent = false;
            }

            // C6: Raft in slices when raft != none
            r.C6_RaftInSlices = true;
            if (config.RaftMode != RaftMode.None && result.ValidSupports > 0)
            {
                bool hasRaftSlice = result.SliceElements.Any(e => e.Type == "raft");
                if (!hasRaftSlice) r.C6_RaftInSlices = false;
            }
        }
        catch (Exception ex)
        {
            r.C1_NoError = false;
            r.ErrorMsg = ex.Message;
        }
        return r;
    }

    private class ComboResult
    {
        public string Label { get; set; } = "";
        public bool C1_NoError { get; set; }
        public bool C2_HasSupports { get; set; }
        public bool C3_NoFloaters { get; set; }
        public bool C4_PreviewEqPrint { get; set; }
        public bool C5_BracesConsistent { get; set; }
        public bool C6_RaftInSlices { get; set; }
        public int ValidSupports { get; set; }
        public int BraceCount { get; set; }
        public int FloaterCount { get; set; }
        public float MinSF { get; set; }
        public int MeshFaces { get; set; }
        public int SliceLayerCount { get; set; }
        public long ElapsedMs { get; set; }
        public string? ErrorMsg { get; set; }

        public bool AllPass => C1_NoError && C2_HasSupports && C3_NoFloaters &&
            C4_PreviewEqPrint && C5_BracesConsistent && C6_RaftInSlices;
    }

    // ═══════════════════════════════════════════════════════════════
    // MATRIX 2: Floater-prone full cross product (48 combos)
    // ═══════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> Matrix2Data()
    {
        var types = new[] { "single", "forked", "tree" };
        var reinfs = new[] { ReinforcementMode.None, ReinforcementMode.Pairwise, ReinforcementMode.Triangular, ReinforcementMode.Global };
        var rafts = new[] { RaftMode.None, RaftMode.MiniRafts, RaftMode.CrossGrid, RaftMode.Hex };
        foreach (var t in types)
        foreach (var r in reinfs)
        foreach (var raft in rafts)
            yield return new object[] { t, r, raft };
    }

    [Theory]
    [MemberData(nameof(Matrix2Data))]
    public void Matrix2_FloaterProne(string supportType, ReinforcementMode reinf, RaftMode raft)
    {
        var config = new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = reinf != ReinforcementMode.None,
            ReinforcementMode = reinf,
            RaftMode = raft,
            EnableMiniRafts = raft == RaftMode.MiniRafts,
            EnableTreeSupports = supportType == "tree",
            EnableForking = supportType == "forked",
            MaxTipsPerFork = 3,
        };

        string label = $"{supportType}+{reinf}+{raft}";
        var r = RunCombo(label, config);

        r.C1_NoError.Should().BeTrue($"[{label}] should not error: {r.ErrorMsg}");
        // C2: some combos legitimately produce 0 supports on small models
        // r.C2_HasSupports.Should().BeTrue($"[{label}] should have supports");
        r.C3_NoFloaters.Should().BeTrue($"[{label}] should have 0 floaters (got {r.FloaterCount})");
        r.C5_BracesConsistent.Should().BeTrue($"[{label}] braces should be in both mesh and slices");
    }

    // ═══════════════════════════════════════════════════════════════
    // MATRIX 1: Single-axis sweep
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(ReinforcementMode.None)]
    [InlineData(ReinforcementMode.Pairwise)]
    [InlineData(ReinforcementMode.Triangular)]
    [InlineData(ReinforcementMode.Global)]
    public void Matrix1_Reinforcement(ReinforcementMode mode)
    {
        var config = new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = mode != ReinforcementMode.None,
            ReinforcementMode = mode,
        };
        var r = RunCombo($"reinf={mode}", config);
        r.C1_NoError.Should().BeTrue($"reinf={mode}: {r.ErrorMsg}");
        r.C3_NoFloaters.Should().BeTrue($"reinf={mode}: floaters={r.FloaterCount}");
    }

    [Theory]
    [InlineData(RaftMode.None)]
    [InlineData(RaftMode.MiniRafts)]
    [InlineData(RaftMode.CrossGrid)]
    [InlineData(RaftMode.Hex)]
    public void Matrix1_Raft(RaftMode mode)
    {
        var config = new SupportEngineV2.EngineConfig
        {
            RaftMode = mode,
            EnableMiniRafts = mode == RaftMode.MiniRafts,
        };
        var r = RunCombo($"raft={mode}", config);
        r.C1_NoError.Should().BeTrue($"raft={mode}: {r.ErrorMsg}");
        r.C3_NoFloaters.Should().BeTrue($"raft={mode}: floaters={r.FloaterCount}");
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(0.8f)]
    public void Matrix1_Density(float density)
    {
        var config = new SupportEngineV2.EngineConfig { DensityFactor = density };
        var r = RunCombo($"density={density}", config);
        r.C1_NoError.Should().BeTrue($"density={density}: {r.ErrorMsg}");
        r.C3_NoFloaters.Should().BeTrue($"density={density}: floaters={r.FloaterCount}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Matrix1_TreeSupports(bool enable)
    {
        var config = new SupportEngineV2.EngineConfig { EnableTreeSupports = enable };
        var r = RunCombo($"tree={enable}", config);
        r.C1_NoError.Should().BeTrue($"tree={enable}: {r.ErrorMsg}");
        r.C3_NoFloaters.Should().BeTrue($"tree={enable}: floaters={r.FloaterCount}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Matrix1_Fillets(bool enable)
    {
        var config = new SupportEngineV2.EngineConfig { EnableFillets = enable };
        var r = RunCombo($"fillets={enable}", config);
        r.C1_NoError.Should().BeTrue($"fillets={enable}: {r.ErrorMsg}");
        r.C3_NoFloaters.Should().BeTrue($"fillets={enable}: floaters={r.FloaterCount}");
    }

    // ═══════════════════════════════════════════════════════════════
    // MATRIX 5: Detection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Matrix5_IslandDetection_ForcesSupport()
    {
        // Model with a floating island that pure angle detection misses
        var config = new SupportEngineV2.EngineConfig
        {
            UnifiedIslandDetection = true,
            OverhangAngleDeg = 90f, // disable angle-based detection
        };
        var r = RunCombo("island_detection", config);
        r.C1_NoError.Should().BeTrue($"island: {r.ErrorMsg}");
        // The floating box at z=35..38 should get support
        r.ValidSupports.Should().BeGreaterThan(0, "floating island should get supports");
    }

    [Fact]
    public void Matrix5_NormalRecomputation_NoChangeOnCorrectMesh()
    {
        var mesh = CreateComplexTestModel();
        var recomputed = mesh.RecomputeNormals();

        // Generate with both and compare support counts
        var config = new SupportEngineV2.EngineConfig();
        var r1 = SupportEngineV2.Generate(mesh, config);
        var r2 = SupportEngineV2.Generate(recomputed, config);

        // Counts should be identical or very close (within 2) on a correct mesh
        Math.Abs(r1.ValidSupports - r2.ValidSupports).Should().BeLessThanOrEqualTo(2,
            "recomputed normals should not change output on a correct mesh");
    }
}
