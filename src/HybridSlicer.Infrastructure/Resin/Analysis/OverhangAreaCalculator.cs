using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Calculates the total overhang surface area that needs support.
/// Breaks down by angle range for support density planning.
/// </summary>
public static class OverhangAreaCalculator
{
    public sealed record OverhangBreakdown
    {
        public required float TotalOverhangAreaMm2 { get; init; }
        public required float SteepOverhangMm2 { get; init; }  // >60 deg
        public required float ModerateOverhangMm2 { get; init; } // 45-60 deg
        public required float MildOverhangMm2 { get; init; }    // 30-45 deg
        public required float HorizontalDownMm2 { get; init; }  // near-horizontal facing down
        public required float TotalSurfaceAreaMm2 { get; init; }
        public required float OverhangPct { get; init; }
    }

    public static OverhangBreakdown Calculate(StlMesh mesh, float thresholdAngleDeg = 45f)
    {
        float totalArea = 0, overhangArea = 0;
        float steep = 0, moderate = 0, mild = 0, horizontal = 0;
        float cosThreshold = MathF.Cos(thresholdAngleDeg * MathF.PI / 180f);

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            float len = cross.Length();
            if (len < 1e-6f) continue;
            var normal = cross / len;
            float area = len * 0.5f;
            totalArea += area;

            // Downward-facing check
            if (normal.Z < -cosThreshold)
            {
                overhangArea += area;
                float absAngle = MathF.Acos(Math.Clamp(-normal.Z, 0, 1)) * 180f / MathF.PI;
                if (absAngle < 10) horizontal += area;
                else if (absAngle < 30) steep += area;
                else if (absAngle < 45) moderate += area;
                else mild += area;
            }
        }

        return new OverhangBreakdown
        {
            TotalOverhangAreaMm2 = overhangArea,
            SteepOverhangMm2 = steep,
            ModerateOverhangMm2 = moderate,
            MildOverhangMm2 = mild,
            HorizontalDownMm2 = horizontal,
            TotalSurfaceAreaMm2 = totalArea,
            OverhangPct = totalArea > 0 ? overhangArea / totalArea * 100f : 0,
        };
    }
}
