using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Physics-driven per-support sizing function.
///
/// Sits between routing and meshing. Every support gets its tip, pillar, and base
/// radii computed from the peel force it actually carries and its height — instead
/// of fixed constants from the config.
///
/// During peel (bottom-up), the support is in TENSION — the FEP film pulls the
/// cured layer away from the LCD. The force each support carries is:
///   F = (P_adh * A_layer) / n_supports
///
/// The tip must not tear off in tension:
///   r_tip = sqrt(F * SF / (π * σ_bond))
///
/// The pillar must not break or whip under lateral drag:
///   r_pillar = max(tensile_floor, stiffness_for_height)
///
/// The base flares for bed adhesion:
///   r_base = r_pillar * BASE_FLARE
/// </summary>
public static class SupportSizer
{
    // ── Calibration constants ─────────────────────────────────────────

    /// <summary>Safety factor applied to all force-based sizing.</summary>
    public const float SF = 2.0f;

    /// <summary>Minimum tip radius (mm). Floor for visibility: 0.25mm = 0.5mm diameter.</summary>
    public const float R_TIP_MIN = 0.25f;

    /// <summary>Minimum pillar radius (mm).</summary>
    public const float R_PILLAR_MIN = 0.3f;

    /// <summary>Base flare multiplier relative to pillar radius.</summary>
    public const float BASE_FLARE = 2.7f;

    /// <summary>Contact sphere radius = tip radius * this factor.</summary>
    public const float CONTACT_SPHERE_SCALE = 1.6f;

    /// <summary>Contact penetration depth into the model surface (mm).</summary>
    public const float CONTACT_DEPTH = 0.3f;

    /// <summary>Pillar stiffness coefficient: radius grows by this * height to resist sway.</summary>
    public const float K_STIFF = 0.004f;

    /// <summary>
    /// Adhesion pressure of FEP/nFEP film interface (N/mm²).
    /// This is the ONE number that must be calibrated empirically per resin+film combo.
    /// Default: 0.015 N/mm² ≈ middle of FEP range.
    /// Back-solve from ChiTuBox Medium preset on a known part to inherit their calibration.
    /// </summary>
    public const float P_ADH_DEFAULT = 0.015f;

    /// <summary>Green resin bond strength at tip (MPa). Lower than bulk because the
    /// tip contacts partially-cured resin at the layer boundary.</summary>
    public const float SIGMA_BOND = 15f; // MPa — green bond, not cured tensile

    /// <summary>Bulk tensile strength of cured resin (MPa).</summary>
    public const float SIGMA_RESIN = 40f; // MPa — standard resin

    // ── Manual overrides (from Advanced Settings panel) ─────────────

    /// <summary>
    /// Optional per-dimension manual overrides from the UI's Advanced Settings panel.
    /// Any non-null value wins over the physics-computed dimension.
    /// </summary>
    public sealed class ManualOverrides
    {
        public float? TipRadiusMm { get; init; }
        public float? ContactSphereRadiusMm { get; init; }
        public float? ContactDepthMm { get; init; }
        public float? PillarRadiusMm { get; init; }
        public float? BaseRadiusMm { get; init; }
        public float? BaseHeightMm { get; init; }

        public bool IsEmpty =>
            !TipRadiusMm.HasValue && !ContactSphereRadiusMm.HasValue && !ContactDepthMm.HasValue &&
            !PillarRadiusMm.HasValue && !BaseRadiusMm.HasValue && !BaseHeightMm.HasValue;
    }

    // ── Result ────────────────────────────────────────────────────────

