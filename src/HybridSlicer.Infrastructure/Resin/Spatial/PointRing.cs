using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Generates N evenly-spaced points on a circle perpendicular to a given direction.
/// Used for volumetric beam-casting: checking collisions of cylindrical support elements
/// by casting rays at multiple points around their circumference.
/// </summary>
public static class PointRing
{
    /// <summary>
    /// Generate N points on a circle of the given radius, centered at origin,
    /// lying in the plane perpendicular to the given direction.
    /// </summary>
    public static Vector3[] Generate(Vector3 direction, float radius, int count)
    {
        if (count <= 0) return Array.Empty<Vector3>();
        direction = Vector3.Normalize(direction);

        // Build orthonormal basis perpendicular to direction
        var up = MathF.Abs(direction.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(direction, up));
        up = Vector3.Cross(right, direction);

        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float angle = 2f * MathF.PI * i / count;
            points[i] = right * (MathF.Cos(angle) * radius) + up * (MathF.Sin(angle) * radius);
        }
        return points;
    }

    /// <summary>
    /// Generate N+1 ray origins for beam testing: center + N ring points.
    /// Each origin is offset from the given center point.
    /// </summary>
    public static Vector3[] BeamOrigins(Vector3 center, Vector3 direction, float radius, int ringCount)
    {
        var ring = Generate(direction, radius, ringCount);
        var origins = new Vector3[ring.Length + 1];
        origins[0] = center;
        for (int i = 0; i < ring.Length; i++)
            origins[i + 1] = center + ring[i];
        return origins;
    }
}
