using System.Numerics;
using HybridSlicer.Domain.Enums;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>
/// Advanced resin support generation engine.
/// 100% original implementation — no external libraries.
///
/// Support types:
/// - Light: minimal contact, thin shaft, easy removal, small marks
/// - Medium: balanced contact, standard shaft, reliable
/// - Heavy: maximum contact, thick shaft, critical overhangs
/// - Tree: branching structure — multiple tips merge into shared trunks
/// - CrossBraced: columns with diagonal struts for rigidity
///
/// Support anatomy (per support):
/// - Contact tip: sphere/cone at model surface
/// - Neck: thin breakpoint for easy removal
/// - Upper taper: transition from neck to shaft
/// - Shaft: main column body
/// - Lower taper: transition from shaft to base
/// - Base/foot: wider platform on build plate
///
/// Cross-bracing:
/// - Diagonal struts between adjacent supports at regular intervals
/// - Dramatically increases rigidity for tall/thin parts
/// </summary>
public static class AdvancedSupportEngine
{
    // ── Support presets ──────────────────────────────────────────────────────

    public sealed record SupportPreset
    {
        public string Name { get; init; } = "Medium";
        // Contact tip
        public float TipDiameterMm { get; init; } = 0.5f;
        public string TipShape { get; init; } = "sphere";  // point|sphere|cone|flat|pyramid|skate|chisel|mushroom|cross|ring|needle
        public float ContactDepthMm { get; init; } = 0.2f; // penetration into model
        // Neck (breakpoint)
        public float NeckDiameterMm { get; init; } = 0.3f;
        public float NeckLengthMm { get; init; } = 0.5f;
        public string NeckType { get; init; } = "thin";     // thin|waist|perforated|graduated|double
        // Shaft
        public float ShaftDiameterMm { get; init; } = 0.8f;
        public string ShaftType { get; init; } = "cylinder"; // cylinder|cone|hollow|square|xprofile|ibeam|lattice|spiral|ribbed|diamond
        // Tapers
        public float UpperTaperLengthMm { get; init; } = 1.0f;
        public float LowerTaperLengthMm { get; init; } = 1.5f;
        // Base/foot
        public float BaseDiameterMm { get; init; } = 2.0f;
        public float BaseHeightMm { get; init; } = 0.5f;
        public string BaseType { get; init; } = "disc";     // disc|cone|pyramid|raft|miniraft|pin|skirted|webbed|anchor|pad
        // Cross-brace
        public float BraceDiameterMm { get; init; } = 0.4f;
        public float BraceIntervalMm { get; init; } = 5.0f;
        public string BraceType { get; init; } = "diagonal"; // diagonal|horizontal|truss|ladder|shore
        // Structure
        public string StructureType { get; init; } = "column"; // column|tapered|tree|crossbraced|lattice|wall|gusset|cage|organic|truss|scaffold
    }

    // ── ALL SUPPORT PRESETS (from research taxonomy) ─────────────────────────

    // --- Column types (weight variants) ---
    public static readonly SupportPreset LightPreset = new() {
        Name="Light", TipDiameterMm=0.25f, TipShape="point", ContactDepthMm=0.1f,
        NeckDiameterMm=0.12f, NeckLengthMm=0.3f, NeckType="thin",
        ShaftDiameterMm=0.4f, ShaftType="cylinder",
        UpperTaperLengthMm=0.5f, LowerTaperLengthMm=1.0f,
        BaseDiameterMm=1.2f, BaseHeightMm=0.3f, BaseType="disc",
        BraceDiameterMm=0.2f, BraceIntervalMm=8.0f, StructureType="column",
    };
    public static readonly SupportPreset MediumPreset = new() {
        Name="Medium", TipDiameterMm=0.5f, TipShape="cone", ContactDepthMm=0.2f,
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, NeckType="waist",
        ShaftDiameterMm=0.8f, ShaftType="cylinder",
        UpperTaperLengthMm=1.0f, LowerTaperLengthMm=1.5f,
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, BaseType="disc",
        BraceDiameterMm=0.4f, BraceIntervalMm=5.0f, StructureType="column",
    };
    public static readonly SupportPreset HeavyPreset = new() {
        Name="Heavy", TipDiameterMm=1.0f, TipShape="flat", ContactDepthMm=0.4f,
        NeckDiameterMm=0.6f, NeckLengthMm=0.8f, NeckType="graduated",
        ShaftDiameterMm=1.5f, ShaftType="cylinder",
        UpperTaperLengthMm=1.5f, LowerTaperLengthMm=2.0f,
        BaseDiameterMm=3.0f, BaseHeightMm=0.8f, BaseType="anchor",
        BraceDiameterMm=0.6f, BraceIntervalMm=4.0f, StructureType="column",
    };

