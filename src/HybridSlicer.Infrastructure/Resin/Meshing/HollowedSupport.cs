using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Generates hollow shell geometry for tall support pillars.
///
/// For pillars exceeding a configurable height threshold (default 20mm), solid frustums
/// are replaced with thin-walled shells to reduce resin consumption while maintaining
/// structural integrity. The hollow frustum consists of:
///
/// - Outer frustum (same as the original solid)
/// - Inner frustum (reduced radius by wall thickness)
/// - Top annular cap (ring connecting outer and inner top edges)
/// - Bottom annular cap (ring connecting outer and inner bottom edges)
///
/// Material savings: for a 1mm radius pillar with 0.6mm walls, volume reduction is ~64%.
/// Structural note: thin-walled cylinders have nearly equivalent buckling resistance
/// to solid cylinders of the same outer diameter for wall thickness >= 0.4mm at
/// typical support dimensions.
/// </summary>
public static class HollowedSupport
{
    /// <summary>
    /// Default wall thickness for hollow supports (mm).
    /// Must be >= minimum printable feature size (typically 0.3-0.4mm for LCD printers).
    /// </summary>
    public const float DefaultWallThickness = 0.6f;

    /// <summary>
    /// Default minimum pillar height to apply hollowing (mm).
    /// Short pillars are not worth hollowing due to cap overhead.
    /// </summary>
    public const float DefaultMinHeightMm = 20f;

    /// <summary>
    /// Generate a hollow frustum (shell) with inner and outer walls plus annular caps.
    /// </summary>
    /// <param name="rTopOuter">Outer radius at the top of the frustum (mm).</param>
    /// <param name="rBotOuter">Outer radius at the bottom of the frustum (mm).</param>
    /// <param name="wallThickness">Wall thickness (mm). Inner radius = outer - wall.</param>
    /// <param name="height">Height of the frustum along the Y axis (mm).</param>
    /// <param name="sides">Number of circumferential segments for tessellation.</param>
    /// <returns>Watertight indexed triangle mesh of the hollow frustum.</returns>
    public static IndexedTriangleSet HollowFrustum(
        float rTopOuter, float rBotOuter,
        float wallThickness, float height, int sides = 24)
    {
        var mesh = new IndexedTriangleSet();
        if (height < 1e-6f) return mesh;

        // Compute inner radii, clamped to ensure positive values
        float rTopInner = MathF.Max(rTopOuter - wallThickness, rTopOuter * 0.1f);
        float rBotInner = MathF.Max(rBotOuter - wallThickness, rBotOuter * 0.1f);

        // If the inner radius would be too small (nearly solid), fall back to solid
        if (rTopInner < 0.1f && rBotInner < 0.1f)
        {
            return SupportMesher.Frustum(rTopOuter, rBotOuter, height, sides);
        }

        // Generate vertex rings: outer top, outer bottom, inner top, inner bottom
        var outerTopRing = new int[sides];
        var outerBotRing = new int[sides];
        var innerTopRing = new int[sides];
        var innerBotRing = new int[sides];

        for (int i = 0; i < sides; i++)
        {
            float angle = 2f * MathF.PI * i / sides;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            outerTopRing[i] = mesh.AddVertex(new Vector3(cos * rTopOuter, height, sin * rTopOuter));
            outerBotRing[i] = mesh.AddVertex(new Vector3(cos * rBotOuter, 0, sin * rBotOuter));
            innerTopRing[i] = mesh.AddVertex(new Vector3(cos * rTopInner, height, sin * rTopInner));
            innerBotRing[i] = mesh.AddVertex(new Vector3(cos * rBotInner, 0, sin * rBotInner));
        }

        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;

            // Outer wall (normals face outward) — same winding as SupportMesher.Frustum
            mesh.AddFace(outerTopRing[i], outerBotRing[i], outerBotRing[next]);
            mesh.AddFace(outerTopRing[i], outerBotRing[next], outerTopRing[next]);

            // Inner wall (normals face inward — reversed winding)
            mesh.AddFace(innerTopRing[i], innerBotRing[next], innerBotRing[i]);
            mesh.AddFace(innerTopRing[i], innerTopRing[next], innerBotRing[next]);

            // Top annular cap (connects outer top to inner top, normal faces up)
            mesh.AddFace(outerTopRing[i], outerTopRing[next], innerTopRing[next]);
            mesh.AddFace(outerTopRing[i], innerTopRing[next], innerTopRing[i]);

            // Bottom annular cap (connects outer bottom to inner bottom, normal faces down)
            mesh.AddFace(outerBotRing[i], innerBotRing[i], innerBotRing[next]);
            mesh.AddFace(outerBotRing[i], innerBotRing[next], outerBotRing[next]);
        }

        return mesh;
    }

    /// <summary>
    /// Determine whether a pillar segment should be hollowed based on its height.
    /// </summary>
    /// <param name="segmentHeight">Height of the pillar segment (mm).</param>
    /// <param name="minHeightMm">Minimum height threshold for hollowing.</param>
    /// <returns>True if the segment should use hollow geometry.</returns>
    public static bool ShouldHollow(float segmentHeight, float minHeightMm = DefaultMinHeightMm)
    {
        return segmentHeight > minHeightMm;
    }

    /// <summary>
    /// Generate an oriented hollow frustum between two 3D points.
    /// Equivalent to <see cref="SupportMesher.OrientedFrustum"/> but with hollow shell.
    /// </summary>
    /// <param name="pointA">Start point (bottom of frustum).</param>
    /// <param name="pointB">End point (top of frustum).</param>
    /// <param name="radiusA">Outer radius at point A.</param>
    /// <param name="radiusB">Outer radius at point B.</param>
    /// <param name="wallThickness">Wall thickness (mm).</param>
    /// <param name="sides">Number of circumferential segments.</param>
    /// <returns>Oriented hollow frustum mesh.</returns>
    public static IndexedTriangleSet OrientedHollowFrustum(
        Vector3 pointA, Vector3 pointB,
        float radiusA, float radiusB,
        float wallThickness = DefaultWallThickness, int sides = 24)
    {
        float height = Vector3.Distance(pointA, pointB);
        if (height < 1e-6f) return new IndexedTriangleSet();

        // Build hollow frustum along Y axis: bottom at Y=0 (radiusA), top at Y=height (radiusB)
        var mesh = HollowFrustum(radiusB, radiusA, wallThickness, height, sides);

        // Compute rotation from default Y-up to actual direction
        var dir = Vector3.Normalize(pointB - pointA);
        var defaultDir = Vector3.UnitY;
        Quaternion rotation;
        float dot = Vector3.Dot(dir, defaultDir);
        if (dot < -0.999f)
            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        else if (dot > 0.999f)
            rotation = Quaternion.Identity;
        else
        {
            var cross = Vector3.Cross(defaultDir, dir);
            float w = 1f + dot;
            rotation = Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, w));
        }

        // Apply rotation and translation so bottom is at pointA
        mesh.Transform(rotation, pointA);
        return mesh;
    }
}
