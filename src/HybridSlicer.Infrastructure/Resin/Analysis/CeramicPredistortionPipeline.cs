using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Ceramic predistortion pipeline: morphs the nominal mesh by the inverse
/// of a measured deviation field to compensate for sintering shrinkage/warpage.
///
/// Algorithm (iterative fixed-point):
/// 1. Start with nominal mesh M0
/// 2. Apply deviation field D(x) → compute predistorted mesh M' = M0 + alpha * D(x)
/// 3. (In production: print M', measure deviation D'(x), update D = D + alpha * D')
/// 4. Repeat until ||D'|| < tolerance or max iterations reached
///
/// The deviation field can be:
/// - Uniform shrinkage (scalar per axis)
/// - Spatial (per-vertex from 3D scan comparison)
/// - RBF interpolated (from sparse measurement points)
/// </summary>
public static class CeramicPredistortionPipeline
{
    public sealed record PredistortionConfig
    {
        /// <summary>Relaxation factor (0-1). Lower = more conservative, slower convergence.</summary>
        public float Alpha { get; init; } = 0.7f;
        /// <summary>Max iterations for the fixed-point loop.</summary>
        public int MaxIterations { get; init; } = 5;
        /// <summary>Convergence tolerance (mm). Stop when max deviation < this.</summary>
        public float ToleranceMm { get; init; } = 0.05f;
        /// <summary>Uniform shrinkage (%). Applied if no spatial field provided.</summary>
        public float ShrinkageXPct { get; init; } = 15f;
        public float ShrinkageYPct { get; init; } = 15f;
        public float ShrinkageZPct { get; init; } = 18f; // Z typically shrinks more in ceramics
    }

    public sealed record DeviationField
    {
        /// <summary>Per-vertex displacement vectors. null = use uniform shrinkage.</summary>
        public Vector3[]? PerVertexDisplacements { get; init; }
    }

    public sealed record PredistortionResult
    {
        public required StlMesh PredistortedMesh { get; init; }
        public required int IterationsUsed { get; init; }
        public required float MaxDeviationMm { get; init; }
        public required float AvgDeviationMm { get; init; }
        public required bool Converged { get; init; }
        public required Vector3 ScaleApplied { get; init; }
    }

    /// <summary>
    /// Apply predistortion to compensate for ceramic sintering.
    /// </summary>
    public static PredistortionResult Predistort(
        StlMesh nominalMesh,
        PredistortionConfig config,
        DeviationField? measuredDeviation = null)
    {
        var center = (nominalMesh.Min + nominalMesh.Max) * 0.5f;
        int vertCount = nominalMesh.Vertices.Length;

        // Compute displacement field
        var displacements = new Vector3[vertCount];

        if (measuredDeviation?.PerVertexDisplacements != null &&
            measuredDeviation.PerVertexDisplacements.Length == vertCount)
        {
            // Use measured spatial deviation (inverse: predistort = nominal - deviation)
            for (int i = 0; i < vertCount; i++)
                displacements[i] = -measuredDeviation.PerVertexDisplacements[i];
        }
        else
        {
            // Uniform shrinkage compensation: scale up by inverse of shrinkage
            float sx = 1f / (1f - config.ShrinkageXPct / 100f) - 1f;
            float sy = 1f / (1f - config.ShrinkageYPct / 100f) - 1f;
            float sz = 1f / (1f - config.ShrinkageZPct / 100f) - 1f;

            for (int i = 0; i < vertCount; i++)
            {
                var v = nominalMesh.Vertices[i];
                displacements[i] = new Vector3(
                    (v.X - center.X) * sx,
                    (v.Y - center.Y) * sy,
                    (v.Z - center.Z) * sz);
            }
        }

        // Iterative fixed-point with relaxation
        var currentVerts = new Vector3[vertCount];
        Array.Copy(nominalMesh.Vertices, currentVerts, vertCount);

        float maxDev = float.MaxValue;
        float avgDev = 0;
        int iter = 0;
        bool converged = false;

        for (iter = 0; iter < config.MaxIterations; iter++)
        {
            maxDev = 0; avgDev = 0;

            for (int i = 0; i < vertCount; i++)
            {
                var target = nominalMesh.Vertices[i] + displacements[i] * config.Alpha;
                var delta = target - currentVerts[i];
                float devMag = delta.Length();
                maxDev = Math.Max(maxDev, devMag);
                avgDev += devMag;
                currentVerts[i] = target;
            }

            avgDev /= vertCount;

            if (maxDev < config.ToleranceMm)
            {
                converged = true;
                break;
            }
        }

        // Build result mesh
        float scaleX = 1f / (1f - config.ShrinkageXPct / 100f);
        float scaleY = 1f / (1f - config.ShrinkageYPct / 100f);
        float scaleZ = 1f / (1f - config.ShrinkageZPct / 100f);

        // Use the uniform scale path to create a new mesh
        var resultMesh = nominalMesh.Transform(Vector3.Zero, 1f); // clone
        // Apply the computed displacements to the clone's vertices
        // Note: StlMesh.Transform only supports uniform scale, so for anisotropic
        // we directly manipulate vertices via a new mesh construction
        var finalVerts = currentVerts;

        // Reconstruct mesh with displaced vertices
        int triCount = nominalMesh.TriangleCount;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = finalVerts[t * 3]; var v1 = finalVerts[t * 3 + 1]; var v2 = finalVerts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length(); if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
            off += 12;
            for (int vi = 0; vi < 3; vi++)
            {
                BitConverter.GetBytes(finalVerts[t * 3 + vi].X).CopyTo(data, off);
                BitConverter.GetBytes(finalVerts[t * 3 + vi].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(finalVerts[t * 3 + vi].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }

        return new PredistortionResult
        {
            PredistortedMesh = StlMesh.FromBinary(data),
            IterationsUsed = iter + 1,
            MaxDeviationMm = maxDev,
            AvgDeviationMm = avgDev,
            Converged = converged,
            ScaleApplied = new Vector3(scaleX, scaleY, scaleZ),
        };
    }
}