    // --- Tip shape variants ---
    public static readonly SupportPreset PointTipPreset = new() {
        Name="Point Tip", TipDiameterMm=0.15f, TipShape="point", ContactDepthMm=0.05f,
        NeckDiameterMm=0.1f, NeckLengthMm=0.2f, ShaftDiameterMm=0.35f,
        BaseDiameterMm=1.0f, BaseHeightMm=0.3f, StructureType="column",
    };
    public static readonly SupportPreset PyramidTipPreset = new() {
        Name="Pyramid Tip", TipDiameterMm=0.5f, TipShape="pyramid", ContactDepthMm=0.3f,
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, ShaftDiameterMm=0.8f,
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset SkateTipPreset = new() {
        Name="Skate Tip", TipDiameterMm=0.4f, TipShape="skate", ContactDepthMm=0.2f,
        NeckDiameterMm=0.25f, NeckLengthMm=0.4f, ShaftDiameterMm=0.7f,
        BaseDiameterMm=1.8f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset ChiselTipPreset = new() {
        Name="Chisel Tip", TipDiameterMm=0.3f, TipShape="chisel", ContactDepthMm=0.15f,
        NeckDiameterMm=0.2f, NeckLengthMm=0.3f, ShaftDiameterMm=0.6f,
        BaseDiameterMm=1.5f, BaseHeightMm=0.4f, StructureType="column",
    };
    public static readonly SupportPreset MushroomTipPreset = new() {
        Name="Mushroom Tip", TipDiameterMm=0.6f, TipShape="mushroom", ContactDepthMm=0.15f,
        NeckDiameterMm=0.2f, NeckLengthMm=0.6f, NeckType="double",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.0f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset CrossTipPreset = new() {
        Name="Cross Tip", TipDiameterMm=0.6f, TipShape="cross", ContactDepthMm=0.3f,
        NeckDiameterMm=0.35f, NeckLengthMm=0.5f, ShaftDiameterMm=0.9f,
        BaseDiameterMm=2.2f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset RingTipPreset = new() {
        Name="Ring Tip", TipDiameterMm=0.5f, TipShape="ring", ContactDepthMm=0.1f,
        NeckDiameterMm=0.25f, NeckLengthMm=0.4f, ShaftDiameterMm=0.7f,
        BaseDiameterMm=1.8f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset NeedleTipPreset = new() {
        Name="Needle Tip", TipDiameterMm=0.1f, TipShape="needle", ContactDepthMm=0.5f,
        NeckDiameterMm=0.08f, NeckLengthMm=0.2f, ShaftDiameterMm=0.3f,
        BaseDiameterMm=1.0f, BaseHeightMm=0.3f, StructureType="column",
    };

    // --- Shaft type variants ---
    public static readonly SupportPreset TaperedColumnPreset = new() {
        Name="Tapered Column", TipDiameterMm=0.5f, TipShape="cone",
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, ShaftDiameterMm=1.2f, ShaftType="cone",
        BaseDiameterMm=2.5f, BaseHeightMm=0.6f, StructureType="tapered",
    };
    public static readonly SupportPreset HollowTubePreset = new() {
        Name="Hollow Tube", TipDiameterMm=0.5f, TipShape="cone",
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, ShaftDiameterMm=1.0f, ShaftType="hollow",
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset SquareColumnPreset = new() {
        Name="Square Column", TipDiameterMm=0.5f, TipShape="pyramid",
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, ShaftDiameterMm=0.8f, ShaftType="square",
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, BaseType="pyramid", StructureType="column",
    };
    public static readonly SupportPreset XProfilePreset = new() {
        Name="X-Profile", TipDiameterMm=0.4f, TipShape="cross",
        NeckDiameterMm=0.25f, NeckLengthMm=0.4f, ShaftDiameterMm=0.8f, ShaftType="xprofile",
        BaseDiameterMm=1.8f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset IBeamPreset = new() {
        Name="I-Beam", TipDiameterMm=0.5f, TipShape="flat",
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, ShaftDiameterMm=1.0f, ShaftType="ibeam",
        BaseDiameterMm=2.5f, BaseHeightMm=0.6f, StructureType="column",
    };
    public static readonly SupportPreset LatticeColumnPreset = new() {
        Name="Lattice Column", TipDiameterMm=0.4f, TipShape="cone",
        NeckDiameterMm=0.25f, NeckLengthMm=0.4f, ShaftDiameterMm=1.2f, ShaftType="lattice",
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, StructureType="lattice",
    };
    public static readonly SupportPreset SpiralPreset = new() {
        Name="Spiral", TipDiameterMm=0.4f, TipShape="cone",
        NeckDiameterMm=0.25f, NeckLengthMm=0.4f, ShaftDiameterMm=0.6f, ShaftType="spiral",
        BaseDiameterMm=1.5f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset RibbedPreset = new() {
        Name="Ribbed", TipDiameterMm=0.5f, TipShape="cone",
        NeckDiameterMm=0.3f, NeckLengthMm=0.5f, ShaftDiameterMm=0.8f, ShaftType="ribbed",
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, StructureType="column",
    };
    public static readonly SupportPreset DiamondPreset = new() {
        Name="Diamond/Open", TipDiameterMm=0.4f, TipShape="cone",
        NeckDiameterMm=0.25f, NeckLengthMm=0.4f, ShaftDiameterMm=1.0f, ShaftType="diamond",
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f, StructureType="column",
    };

    // --- Base type variants ---
    public static readonly SupportPreset ConeBasePreset = new() {
        Name="Cone Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.5f, BaseHeightMm=0.8f, BaseType="cone", StructureType="column",
    };
    public static readonly SupportPreset PyramidBasePreset = new() {
        Name="Pyramid Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.5f, BaseHeightMm=0.8f, BaseType="pyramid", StructureType="column",
    };
    public static readonly SupportPreset RaftBasePreset = new() {
        Name="Raft Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=4.0f, BaseHeightMm=1.0f, BaseType="raft", StructureType="column",
    };
    public static readonly SupportPreset MiniRaftBasePreset = new() {
        Name="Mini Raft Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=3.0f, BaseHeightMm=0.6f, BaseType="miniraft", StructureType="column",
    };
    public static readonly SupportPreset PinBasePreset = new() {
        Name="Pin Base", TipDiameterMm=0.3f, TipShape="sphere",
        ShaftDiameterMm=0.5f, BaseDiameterMm=0.6f, BaseHeightMm=0.2f, BaseType="pin", StructureType="column",
    };
    public static readonly SupportPreset SkirtedBasePreset = new() {
        Name="Skirted Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.5f, BaseHeightMm=0.6f, BaseType="skirted", StructureType="column",
    };
    public static readonly SupportPreset WebbedBasePreset = new() {
        Name="Webbed Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.5f, BaseHeightMm=0.5f, BaseType="webbed", StructureType="column",
    };
    public static readonly SupportPreset AnchorBasePreset = new() {
        Name="Anchor Base", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.8f, BaseDiameterMm=3.5f, BaseHeightMm=1.0f, BaseType="anchor", StructureType="column",
    };

    // --- Structure type variants ---
    public static readonly SupportPreset WallBladePreset = new() {
        Name="Wall/Blade", TipDiameterMm=0.3f, TipShape="chisel",
        NeckDiameterMm=0.2f, NeckLengthMm=0.3f, ShaftDiameterMm=0.3f, ShaftType="cylinder",
        BaseDiameterMm=1.5f, BaseHeightMm=0.5f, StructureType="wall",
    };
    public static readonly SupportPreset SmallPillarPreset = new() {
        Name="Small Pillar", TipDiameterMm=0.2f, TipShape="point", ContactDepthMm=0.05f,
        NeckDiameterMm=0.1f, NeckLengthMm=0.15f, ShaftDiameterMm=0.25f,
        BaseDiameterMm=0.8f, BaseHeightMm=0.2f, BaseType="pin", StructureType="column",
    };
    public static readonly SupportPreset ScaffoldPreset = new() {
        Name="Scaffold", TipDiameterMm=0.4f, TipShape="cone",
        ShaftDiameterMm=0.5f, ShaftType="lattice",
        BaseDiameterMm=1.5f, BaseHeightMm=0.5f, BaseType="disc",
        BraceDiameterMm=0.3f, BraceIntervalMm=3.0f, BraceType="truss", StructureType="scaffold",
    };
    public static readonly SupportPreset TrussPreset = new() {
        Name="Truss", TipDiameterMm=0.5f, TipShape="cone",
        ShaftDiameterMm=0.6f, ShaftType="cylinder",
        BaseDiameterMm=2.0f, BaseHeightMm=0.5f,
        BraceDiameterMm=0.4f, BraceIntervalMm=3.0f, BraceType="truss", StructureType="truss",
    };
    public static readonly SupportPreset GussetPreset = new() {
        Name="Gusset", TipDiameterMm=0.4f, TipShape="flat",
        ShaftDiameterMm=0.6f, ShaftType="cylinder",
        BaseDiameterMm=1.5f, BaseHeightMm=0.4f, StructureType="gusset",
    };
    public static readonly SupportPreset CagePreset = new() {
        Name="Cage", TipDiameterMm=0.3f, TipShape="cone",
        ShaftDiameterMm=0.4f, ShaftType="lattice",
        BaseDiameterMm=1.5f, BaseHeightMm=0.5f,
        BraceDiameterMm=0.3f, BraceIntervalMm=2.0f, BraceType="truss", StructureType="cage",
    };
    public static readonly SupportPreset OrganicPreset = new() {
        Name="Organic", TipDiameterMm=0.3f, TipShape="sphere",
        NeckDiameterMm=0.2f, NeckLengthMm=0.4f, NeckType="graduated",
        ShaftDiameterMm=0.6f, ShaftType="spiral",
        BaseDiameterMm=1.8f, BaseHeightMm=0.5f, BaseType="webbed", StructureType="organic",
    };

    // --- Neck type variants ---
    public static readonly SupportPreset WaistNeckPreset = new() {
        Name="Waist Neck", TipDiameterMm=0.5f, TipShape="cone",
        NeckDiameterMm=0.2f, NeckLengthMm=0.6f, NeckType="waist",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.0f, StructureType="column",
    };
    public static readonly SupportPreset PerforatedNeckPreset = new() {
        Name="Perforated Neck", TipDiameterMm=0.5f, TipShape="cone",
        NeckDiameterMm=0.25f, NeckLengthMm=0.5f, NeckType="perforated",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.0f, StructureType="column",
    };
    public static readonly SupportPreset DoubleNeckPreset = new() {
        Name="Double Neck", TipDiameterMm=0.5f, TipShape="cone",
        NeckDiameterMm=0.2f, NeckLengthMm=0.8f, NeckType="double",
        ShaftDiameterMm=0.8f, BaseDiameterMm=2.0f, StructureType="column",
    };

    // ── ALL PRESETS DICTIONARY ───────────────────────────────────────────────

    public static readonly Dictionary<string, SupportPreset> AllPresets = new()
    {
        // Weight variants
        ["light"] = LightPreset, ["medium"] = MediumPreset, ["heavy"] = HeavyPreset,
        // Tip shapes
        ["point-tip"] = PointTipPreset, ["pyramid-tip"] = PyramidTipPreset, ["skate-tip"] = SkateTipPreset,
        ["chisel-tip"] = ChiselTipPreset, ["mushroom-tip"] = MushroomTipPreset, ["cross-tip"] = CrossTipPreset,
        ["ring-tip"] = RingTipPreset, ["needle-tip"] = NeedleTipPreset,
        // Shaft types
        ["tapered"] = TaperedColumnPreset, ["hollow-tube"] = HollowTubePreset,
        ["square"] = SquareColumnPreset, ["x-profile"] = XProfilePreset, ["i-beam"] = IBeamPreset,
        ["lattice-column"] = LatticeColumnPreset, ["spiral"] = SpiralPreset,
        ["ribbed"] = RibbedPreset, ["diamond-open"] = DiamondPreset,
        // Base types
        ["cone-base"] = ConeBasePreset, ["pyramid-base"] = PyramidBasePreset,
        ["raft-base"] = RaftBasePreset, ["miniraft-base"] = MiniRaftBasePreset,
        ["pin-base"] = PinBasePreset, ["skirted-base"] = SkirtedBasePreset,
        ["webbed-base"] = WebbedBasePreset, ["anchor-base"] = AnchorBasePreset,
        // Structure types
        ["tree"] = MediumPreset, ["crossbraced"] = MediumPreset, // use medium but structure differs
        ["wall-blade"] = WallBladePreset, ["small-pillar"] = SmallPillarPreset,
        ["scaffold"] = ScaffoldPreset, ["truss"] = TrussPreset,
        ["gusset"] = GussetPreset, ["cage"] = CagePreset, ["organic"] = OrganicPreset,
        // Neck types
        ["waist-neck"] = WaistNeckPreset, ["perforated-neck"] = PerforatedNeckPreset,
        ["double-neck"] = DoubleNeckPreset,
    };

    public static SupportPreset GetPreset(string type) =>
        AllPresets.TryGetValue(type, out var p) ? p : MediumPreset;

    // ── Support structure data model ────────────────────────────────────────

    /// <summary>One complete support structure with all anatomical components.</summary>
    public sealed record AdvancedSupport
    {
        public required string Id { get; init; }
        public required string Type { get; init; }    // light | medium | heavy | tree | crossbraced
        // Contact point on model
        public required float ContactX { get; init; }
        public required float ContactY { get; init; }
        public required float ContactZ { get; init; }
        public required float NormalX { get; init; }
        public required float NormalY { get; init; }
        public required float NormalZ { get; init; }
        // Anatomy dimensions
        public required SupportPreset Preset { get; init; }
        // Base point (on build plate or on another surface)
        public required float BaseX { get; init; }
        public required float BaseY { get; init; }
        public required float BaseZ { get; init; }
        // Tree: branch merge point (only for tree supports)
        public float? MergeZ { get; init; }
        public float? MergeX { get; init; }
        public float? MergeY { get; init; }
        public string? ParentTrunkId { get; init; } // which trunk this branch connects to
        // Segments for preview (detailed geometry points)
        public List<SupportSegment> Segments { get; init; } = [];
    }

    /// <summary>One geometric segment of a support for rendering.</summary>
    public sealed record SupportSegment
    {
        public required string Part { get; init; }   // tip | neck | upperTaper | shaft | lowerTaper | base | branch | brace
        public required float X1 { get; init; }
        public required float Y1 { get; init; }
        public required float Z1 { get; init; }
        public required float R1 { get; init; }      // radius at start
        public required float X2 { get; init; }
        public required float Y2 { get; init; }
        public required float Z2 { get; init; }
        public required float R2 { get; init; }      // radius at end
    }

    /// <summary>Cross-brace between two supports.</summary>
    public sealed record CrossBrace
    {
        public required string SupportA { get; init; }
        public required string SupportB { get; init; }
        public required float X1 { get; init; }
        public required float Y1 { get; init; }
        public required float Z1 { get; init; }
        public required float X2 { get; init; }
        public required float Y2 { get; init; }
        public required float Z2 { get; init; }
        public required float Diameter { get; init; }
    }

    public sealed record AdvancedSupportResult
    {
        public List<AdvancedSupport> Supports { get; init; } = [];
        public List<CrossBrace> CrossBraces { get; init; } = [];
        public int OverhangFaceCount { get; init; }
        public long ElapsedMs { get; init; }
    }

    // ── Generation config ───────────────────────────────────────────────────

    public sealed record AdvancedSupportConfig
    {
        public PrinterOrientation Orientation { get; init; } = PrinterOrientation.BottomUp;
        public string SupportType { get; init; } = "medium"; // light | medium | heavy | tree | crossbraced
        public string Placement { get; init; } = "buildplate";
        public double OverhangAngleDeg { get; init; } = 45;
        public double DensityFactor { get; init; } = 0.5;
        public bool CrossBracingEnabled { get; init; } = true;
        public double CrossBraceMaxDistMm { get; init; } = 8.0;
    }

    // ── Generation ──────────────────────────────────────────────────────────

    public static AdvancedSupportResult Generate(StlMesh mesh, AdvancedSupportConfig config)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var preset = GetPreset(config.SupportType);

        // Bottom-Up: thinner tips for surface quality
        if (config.Orientation == PrinterOrientation.BottomUp)
            preset = preset with { TipDiameterMm = preset.TipDiameterMm * 0.8f, NeckDiameterMm = preset.NeckDiameterMm * 0.8f };

        // Center mesh: place on bed (Z=0) and center XY
        float meshW = mesh.Max.X - mesh.Min.X;
        float meshD = mesh.Max.Y - mesh.Min.Y;
        float offX = -(mesh.Min.X + meshW / 2);
        float offY = -(mesh.Min.Y + meshD / 2);
        float offZ = -mesh.Min.Z;
        mesh = mesh.Transform(new Vector3(offX, offY, offZ), 1.0f);

        // Detect overhangs with severity scoring
        var overhangCos = MathF.Cos(MathF.PI / 180f * (float)config.OverhangAngleDeg);
        var gravityDir = new Vector3(0, 0, -1);
        var overhangPoints = new List<(Vector3 center, Vector3 normal, float area, float severity)>();

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            var area = cross.Length() * 0.5f;
            var normal = Vector3.Normalize(cross);
            if (float.IsNaN(normal.X) || area < 1e-8f) continue;

            float dot = Vector3.Dot(normal, gravityDir);
            if (dot > overhangCos)
            {
                var center = (v0 + v1 + v2) / 3f;
                if (center.Z < 0.2f) continue; // skip faces already on bed
                // Severity: 0 at threshold, 1 at fully downward. Drives adaptive density.
                float severity = (dot - overhangCos) / (1f - overhangCos);
                overhangPoints.Add((center, normal, area, severity));
            }
        }

        // Sort by severity (worst first), then Z descending, then area descending
        overhangPoints.Sort((a, b) =>
        {
            int sc = b.severity.CompareTo(a.severity);
            if (sc != 0) return sc;
            int zc = b.center.Z.CompareTo(a.center.Z);
            return zc != 0 ? zc : b.area.CompareTo(a.area);
        });

        // Adaptive density: tighter spacing for severe overhangs, wider for mild
        float baseSpacing = (float)(4.0 / (config.DensityFactor + 0.1));
        var supports = new List<AdvancedSupport>();
        var placed = new List<(Vector3 pos, float baseZ)>();
        int idCounter = 0;

        // Build spatial lookup for "everywhere" placement: find surfaces below overhang points
        bool placeEverywhere = config.Placement == "everywhere";

        foreach (var (center, normal, area, severity) in overhangPoints)
        {
            // Adaptive spacing: severe overhangs get 50% tighter spacing
            float localSpacing = baseSpacing * (1f - severity * 0.5f);
            localSpacing = Math.Max(localSpacing, 1.0f); // never closer than 1mm

            bool tooClose = placed.Any(p => Vector2.Distance(
                new Vector2(center.X, center.Y), new Vector2(p.pos.X, p.pos.Y)) < localSpacing);
            if (tooClose) continue;

            // Determine base Z: try to land on intermediate surfaces if "everywhere"
            float supportBaseZ = 0;
            if (placeEverywhere)
            {
                supportBaseZ = FindIntermediateSurface(mesh, center, center.Z);
            }

            var support = BuildSupport($"sup-{++idCounter}", config.SupportType, preset, center, normal, supportBaseZ);

            // Collision check: verify the shaft doesn't pass through the mesh
            if (!ShaftCollidesWithMesh(mesh, support))
            {
                supports.Add(support);
                placed.Add((center, supportBaseZ));
            }
        }

        // Tree merging (if tree type)
        if (config.SupportType == "tree" && supports.Count > 1)
            supports = MergeIntoTrees(supports, preset);

        // Cross-bracing
        var braces = new List<CrossBrace>();
        if (config.CrossBracingEnabled || config.SupportType == "crossbraced")
            braces = GenerateCrossBraces(supports, preset, (float)config.CrossBraceMaxDistMm);

        sw.Stop();
        return new AdvancedSupportResult
        {
            Supports = supports,
            CrossBraces = braces,
            OverhangFaceCount = overhangPoints.Count,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // ── Build one support with full anatomy ──────────────────────────────────
    //
    // PrusaSlicer-inspired pinhead geometry:
    //   1. PIN SPHERE — small sphere at model contact, oriented along clamped normal
    //   2. CONNECTING CONE — tangent cone from pin sphere to back sphere
    //   3. BACK SPHERE — larger sphere at junction point
    //   4. PILLAR — vertical shaft from junction down to base
    //   5. PEDESTAL — truncated cone at build plate
    //
    // The head direction = surface normal clamped to max 45° from vertical.
    // This creates a realistic angled approach that matches production slicers.

    private static AdvancedSupport BuildSupport(string id, string type, SupportPreset preset,
        Vector3 contact, Vector3 normal, float baseZ = 0)
    {
        var segments = new List<SupportSegment>();
        // PrusaSlicer naming: r_pin = small tip sphere, r_back = large junction sphere
        float rPin = preset.TipDiameterMm / 2;   // small sphere at model contact
        float rBack = preset.ShaftDiameterMm / 2; // back sphere at junction
        float tipR = rPin; // alias for shape-specific code
        float neckR = preset.NeckDiameterMm / 2;
        float shaftR = preset.ShaftDiameterMm / 2;
        float baseR = preset.BaseDiameterMm / 2;

        // ── Compute head direction (PrusaSlicer-style) ─────────────────────────
        // Start from surface normal, clamp polar angle to max bridge_slope (45°) from vertical
        var headDir = normal;
        // Overhangs have normals pointing downward; ensure downward component
        if (headDir.Z > -0.1f) headDir = new Vector3(headDir.X, headDir.Y, -1f);
        headDir = Vector3.Normalize(headDir);

        // Clamp to max 45° from vertical (like PrusaSlicer's bridge_slope)
        float cosMaxAngle = MathF.Cos(MathF.PI / 4f); // 45°
        if (-headDir.Z < cosMaxAngle)
        {
            // Too horizontal — project toward vertical while keeping XY direction
            float xyMag = MathF.Sqrt(headDir.X * headDir.X + headDir.Y * headDir.Y);
            float maxXY = MathF.Tan(MathF.PI / 4f); // tan(45°) = 1.0
            if (xyMag > 0.001f)
            {
                float scale = maxXY / xyMag * (-headDir.Z > 0.001f ? -headDir.Z : 0.5f);
                headDir = Vector3.Normalize(new Vector3(headDir.X * scale, headDir.Y * scale, -1f));
            }
            else headDir = new Vector3(0, 0, -1);
        }

        // ── Pinhead geometry (PrusaSlicer: pin sphere + cone + back sphere) ──
        float penetration = preset.ContactDepthMm; // how deep pin penetrates model
        // Head width = distance between sphere centers along head direction
        float headWidth = preset.NeckLengthMm + preset.UpperTaperLengthMm;
        // Full pinhead length along headDir
        float pinheadLen = rPin + headWidth + rBack;

        // Positions along the head direction
        var pinCenter = contact + headDir * (rPin - penetration); // pin sphere center (slightly inside model)
        var backCenter = contact + headDir * (pinheadLen - rBack - penetration); // back sphere center
        var junctionPt = contact + headDir * (pinheadLen - penetration); // where pillar starts

        // Ensure junction doesn't go below shaft bottom
        float lowerTaperStart = baseZ + preset.BaseHeightMm + preset.LowerTaperLengthMm;
        float shaftBot = Math.Max(lowerTaperStart, baseZ + 0.5f);
        if (junctionPt.Z < shaftBot + 1.0f)
        {
            // Scale down the head to fit
            float availLen = contact.Z - shaftBot - 1.0f;
            if (availLen > 1.0f)
            {
                float sc = availLen / pinheadLen;
                pinCenter = contact + headDir * (rPin - penetration) * sc;
                backCenter = contact + headDir * (pinheadLen - rBack - penetration) * sc;
                junctionPt = contact + headDir * availLen;
            }
            else
            {
                // Very short support — just go vertical
                junctionPt = new Vector3(contact.X, contact.Y, Math.Max(contact.Z - 2.0f, shaftBot + 0.5f));
                pinCenter = contact + (junctionPt - contact) * 0.2f;
                backCenter = contact + (junctionPt - contact) * 0.7f;
            }
        }

        // Shaft is vertical below the junction point
        float shaftX = junctionPt.X;
        float shaftY = junctionPt.Y;

        // ── 1. PIN SPHERE → model contact (tip segment along headDir) ─────────
        // Approximate the pin sphere as a cone: point at model surface, widens to rPin
        segments.Add(new SupportSegment { Part = "tip",
            X1 = contact.X, Y1 = contact.Y, Z1 = contact.Z, R1 = 0,
            X2 = pinCenter.X, Y2 = pinCenter.Y, Z2 = pinCenter.Z, R2 = rPin });

        // ── 2. CONNECTING CONE (neck) — pin sphere to back sphere along headDir ──
        // This is the tangent cone that smoothly connects both spheres
        segments.Add(new SupportSegment { Part = "neck",
            X1 = pinCenter.X, Y1 = pinCenter.Y, Z1 = pinCenter.Z, R1 = rPin,
            X2 = backCenter.X, Y2 = backCenter.Y, Z2 = backCenter.Z, R2 = rBack });

        // ── 3. BACK SPHERE → junction (upperTaper: transitions to vertical) ──
        segments.Add(new SupportSegment { Part = "upperTaper",
            X1 = backCenter.X, Y1 = backCenter.Y, Z1 = backCenter.Z, R1 = rBack,
            X2 = junctionPt.X, Y2 = junctionPt.Y, Z2 = junctionPt.Z, R2 = shaftR });

        // ── 4. SHAFT — vertical pillar from junction down to base zone ──────
        float shaftTop = junctionPt.Z;
        float shaftLen = shaftTop - shaftBot;
        if (shaftLen > 0.1f)
        {
            switch (preset.ShaftType)
            {
                case "cone":
                    segments.Add(new SupportSegment { Part = "shaft",
                        X1 = shaftX, Y1 = shaftY, Z1 = shaftTop, R1 = shaftR * 0.7f,
                        X2 = shaftX, Y2 = shaftY, Z2 = shaftBot, R2 = shaftR * 1.3f });
                    break;
                case "hollow":
                    segments.Add(new SupportSegment { Part = "shaft",
                        X1 = shaftX, Y1 = shaftY, Z1 = shaftTop, R1 = shaftR,
                        X2 = shaftX, Y2 = shaftY, Z2 = shaftBot, R2 = shaftR });
                    segments.Add(new SupportSegment { Part = "shaftInner",
                        X1 = shaftX, Y1 = shaftY, Z1 = shaftTop, R1 = shaftR * 0.6f,
                        X2 = shaftX, Y2 = shaftY, Z2 = shaftBot, R2 = shaftR * 0.6f });
                    break;
                case "lattice":
                case "diamond":
                    float latticeStep = Math.Min(2.0f, shaftLen / 3);
                    float cz = shaftTop;
                    bool wide = true;
                    while (cz > shaftBot + latticeStep * 0.5f)
                    {
                        float nz = Math.Max(cz - latticeStep, shaftBot);
                        float lr1 = wide ? shaftR : shaftR * 0.4f;
                        float lr2 = wide ? shaftR * 0.4f : shaftR;
                        segments.Add(new SupportSegment { Part = "shaft",
                            X1 = shaftX, Y1 = shaftY, Z1 = cz, R1 = lr1,
                            X2 = shaftX, Y2 = shaftY, Z2 = nz, R2 = lr2 });
                        cz = nz;
                        wide = !wide;
                    }
                    break;
                case "spiral":
                    float spiralOff = shaftR * 0.3f;
                    int spiralN = Math.Max(2, (int)(shaftLen / 3f));
                    float sdz = shaftLen / spiralN;
                    for (int si = 0; si < spiralN; si++)
                    {
                        float sz1 = shaftTop - si * sdz;
                        float sz2 = shaftTop - (si + 1) * sdz;
                        float a1 = si * MathF.PI * 0.5f;
                        float a2 = (si + 1) * MathF.PI * 0.5f;
                        segments.Add(new SupportSegment { Part = "shaft",
                            X1 = shaftX + MathF.Cos(a1) * spiralOff, Y1 = shaftY + MathF.Sin(a1) * spiralOff, Z1 = sz1, R1 = shaftR * 0.8f,
                            X2 = shaftX + MathF.Cos(a2) * spiralOff, Y2 = shaftY + MathF.Sin(a2) * spiralOff, Z2 = sz2, R2 = shaftR * 0.8f });
                    }
                    break;
                case "ribbed":
                    float ribStep = Math.Min(1.5f, shaftLen / 3);
                    float rz = shaftTop;
                    bool thick = true;
                    while (rz > shaftBot + ribStep * 0.5f)
                    {
                        float rnz = Math.Max(rz - ribStep, shaftBot);
                        float rr = thick ? shaftR : shaftR * 0.65f;
                        segments.Add(new SupportSegment { Part = "shaft",
                            X1 = shaftX, Y1 = shaftY, Z1 = rz, R1 = rr,
                            X2 = shaftX, Y2 = shaftY, Z2 = rnz, R2 = rr });
                        rz = rnz;
                        thick = !thick;
                    }
                    break;
                default: // "cylinder", "square", "xprofile", "ibeam"
                    segments.Add(new SupportSegment { Part = "shaft",
                        X1 = shaftX, Y1 = shaftY, Z1 = shaftTop, R1 = shaftR,
                        X2 = shaftX, Y2 = shaftY, Z2 = shaftBot, R2 = shaftR });
                    break;
            }
        }

        // ── 5. Lower taper (shaft → base) ────────────────────────────────────
        float lowerBot = baseZ + preset.BaseHeightMm;
        segments.Add(new SupportSegment
        {
            Part = "lowerTaper", X1 = shaftX, Y1 = shaftY, Z1 = shaftBot, R1 = shaftR,
            X2 = shaftX, Y2 = shaftY, Z2 = lowerBot, R2 = baseR,
        });

        // ── 6. BASE — type-dependent, always on build plate ──────────────────
        switch (preset.BaseType)
        {
            case "cone":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 0.3f });
                break;
            case "pyramid":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR * 1.1f,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 0.2f });
                break;
            case "raft":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR * 1.5f,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 1.5f });
                break;
            case "miniraft":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR * 1.2f,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 1.2f });
                break;
            case "pin":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR * 0.5f,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 0.5f });
                break;
            case "skirted":
                float skZ = baseZ + preset.BaseHeightMm * 0.3f;
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR,
                    X2 = shaftX, Y2 = shaftY, Z2 = skZ, R2 = baseR });
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = skZ, R1 = baseR,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 1.6f });
                break;
            case "webbed":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR });
                float wO = baseR * 1.2f, wR = baseR * 0.2f;
                float wMidZ = (lowerBot + baseZ) / 2;
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = wMidZ, R1 = wR,
                    X2 = shaftX + wO, Y2 = shaftY, Z2 = baseZ, R2 = wR });
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = wMidZ, R1 = wR,
                    X2 = shaftX - wO, Y2 = shaftY, Z2 = baseZ, R2 = wR });
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = wMidZ, R1 = wR,
                    X2 = shaftX, Y2 = shaftY + wO, Z2 = baseZ, R2 = wR });
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = wMidZ, R1 = wR,
                    X2 = shaftX, Y2 = shaftY - wO, Z2 = baseZ, R2 = wR });
                break;
            case "anchor":
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR * 1.4f });
                break;
            default: // "disc"
                segments.Add(new SupportSegment { Part = "base", X1 = shaftX, Y1 = shaftY, Z1 = lowerBot, R1 = baseR,
                    X2 = shaftX, Y2 = shaftY, Z2 = baseZ, R2 = baseR });
                break;
        }

        return new AdvancedSupport
        {
            Id = id, Type = type, Preset = preset,
            ContactX = contact.X, ContactY = contact.Y, ContactZ = contact.Z,
            NormalX = normal.X, NormalY = normal.Y, NormalZ = normal.Z,
            BaseX = shaftX, BaseY = shaftY, BaseZ = baseZ,
            Segments = segments,
        };
    }

    // ── Shaft collision check ──────────────────────────────────────────────

    /// <summary>
    /// Check if a support's vertical shaft passes through the mesh interior.
    /// Uses ray casting: odd number of intersections above a point = inside mesh.
    /// </summary>
    private static bool ShaftCollidesWithMesh(StlMesh mesh, AdvancedSupport support)
    {
        float shaftTop = support.ContactZ - 3.0f; // skip pinhead area
        float shaftBot = support.BaseZ + 1.0f;
        if (shaftTop <= shaftBot) return false;

        float sx = support.BaseX, sy = support.BaseY;
        int sampleCount = Math.Min(8, Math.Max(2, (int)((shaftTop - shaftBot) / 15)));

        for (int si = 0; si <= sampleCount; si++)
        {
            float sampleZ = shaftTop - (shaftTop - shaftBot) * si / Math.Max(1, sampleCount);
            int intersections = 0;

            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var v0 = mesh.Vertices[t * 3];
                var v1 = mesh.Vertices[t * 3 + 1];
                var v2 = mesh.Vertices[t * 3 + 2];

                float triMinZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
                float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));
                if (sampleZ < triMinZ || sampleZ > triMaxZ) continue;

                float denom = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
                if (MathF.Abs(denom) < 1e-8f) continue;
                float u = ((v1.Y - v2.Y) * (sx - v2.X) + (v2.X - v1.X) * (sy - v2.Y)) / denom;
                if (u < -0.01f || u > 1.01f) continue;
                float v = ((v2.Y - v0.Y) * (sx - v2.X) + (v0.X - v2.X) * (sy - v2.Y)) / denom;
                if (v < -0.01f || u + v > 1.01f) continue;

                float hitZ = v0.Z * u + v1.Z * v + v2.Z * (1 - u - v);
                if (hitZ > sampleZ) intersections++;
            }

            if (intersections % 2 == 1) return true; // inside mesh
        }

        return false;
    }

    // ── Intermediate surface finder ──────────────────────────────────────────────

    /// <summary>
    /// Cast a ray straight down from the overhang point and find the first surface
    /// below it (but above the build plate). Returns 0 if no intermediate surface.
    /// </summary>
    private static float FindIntermediateSurface(StlMesh mesh, Vector3 point, float startZ)
    {
        float bestZ = 0; // default: build plate
        float minGap = 2.0f; // need at least 2mm clearance for a useful support

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

            // Quick Z bounds check
            float triMinZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
            float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));
            if (triMaxZ >= startZ - minGap || triMinZ < bestZ) continue;

            // Check if ray (point.X, point.Y, going down) intersects this triangle
            // Using barycentric coordinate test on the XY projection
            float denom = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
            if (MathF.Abs(denom) < 1e-8f) continue;

            float u = ((v1.Y - v2.Y) * (point.X - v2.X) + (v2.X - v1.X) * (point.Y - v2.Y)) / denom;
            if (u < 0 || u > 1) continue;
            float v = ((v2.Y - v0.Y) * (point.X - v2.X) + (v0.X - v2.X) * (point.Y - v2.Y)) / denom;
            if (v < 0 || u + v > 1) continue;

            float hitZ = v0.Z * u + v1.Z * v + v2.Z * (1 - u - v);
            if (hitZ > bestZ && hitZ < startZ - minGap)
            {
                // Verify the face is upward-facing (can support from the top)
                var cross = Vector3.Cross(v1 - v0, v2 - v0);
                var normal = Vector3.Normalize(cross);
                if (!float.IsNaN(normal.Z) && normal.Z > 0.3f) // face is reasonably upward
                    bestZ = hitZ;
            }
        }

        return bestZ;
    }

    // ── Tree merging ────────────────────────────────────────────────────────

    private static List<AdvancedSupport> MergeIntoTrees(List<AdvancedSupport> supports, SupportPreset preset)
    {
        // Multi-level tree merging using hierarchical clustering
        // Level 1: small groups within 5mm → sub-branches merge into branches
        // Level 2: branches within 10mm → merge into main trunks
        float level1Radius = 5.0f;
        float level2Radius = 10.0f;
        var result = new List<AdvancedSupport>();
        var used = new bool[supports.Count];
        int trunkCounter = 0;

        // Sort by Z ascending so lower supports are processed first for better trunk placement
        var sortedIndices = Enumerable.Range(0, supports.Count)
            .OrderBy(i => supports[i].ContactZ).ToList();

        // Level 1: form small groups (2-4 supports)
        var level1Groups = new List<List<int>>();
        foreach (int i in sortedIndices)
        {
            if (used[i]) continue;
            var group = new List<int> { i };
            foreach (int j in sortedIndices)
            {
                if (j == i || used[j]) continue;
                float dist = Vector2.Distance(
                    new Vector2(supports[i].ContactX, supports[i].ContactY),
                    new Vector2(supports[j].ContactX, supports[j].ContactY));
                if (dist < level1Radius && group.Count < 4)
                    group.Add(j);
            }
            foreach (int g in group) used[g] = true;
            level1Groups.Add(group);
        }

        // Level 2: merge nearby level-1 groups into larger trees
        var groupUsed = new bool[level1Groups.Count];
        var trees = new List<List<List<int>>>(); // tree → list of level1 groups

        for (int gi = 0; gi < level1Groups.Count; gi++)
        {
            if (groupUsed[gi]) continue;
            var tree = new List<List<int>> { level1Groups[gi] };
            float gcx = level1Groups[gi].Average(i => supports[i].ContactX);
            float gcy = level1Groups[gi].Average(i => supports[i].ContactY);
            groupUsed[gi] = true;

            for (int gj = gi + 1; gj < level1Groups.Count; gj++)
            {
                if (groupUsed[gj]) continue;
                float gcx2 = level1Groups[gj].Average(i => supports[i].ContactX);
                float gcy2 = level1Groups[gj].Average(i => supports[i].ContactY);
                if (Vector2.Distance(new Vector2(gcx, gcy), new Vector2(gcx2, gcy2)) < level2Radius && tree.Count < 4)
                {
                    tree.Add(level1Groups[gj]);
                    groupUsed[gj] = true;
                }
            }
            trees.Add(tree);
        }

        // Build geometry for each tree
        foreach (var tree in trees)
        {
            var allIndices = tree.SelectMany(g => g).ToList();

            if (allIndices.Count == 1)
            {
                // Standalone — no merge needed
                result.Add(supports[allIndices[0]]);
                continue;
            }

            string trunkId = $"trunk-{++trunkCounter}";
            float trunkCx = allIndices.Average(i => supports[i].ContactX);
            float trunkCy = allIndices.Average(i => supports[i].ContactY);
            float minContactZ = allIndices.Min(i => supports[i].ContactZ);

            // Weighted merge Z: higher for taller supports, minimum 30% of lowest contact
            float trunkMergeZ = Math.Max(minContactZ * 0.3f, 2.0f);

            if (tree.Count == 1)
            {
                // Single level-1 group → simple 2-level tree
                EmitBranches(result, supports, tree[0], preset, trunkId, trunkCx, trunkCy, trunkMergeZ);
            }
            else
            {
                // Multi-group tree: each group gets a sub-trunk, all sub-trunks merge to main trunk
                float subTrunkZ = trunkMergeZ + (minContactZ - trunkMergeZ) * 0.4f;
                int subCounter = 0;

                foreach (var group in tree)
                {
                    float subCx = group.Average(i => supports[i].ContactX);
                    float subCy = group.Average(i => supports[i].ContactY);
                    string subTrunkId = $"{trunkId}-sub-{++subCounter}";

                    if (group.Count == 1)
                    {
                        // Single support → branch directly to main trunk
                        var s = supports[group[0]];
                        var segs = new List<SupportSegment>();
                        BuildTipSegments(segs, s, preset);
                        segs.Add(new SupportSegment { Part = "branch",
                            X1 = s.ContactX, Y1 = s.ContactY, Z1 = s.ContactZ - preset.ContactDepthMm, R1 = preset.NeckDiameterMm / 2,
                            X2 = trunkCx, Y2 = trunkCy, Z2 = trunkMergeZ, R2 = preset.ShaftDiameterMm * 0.5f });
                        result.Add(s with { Type = "tree", Segments = segs,
                            MergeZ = trunkMergeZ, MergeX = trunkCx, MergeY = trunkCy, ParentTrunkId = trunkId });
                    }
                    else
                    {
                        // Multiple supports → form sub-branches to sub-trunk, then sub-trunk to main trunk
                        EmitBranches(result, supports, group, preset, subTrunkId, subCx, subCy, subTrunkZ);

                        // Sub-trunk connects to main trunk
                        float subShaftR = preset.ShaftDiameterMm * 0.5f;
                        var subSegs = new List<SupportSegment>
                        {
                            new() { Part = "branch", X1 = subCx, Y1 = subCy, Z1 = subTrunkZ, R1 = subShaftR,
                                    X2 = trunkCx, Y2 = trunkCy, Z2 = trunkMergeZ, R2 = preset.ShaftDiameterMm * 0.6f },
                        };
                        result.Add(new AdvancedSupport {
                            Id = subTrunkId, Type = "tree-subtruck", Preset = preset,
                            ContactX = subCx, ContactY = subCy, ContactZ = subTrunkZ,
                            NormalX = 0, NormalY = 0, NormalZ = -1,
                            BaseX = trunkCx, BaseY = trunkCy, BaseZ = trunkMergeZ,
                            MergeZ = trunkMergeZ, MergeX = trunkCx, MergeY = trunkCy, ParentTrunkId = trunkId,
                            Segments = subSegs,
                        });
                    }
                }
            }

            // Main trunk: merge point → base
            float trunkR = preset.ShaftDiameterMm * 0.7f;
            // Trunk gets thicker with more branches
            trunkR *= 1f + allIndices.Count * 0.05f;
            float trunkLowerStart = preset.BaseHeightMm + preset.LowerTaperLengthMm;
            var trunkSegments = new List<SupportSegment>
            {
                new() { Part = "shaft", X1 = trunkCx, Y1 = trunkCy, Z1 = trunkMergeZ, R1 = trunkR,
                         X2 = trunkCx, Y2 = trunkCy, Z2 = trunkLowerStart, R2 = trunkR },
                new() { Part = "lowerTaper", X1 = trunkCx, Y1 = trunkCy, Z1 = trunkLowerStart, R1 = trunkR,
                         X2 = trunkCx, Y2 = trunkCy, Z2 = preset.BaseHeightMm, R2 = preset.BaseDiameterMm / 2 },
                new() { Part = "base", X1 = trunkCx, Y1 = trunkCy, Z1 = preset.BaseHeightMm, R1 = preset.BaseDiameterMm / 2,
                         X2 = trunkCx, Y2 = trunkCy, Z2 = 0, R2 = preset.BaseDiameterMm / 2 },
            };

            result.Add(new AdvancedSupport {
                Id = trunkId, Type = "tree-trunk", Preset = preset,
                ContactX = trunkCx, ContactY = trunkCy, ContactZ = trunkMergeZ,
                NormalX = 0, NormalY = 0, NormalZ = -1,
                BaseX = trunkCx, BaseY = trunkCy, BaseZ = 0,
                Segments = trunkSegments,
            });
        }

        return result;
    }

    private static void BuildTipSegments(List<SupportSegment> segs, AdvancedSupport s, SupportPreset preset)
    {
        float tipR = preset.TipDiameterMm / 2;
        segs.Add(new SupportSegment { Part = "tip",
            X1 = s.ContactX, Y1 = s.ContactY, Z1 = s.ContactZ, R1 = 0,
            X2 = s.ContactX, Y2 = s.ContactY, Z2 = s.ContactZ - preset.ContactDepthMm, R2 = tipR });
    }

    private static void EmitBranches(List<AdvancedSupport> result, List<AdvancedSupport> supports,
        List<int> group, SupportPreset preset, string parentId, float mergeX, float mergeY, float mergeZ)
    {
        foreach (int gi in group)
        {
            var s = supports[gi];
            var segments = new List<SupportSegment>();
            BuildTipSegments(segments, s, preset);

            // Branch angled from contact down to merge point
            float branchTopZ = s.ContactZ - preset.ContactDepthMm;
            segments.Add(new SupportSegment { Part = "branch",
                X1 = s.ContactX, Y1 = s.ContactY, Z1 = branchTopZ, R1 = preset.NeckDiameterMm / 2,
                X2 = mergeX, Y2 = mergeY, Z2 = mergeZ, R2 = preset.ShaftDiameterMm * 0.45f });

            result.Add(s with {
                Type = "tree", Segments = segments,
                MergeZ = mergeZ, MergeX = mergeX, MergeY = mergeY, ParentTrunkId = parentId,
            });
        }
    }

    // ── Cross-bracing ───────────────────────────────────────────────────────

    private static List<CrossBrace> GenerateCrossBraces(
        List<AdvancedSupport> supports, SupportPreset preset, float maxDist)
    {
        var braces = new List<CrossBrace>();
        float braceInterval = preset.BraceIntervalMm;

        // Filter to only column supports (not tree branches/trunks)
        var columns = supports.Where(s =>
            s.Type != "tree" && s.Type != "tree-trunk" && s.Type != "tree-subtruck").ToList();

        // Use shaft positions (BaseX/Y) not contact positions for proper connection
        for (int i = 0; i < columns.Count; i++)
        {
            var si = columns[i];
            // Find nearest neighbors by SHAFT position
            var neighbors = new List<(int idx, float dist)>();
            for (int j = i + 1; j < columns.Count; j++) // j > i to avoid duplicates
            {
                var sj = columns[j];
                float dist = Vector2.Distance(
                    new Vector2(si.BaseX, si.BaseY),
                    new Vector2(sj.BaseX, sj.BaseY));
                if (dist > 0.5f && dist <= maxDist) // min 0.5mm apart
                    neighbors.Add((j, dist));
            }
            neighbors.Sort((a, b) => a.dist.CompareTo(b.dist));

            // Connect to nearest 3 neighbors
            foreach (var (j, xyDist) in neighbors.Take(3))
            {
                var sj = columns[j];
                // Shaft Z range where both supports overlap
                float shaftTopI = si.ContactZ - 3f; // below pinhead
                float shaftTopJ = sj.ContactZ - 3f;
                float minZ = Math.Max(si.BaseZ, sj.BaseZ) + 2;
                float maxZ = Math.Min(shaftTopI, shaftTopJ) - 1;
                if (maxZ <= minZ + braceInterval) continue;

                // Generate alternating pattern: horizontal bridge + diagonal cross
                int maxBracesPerPair = Math.Min(6, (int)((maxZ - minZ) / braceInterval));
                int braceCount = 0;
                bool cross = false;

                for (float z = minZ + braceInterval; z < maxZ && braceCount < maxBracesPerPair; z += braceInterval)
                {
                    if (cross)
                    {
                        // Diagonal cross-brace (zigzag)
                        float z2 = Math.Min(z + braceInterval * 0.5f, maxZ);
                        braces.Add(new CrossBrace
                        {
                            SupportA = si.Id, SupportB = sj.Id,
                            X1 = si.BaseX, Y1 = si.BaseY, Z1 = z,
                            X2 = sj.BaseX, Y2 = sj.BaseY, Z2 = z2,
                            Diameter = preset.BraceDiameterMm,
                        });
                    }
                    else
                    {
                        // Horizontal bridge (same Z on both sides)
                        braces.Add(new CrossBrace
                        {
                            SupportA = si.Id, SupportB = sj.Id,
                            X1 = si.BaseX, Y1 = si.BaseY, Z1 = z,
                            X2 = sj.BaseX, Y2 = sj.BaseY, Z2 = z,
                            Diameter = preset.BraceDiameterMm * 1.2f, // slightly thicker horizontal
                        });
                    }
                    cross = !cross;
                    braceCount++;
                }
            }
        }

        return braces;
    }

    // ── Support Validation ──────────────────────────────────────────────────

    public sealed record ValidationIssue
    {
        public required string SupportId { get; init; }
        public required string Category { get; init; } // "tip-detached" | "overhang-unsupported" | "collision"
        public required string Description { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
    }

    public sealed record ValidationResult
    {
        public int TotalSupports { get; init; }
        public int TotalOverhangs { get; init; }
        public int TipsTouchingModel { get; init; }
        public int TipsDetached { get; init; }
        public int OverhangsSupported { get; init; }
        public int OverhangsUnsupported { get; init; }
        public int CollisionsDetected { get; init; }
        public List<ValidationIssue> Issues { get; init; } = [];
        public long ElapsedMs { get; init; }
    }

    /// <summary>
    /// Validate generated supports against the mesh:
    /// 1. Each support tip must touch the model surface (within tolerance)
    /// 2. All overhang faces must be covered by at least one support
    /// 3. No support shaft should pass through the mesh interior
    /// </summary>
    public static ValidationResult Validate(StlMesh mesh, AdvancedSupportResult result, AdvancedSupportConfig config)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var issues = new List<ValidationIssue>();

        // Center mesh the same way Generate() does
        float meshW = mesh.Max.X - mesh.Min.X;
        float meshD = mesh.Max.Y - mesh.Min.Y;
        float offX = -(mesh.Min.X + meshW / 2);
        float offY = -(mesh.Min.Y + meshD / 2);
        float offZ = -mesh.Min.Z;
        mesh = mesh.Transform(new Vector3(offX, offY, offZ), 1.0f);

        // ── 1. Tip contact validation ────────────────────────────────────────
        // For each support, check if the contact point is within tolerance of any triangle
        float tipTolerance = 2.0f; // mm — allow some slack for pinhead geometry
        int tipsTouching = 0, tipsDetached = 0;

        var nonTrunkSupports = result.Supports
            .Where(s => s.Type != "tree-trunk" && s.Type != "tree-subtruck").ToList();

        foreach (var sup in nonTrunkSupports)
        {
            float minDist = float.MaxValue;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var v0 = mesh.Vertices[t * 3];
                var v1 = mesh.Vertices[t * 3 + 1];
                var v2 = mesh.Vertices[t * 3 + 2];
                var center = (v0 + v1 + v2) / 3f;
                float dist = Vector3.Distance(new Vector3(sup.ContactX, sup.ContactY, sup.ContactZ), center);
                if (dist < minDist) minDist = dist;
                if (dist < tipTolerance) break; // close enough, early exit
            }

            if (minDist <= tipTolerance)
                tipsTouching++;
            else
            {
                tipsDetached++;
                issues.Add(new ValidationIssue
                {
                    SupportId = sup.Id, Category = "tip-detached",
                    Description = $"Tip is {minDist:F1}mm from nearest triangle (tolerance={tipTolerance}mm)",
                    X = sup.ContactX, Y = sup.ContactY, Z = sup.ContactZ,
                });
            }
        }

        // ── 2. Overhang coverage validation ──────────────────────────────────
        // Re-detect overhangs and check each is within range of at least one support
        var overhangCos = MathF.Cos(MathF.PI / 180f * (float)config.OverhangAngleDeg);
        var gravityDir = new Vector3(0, 0, -1);
        var overhangFaces = new List<(Vector3 center, float area)>();

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            var area = cross.Length() * 0.5f;
            var normal = Vector3.Normalize(cross);
            if (float.IsNaN(normal.X) || area < 1e-8f) continue;

            float dot = Vector3.Dot(normal, gravityDir);
            if (dot > overhangCos)
            {
                var center = (v0 + v1 + v2) / 3f;
                if (center.Z < 0.2f) continue;
                overhangFaces.Add((center, area));
            }
        }

        // Each overhang face should have a support within coverage radius
        float baseSpacing = (float)(4.0 / (config.DensityFactor + 0.1));
        float coverageRadius = baseSpacing * 1.5f; // generous coverage check
        int overhangsSupported = 0, overhangsUnsupported = 0;
        var unsupportedSamples = new List<(Vector3 center, float area)>();

        foreach (var (center, area) in overhangFaces)
        {
            bool covered = nonTrunkSupports.Any(s =>
                Vector2.Distance(new Vector2(s.ContactX, s.ContactY), new Vector2(center.X, center.Y)) < coverageRadius);

            if (covered)
                overhangsSupported++;
            else
            {
                overhangsUnsupported++;
                // Only report a sample of unsupported overhangs (not every tiny face)
                if (unsupportedSamples.Count < 20 && area > 0.5f)
                    unsupportedSamples.Add((center, area));
            }
        }

        foreach (var (center, area) in unsupportedSamples)
        {
            issues.Add(new ValidationIssue
            {
                SupportId = "-", Category = "overhang-unsupported",
                Description = $"Overhang face (area={area:F1}mm²) has no support within {coverageRadius:F1}mm",
                X = center.X, Y = center.Y, Z = center.Z,
            });
        }

        // ── 3. Collision detection ───────────────────────────────────────────
        // For each support shaft, cast a ray downward and count mesh intersections.
        // Odd count = inside mesh = collision.
        int collisions = 0;
        foreach (var sup in nonTrunkSupports)
        {
            // Sample points along the shaft (skip tip area near contact)
            float shaftTop = sup.ContactZ - 3.0f; // skip the pinhead area
            float shaftBot = sup.BaseZ + 1.0f;
            if (shaftTop <= shaftBot) continue;

            // Check a few sample points along the shaft
            int samples = Math.Min(5, (int)((shaftTop - shaftBot) / 10));
            bool hasCollision = false;

            for (int si = 0; si <= samples && !hasCollision; si++)
            {
                float sampleZ = shaftTop - (shaftTop - shaftBot) * si / Math.Max(1, samples);
                // Count how many triangles are above this point (ray cast upward)
                int intersections = 0;
                float sx = sup.BaseX, sy = sup.BaseY; // shaft XY position

                for (int t = 0; t < mesh.TriangleCount; t++)
                {
                    var v0 = mesh.Vertices[t * 3];
                    var v1 = mesh.Vertices[t * 3 + 1];
                    var v2 = mesh.Vertices[t * 3 + 2];

                    // Quick Z bounds check
                    float triMinZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
                    float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));
                    if (sampleZ < triMinZ || sampleZ > triMaxZ) continue;

                    // Point-in-triangle test (XY projection at sampleZ)
                    float denom = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
                    if (MathF.Abs(denom) < 1e-8f) continue;
                    float u = ((v1.Y - v2.Y) * (sx - v2.X) + (v2.X - v1.X) * (sy - v2.Y)) / denom;
                    if (u < -0.01f || u > 1.01f) continue;
                    float v = ((v2.Y - v0.Y) * (sx - v2.X) + (v0.X - v2.X) * (sy - v2.Y)) / denom;
                    if (v < -0.01f || u + v > 1.01f) continue;

                    float hitZ = v0.Z * u + v1.Z * v + v2.Z * (1 - u - v);
                    if (hitZ > sampleZ) intersections++;
                }

                // Odd intersections above = inside mesh
                if (intersections % 2 == 1)
                    hasCollision = true;
            }

            if (hasCollision)
            {
                collisions++;
                issues.Add(new ValidationIssue
                {
                    SupportId = sup.Id, Category = "collision",
                    Description = "Support shaft passes through mesh interior",
                    X = sup.BaseX, Y = sup.BaseY, Z = (sup.ContactZ + sup.BaseZ) / 2,
                });
            }
        }

        sw.Stop();
        return new ValidationResult
        {
            TotalSupports = nonTrunkSupports.Count,
            TotalOverhangs = overhangFaces.Count,
            TipsTouchingModel = tipsTouching,
            TipsDetached = tipsDetached,
            OverhangsSupported = overhangsSupported,
            OverhangsUnsupported = overhangsUnsupported,
            CollisionsDetected = collisions,
            Issues = issues,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
