using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Spatial;

/// <summary>
/// Fast collision checking utilities — lighter than full beam-cast validation.
/// Used during support generation where speed is critical.
/// Full beam-cast validation available on-demand via CollisionValidator.
/// </summary>
public static class FastCollisionCheck
{
    /// <summary>
    /// Quick check if a vertical pillar from startZ to endZ at (x,y) passes through the mesh.
    /// Uses 3 sample points with IsInside check — much faster than beam-cast.
    /// </summary>
    public static bool PillarHitsMesh(AabbBvh bvh, float x, float y, float startZ, float endZ, int samples = 3)
    {
        float step = (startZ - endZ) / (samples + 1);
        for (int i = 1; i <= samples; i++)
        {
            float z = startZ - step * i;
            if (bvh.IsInside(new Vector3(x, y, z))) return true;
        }
        return false;
    }

    /// <summary>
    /// Quick check if a line segment from A to B passes through the mesh.
    /// Checks N sample points along the segment.
    /// </summary>
    public static bool SegmentHitsMesh(AabbBvh bvh, Vector3 a, Vector3 b, int samples = 3)
    {
        for (int i = 1; i <= samples; i++)
        {
            float t = (float)i / (samples + 1);
            var pt = Vector3.Lerp(a, b, t);
            if (bvh.IsInside(pt)) return true;
        }
        return false;
    }

    /// <summary>
    /// Check if a point is near the mesh surface (within distance threshold).
    /// Uses ClosestPoint query — faster than beam-cast for proximity checks.
    /// </summary>
    public static bool NearSurface(AabbBvh bvh, Vector3 point, float maxDistance)
    {
        var closest = bvh.ClosestPoint(point);
        return closest.HasValue && closest.Value.Distance <= maxDistance;
    }

    /// <summary>
    /// Quick clearance check: minimum distance from a point to the mesh surface.
    /// Returns float.MaxValue if no mesh nearby.
    /// </summary>
    public static float Clearance(AabbBvh bvh, Vector3 point)
    {
        var closest = bvh.ClosestPoint(point);
        return closest?.Distance ?? float.MaxValue;
    }
}
