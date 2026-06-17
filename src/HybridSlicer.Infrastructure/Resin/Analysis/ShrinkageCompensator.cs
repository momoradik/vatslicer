using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Pre-scales a mesh to compensate for resin curing shrinkage.
/// Applies a uniform or per-axis scale factor based on the resin's
/// known shrinkage percentage. For ceramics, also supports anisotropic
/// shrinkage from sintering.
/// </summary>
public static class ShrinkageCompensator
{
    public sealed record CompensationConfig
    {
        /// <summary>Volumetric shrinkage (%). Linear shrinkage ≈ shrinkage/3.</summary>
        public float ShrinkagePct { get; init; } = 2f;
        /// <summary>Per-axis shrinkage override. Null = use uniform from ShrinkagePct.</summary>
        public float? ShrinkageXPct { get; init; }
        public float? ShrinkageYPct { get; init; }
        public float? ShrinkageZPct { get; init; }
    }

    /// <summary>
    /// Compute the scale factors needed to compensate for shrinkage.
    /// The mesh should be scaled UP by these factors before slicing.
    /// </summary>
    public static (float scaleX, float scaleY, float scaleZ) ComputeScaleFactors(CompensationConfig config)
    {
        float uniformLinear = 1f + config.ShrinkagePct / 300f;
        float sx = config.ShrinkageXPct.HasValue ? 1f + config.ShrinkageXPct.Value / 100f : uniformLinear;
        float sy = config.ShrinkageYPct.HasValue ? 1f + config.ShrinkageYPct.Value / 100f : uniformLinear;
        float sz = config.ShrinkageZPct.HasValue ? 1f + config.ShrinkageZPct.Value / 100f : uniformLinear;
        return (sx, sy, sz);
    }

    /// <summary>
    /// Apply shrinkage compensation to a mesh (returns a new scaled mesh).
    /// </summary>
    /// <summary>
    /// Apply uniform shrinkage compensation via Transform (scale only).
    /// For anisotropic compensation, use the scale factors with a custom mesh builder.
    /// </summary>
    public static StlMesh CompensateUniform(StlMesh mesh, float shrinkagePct)
    {
        float scale = 1f + shrinkagePct / 300f;
        return mesh.Transform(Vector3.Zero, scale);
    }
}