    public sealed class SupportSizing
    {
        /// <summary>Tip radius where support contacts the model (mm).</summary>
        public required float TipRadius { get; init; }
        /// <summary>Contact sphere radius — the visible bead at the touch point (mm).</summary>
        public required float ContactSphereRadius { get; init; }
        /// <summary>How deep the contact sphere penetrates the model surface (mm).</summary>
        public required float ContactDepth { get; init; }
        /// <summary>Pillar shaft radius (mm).</summary>
        public required float PillarRadius { get; init; }
        /// <summary>Base pedestal radius (mm). 0 if support doesn't reach the plate.</summary>
        public required float BaseRadius { get; init; }
        /// <summary>Base boss/cone height (mm).</summary>
        public required float BaseHeight { get; init; }
        /// <summary>Peel force this support carries (N).</summary>
        public required float Force { get; init; }
        /// <summary>Physics-recommended tip radius before any manual override (mm).</summary>
        public float RecommendedTipRadius { get; init; }
        /// <summary>Physics-recommended pillar radius before any manual override (mm).</summary>
        public float RecommendedPillarRadius { get; init; }
    }

    /// <summary>
    /// Compute physics-driven sizing for a single support.
    /// </summary>
    /// <param name="supportHeight">Height from contact to base (mm).</param>
    /// <param name="layerArea">Cross-sectional area of the layer this contact belongs to (mm²).</param>
    /// <param name="supportsInLayer">Number of supports sharing this layer's peel load.</param>
    /// <param name="rootsOnPlate">True if the support reaches the build plate.</param>
    /// <param name="pAdh">Adhesion pressure (N/mm²). Use P_ADH_DEFAULT if unknown.</param>
    /// <param name="sigmaBond">Green bond tensile strength (MPa).</param>
    /// <param name="sigmaResin">Cured resin tensile strength (MPa).</param>
    public static SupportSizing Size(
        float supportHeight,
        float layerArea,
        int supportsInLayer,
        bool rootsOnPlate,
        float pAdh = P_ADH_DEFAULT,
        float sigmaBond = SIGMA_BOND,
        float sigmaResin = SIGMA_RESIN,
        ManualOverrides? ov = null)
    {
        // ── 1. DEMAND: peel force per support ────────────────────────
        float F = (pAdh * layerArea) / MathF.Max(1, supportsInLayer);

        // ── 2. TIP: must not tear off in tension ─────────────────────
        float rTip = MathF.Sqrt((F * SF) / (MathF.PI * sigmaBond));
        rTip = MathF.Max(rTip, R_TIP_MIN);
        float rContactSphere = rTip * CONTACT_SPHERE_SCALE;

        // ── 3. PILLAR: tensile floor + height stiffness ──────────────
        float rPillarLoad = MathF.Sqrt((F * SF) / (MathF.PI * sigmaResin));
        float rPillarStiff = K_STIFF * supportHeight;
        float rPillar = MathF.Max(MathF.Max(rPillarLoad, rPillarStiff), R_PILLAR_MIN);

        // ── 4. BASE: flared pedestal for plate-rooted supports ───────
        float rBase = 0;
        float hBoss = 0;
        if (rootsOnPlate)
        {
            rBase = rPillar * BASE_FLARE;
            hBoss = rBase * 0.8f;
        }

        // Store physics recommendations before applying overrides
        float recommendedTip = rTip;
        float recommendedPillar = rPillar;

        // ── 5. Apply manual overrides ────────────────────────────────
        if (ov != null)
        {
            if (ov.TipRadiusMm.HasValue) rTip = ov.TipRadiusMm.Value;
            if (ov.ContactSphereRadiusMm.HasValue) rContactSphere = ov.ContactSphereRadiusMm.Value;
            if (ov.PillarRadiusMm.HasValue) rPillar = ov.PillarRadiusMm.Value;
            if (ov.BaseRadiusMm.HasValue) rBase = ov.BaseRadiusMm.Value;
            if (ov.BaseHeightMm.HasValue) hBoss = ov.BaseHeightMm.Value;
        }

        return new SupportSizing
        {
            TipRadius = rTip,
            ContactSphereRadius = ov?.ContactSphereRadiusMm ?? rContactSphere,
            ContactDepth = ov?.ContactDepthMm ?? CONTACT_DEPTH,
            PillarRadius = rPillar,
            BaseRadius = rBase,
            BaseHeight = hBoss,
            Force = F,
            RecommendedTipRadius = recommendedTip,
            RecommendedPillarRadius = recommendedPillar,
        };
    }
}
