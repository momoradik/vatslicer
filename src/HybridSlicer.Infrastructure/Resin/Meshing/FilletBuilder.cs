using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Smooths hard corners in support routes by replacing sharp angle changes with
/// circular-arc subdivisions. The centerline curves through the corner instead of
/// kinking, and the resulting frustum chain is tangentially smooth.
///
/// Applied to: fork junctions, tree junctions, bridge waypoints, and any intra-route
/// angle change above a threshold.
/// </summary>
public static class FilletBuilder
{
    /// <summary>Minimum angle change (degrees) to trigger filleting.</summary>
    private const float MIN_ANGLE_DEG = 8f;

    /// <summary>
    /// Process a route's waypoint list and insert arc-subdivided fillets at corners.
    /// Returns a new waypoint list with smooth transitions.
    /// </summary>
    public static List<PillarRouter.Waypoint> FilletRoute(
        List<PillarRouter.Waypoint> path, int subdivisions = 4, float filletFraction = 0.3f)
    {
        if (path.Count < 3 || subdivisions < 1)
            return path;

        var result = new List<PillarRouter.Waypoint>(path.Count + path.Count * subdivisions);
        result.Add(path[0]);

        for (int i = 1; i < path.Count - 1; i++)
        {
            var prev = path[i - 1];
            var curr = path[i];
            var next = path[i + 1];

            // Don't fillet at base waypoints
            if (curr.Type == "base" || next.Type == "base")
            {
                result.Add(curr);
                continue;
            }

            float distIn = Vector3.Distance(prev.Position, curr.Position);
            float distOut = Vector3.Distance(curr.Position, next.Position);

            // Skip if segments are too short to fillet
            if (distIn < 0.1f || distOut < 0.1f)
            {
                result.Add(curr);
                continue;
            }

            var dirIn = (curr.Position - prev.Position) / distIn;
            var dirOut = (next.Position - curr.Position) / distOut;

            // Check for NaN (degenerate directions)
            if (float.IsNaN(dirIn.X) || float.IsNaN(dirOut.X))
            {
                result.Add(curr);
                continue;
            }

            float dot = Vector3.Dot(dirIn, dirOut);
            dot = Math.Clamp(dot, -1f, 1f);
            float angleDeg = MathF.Acos(dot) * 180f / MathF.PI;

            if (angleDeg < MIN_ANGLE_DEG || float.IsNaN(angleDeg))
            {
                result.Add(curr);
                continue;
            }

            // Fillet reach: fraction of the shorter adjacent segment
            float reach = filletFraction * Math.Min(distIn, distOut);
            reach = Math.Min(reach, 5f);
            reach = Math.Max(reach, 0.2f);

            // Points where the arc starts and ends
            var arcStart = curr.Position - dirIn * reach;
            var arcEnd = curr.Position + dirOut * reach;

            // Generate arc waypoints via quadratic Bezier curve
            for (int s = 0; s <= subdivisions; s++)
            {
                float t = (float)s / subdivisions;
                // Hermite smoothstep for natural distribution
                float ts = t * t * (3f - 2f * t);

                // Quadratic Bezier: P = (1-t)²·start + 2(1-t)t·corner + t²·end
                float u = 1f - ts;
                var pos = u * u * arcStart + 2f * u * ts * curr.Position + ts * ts * arcEnd;

                // Smooth radius blend
                float radius = Lerp(prev.Radius, next.Radius, ts) * 0.3f + curr.Radius * 0.7f;

                // Sanity: skip if position is NaN
                if (float.IsNaN(pos.X)) continue;

                result.Add(new PillarRouter.Waypoint
                {
                    Position = pos,
                    Radius = Math.Max(radius, 0.05f),
                    Type = curr.Type,
                });
            }
        }

        result.Add(path[^1]);

        // Post-process: remove any consecutive duplicate positions (zero-length segments
        // produce degenerate frustums with NaN normals in the mesher).
        var cleaned = new List<PillarRouter.Waypoint>(result.Count) { result[0] };
        for (int i = 1; i < result.Count; i++)
        {
            if (Vector3.Distance(result[i].Position, result[i - 1].Position) > 0.01f)
                cleaned.Add(result[i]);
        }

        return cleaned;
    }

    /// <summary>
    /// Generate a tip-to-part cove: a small flare at the contact point where the
    /// support tip meets the part surface, distributing load over an area instead of
    /// a point. Returns extra waypoints to insert between contact and route start.
    /// </summary>
    public static List<PillarRouter.Waypoint> GenerateTipCove(
        Vector3 contactPoint, Vector3 routeStart, float tipRadius, float pillarRadius,
        int subdivisions = 3, float coveHeight = 0.4f)
    {
        float totalDist = Vector3.Distance(contactPoint, routeStart);
        if (totalDist < 0.3f || subdivisions < 1)
            return new List<PillarRouter.Waypoint>();

        var dir = (routeStart - contactPoint) / totalDist;
        if (float.IsNaN(dir.X)) return new List<PillarRouter.Waypoint>();

        float coveLen = Math.Min(coveHeight, totalDist * 0.3f);

        var result = new List<PillarRouter.Waypoint>();

        for (int s = 1; s <= subdivisions; s++)
        {
            float t = (float)s / (subdivisions + 1);
            float tInCove = t * coveLen / totalDist;
            var pos = contactPoint + dir * (totalDist * tInCove);

            // Cove profile: smooth flare from tipRadius outward then back
            float flare = MathF.Sin(t * MathF.PI) * tipRadius * 0.6f;
            float radius = tipRadius + flare * (1f - t) + (pillarRadius - tipRadius) * t;

            if (float.IsNaN(pos.X)) continue;

            result.Add(new PillarRouter.Waypoint
            {
                Position = pos,
                Radius = Math.Max(radius, 0.05f),
                Type = "pillar",
            });
        }

        return result;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
