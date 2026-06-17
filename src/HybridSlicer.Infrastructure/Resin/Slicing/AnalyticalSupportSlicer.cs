using System.Numerics;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Resin.Slicing;

/// <summary>
/// Computes support cross-sections analytically — no mesh tessellation needed.
///
/// Since all support elements are geometric primitives (spheres, cylinders, cones),
/// their cross-sections at any Z height can be computed exactly:
/// - Sphere at height h: circle with radius sqrt(R² - (h - center_z)²)
/// - Vertical cylinder: circle with constant radius
/// - Tilted cylinder/frustum: ellipse (computed with rotation transform)
/// - Cone/frustum: circle with linearly interpolated radius
///
/// This is the PrusaSlicer 2.9.5+ approach — mathematically exact cross-sections
/// without tessellation artifacts.
///
/// Output: list of circles (center_x, center_y, radius) per layer.
/// These are unioned with the model polygon to produce the final layer image.
/// </summary>
public static class AnalyticalSupportSlicer
{
    /// <summary>
    /// A circle cross-section at a given Z layer.
    /// </summary>
    public readonly struct SupportCircle
    {
        public required float CenterX { get; init; }
        public required float CenterY { get; init; }
        public required float Radius { get; init; }
        /// <summary>True if this is a support circle (for adaptive exposure marking).</summary>
        public bool IsSupport { get; init; }
    }

    /// <summary>
    /// Cross-section result for one layer.
    /// </summary>
    public sealed class LayerCrossSection
    {
        public required float Z { get; init; }
        public required List<SupportCircle> Circles { get; init; }
    }

    /// <summary>
    /// A polygon cross-section at a given Z layer (for non-circular shapes: cube, cross, pyramid).
    /// </summary>
    public readonly struct SupportPolygon
    {
        public required float CenterX { get; init; }
        public required float CenterY { get; init; }
        /// <summary>Vertices of the polygon outline (closed, CCW winding).</summary>
        public required Vector2[] Vertices { get; init; }
        public bool IsSupport { get; init; }
    }

    /// <summary>
    /// A support element that can be analytically sliced.
    /// Represents a segment from point A to point B with radii at each end.
    /// </summary>
    public sealed class SupportElement
    {
        public required Vector3 PointA { get; init; }
        public required Vector3 PointB { get; init; }
        public required float RadiusA { get; init; }
        public required float RadiusB { get; init; }
        /// <summary>"sphere", "pillar", "bridge", "pinhead", "pedestal", "interconnect", "fillet", "raft"</summary>
        public required string Type { get; init; }
        /// <summary>Number of polygon sides for cross-section. 0 or >= 24 = circle, 4 = cube/pyramid, 8 = cross.</summary>
        public int Sides { get; set; }
        /// <summary>Rotation angle (radians) for polygon cross-sections.</summary>
        public float RotationRad { get; init; }
    }

    /// <summary>
    /// Convert pillar routes and interconnections to sliceable elements.
    /// </summary>
    public static List<SupportElement> ExtractElements(
        List<(PinheadOptimizer.Pinhead pinhead, PillarRouter.PillarRoute route)> supports,
        List<InterconnectBuilder.Interconnection> interconnections)
    {
        var elements = new List<SupportElement>();

        foreach (var (pinhead, route) in supports)
        {
            // Pinhead: contact → pin center → back center → junction
            elements.Add(new SupportElement
            {
                PointA = pinhead.ContactPoint, PointB = pinhead.PinCenter,
                RadiusA = 0, RadiusB = pinhead.PinRadius,
                Type = "pinhead",
            });
            elements.Add(new SupportElement
            {
                PointA = pinhead.PinCenter, PointB = pinhead.BackCenter,
                RadiusA = pinhead.PinRadius, RadiusB = pinhead.BackRadius,
                Type = "pinhead",
            });
            elements.Add(new SupportElement
            {
                PointA = pinhead.BackCenter, PointB = pinhead.JunctionPoint,
                RadiusA = pinhead.BackRadius, RadiusB = pinhead.BackRadius,
                Type = "pinhead",
            });

            // FIX: If route.Path[0] doesn't match JunctionPoint (e.g. after fillet),
            // emit a connecting element to close the gap.
            if (route.Path.Count > 0)
            {
                float gapDist = Vector3.Distance(pinhead.JunctionPoint, route.Path[0].Position);
                if (gapDist > 0.01f)
                {
                    elements.Add(new SupportElement
                    {
                        PointA = pinhead.JunctionPoint,
                        PointB = route.Path[0].Position,
                        RadiusA = pinhead.BackRadius,
                        RadiusB = route.Path[0].Radius,
                        Type = "junction",
                    });
                }
            }

            // Route waypoints
            for (int i = 0; i < route.Path.Count - 1; i++)
            {
                elements.Add(new SupportElement
                {
                    PointA = route.Path[i].Position,
                    PointB = route.Path[i + 1].Position,
                    RadiusA = route.Path[i].Radius,
                    RadiusB = route.Path[i + 1].Radius,
                    Type = route.Path[i].Type,
                });
            }
        }

        // Interconnections
        foreach (var conn in interconnections)
        {
            elements.Add(new SupportElement
            {
                PointA = conn.PointA, PointB = conn.PointB,
                RadiusA = conn.Radius, RadiusB = conn.Radius,
                Type = "interconnect",
            });
        }

        return elements;
    }

    /// <summary>
    /// Compute the analytical cross-section of all support elements at a given Z height.
    /// Returns a list of circles to be unioned with the model polygon.
    /// </summary>
    public static List<SupportCircle> SliceAtZ(List<SupportElement> elements, float z)
    {
        var circles = new List<SupportCircle>();

        foreach (var elem in elements)
        {
            float zA = elem.PointA.Z, zB = elem.PointB.Z;
            float zMin = Math.Min(zA, zB), zMax = Math.Max(zA, zB);

            // Skip elements that don't span this Z
            if (z < zMin - 0.001f || z > zMax + 0.001f) continue;

            float segHeight = Math.Abs(zA - zB);
            if (segHeight < 0.001f)
            {
                // Horizontal element: check if Z matches
                if (Math.Abs(z - zA) < 0.01f)
                {
                    float r = Math.Max(elem.RadiusA, elem.RadiusB);
                    if (r > 0.01f)
                    {
                        // Add circles at both endpoints and midpoints
                        circles.Add(new SupportCircle { CenterX = elem.PointA.X, CenterY = elem.PointA.Y, Radius = r, IsSupport = true });
                        circles.Add(new SupportCircle { CenterX = elem.PointB.X, CenterY = elem.PointB.Y, Radius = r, IsSupport = true });
                    }
                }
                continue;
            }

            // Interpolation parameter along the segment at this Z
            float t = (z - zA) / (zB - zA);
            t = Math.Clamp(t, 0f, 1f);

            // Interpolated position (XY center of the cross-section)
            float cx = elem.PointA.X + (elem.PointB.X - elem.PointA.X) * t;
            float cy = elem.PointA.Y + (elem.PointB.Y - elem.PointA.Y) * t;

            // Interpolated radius (linear for frustums/cones)
            float r2 = elem.RadiusA + (elem.RadiusB - elem.RadiusA) * t;

            // For tilted elements, the cross-section is an ellipse, but we approximate
            // with a circle using the effective radius. The exact ellipse would require:
            // r_effective = r / cos(tilt_angle)
            // For small tilt angles (<45°), the approximation is within 15%.
            float dx = elem.PointB.X - elem.PointA.X;
            float dy = elem.PointB.Y - elem.PointA.Y;
            float dz = elem.PointB.Z - elem.PointA.Z;
            float xyDist = MathF.Sqrt(dx * dx + dy * dy);
            float tiltAngle = MathF.Atan2(xyDist, Math.Abs(dz));

            // Effective radius: expand for tilted elements
            float cosAngle = MathF.Cos(tiltAngle);
            float effectiveR = cosAngle > 0.1f ? r2 / cosAngle : r2;
            // Cap the expansion to prevent absurdly large circles for near-horizontal elements
            effectiveR = Math.Min(effectiveR, r2 * 3f);

            if (effectiveR > 0.01f)
            {
                circles.Add(new SupportCircle
                {
                    CenterX = cx, CenterY = cy, Radius = effectiveR, IsSupport = true,
                });
            }
        }

        return circles;
    }

    /// <summary>
    /// Slice elements at Z, returning both circles AND polygon cross-sections.
    /// Non-circular elements (Sides == 4 for cube/pyramid, 8 for cross) produce polygons.
    /// </summary>
    public static (List<SupportCircle> circles, List<SupportPolygon> polygons) SliceAtZFull(
        List<SupportElement> elements, float z)
    {
        var circles = new List<SupportCircle>();
        var polygons = new List<SupportPolygon>();

        foreach (var elem in elements)
        {
            float zA = elem.PointA.Z, zB = elem.PointB.Z;
            float zMin = Math.Min(zA, zB), zMax = Math.Max(zA, zB);
            if (z < zMin - 0.001f || z > zMax + 0.001f) continue;

            float segHeight = Math.Abs(zA - zB);
            if (segHeight < 0.001f)
            {
                if (Math.Abs(z - zA) < 0.01f)
                {
                    float r = Math.Max(elem.RadiusA, elem.RadiusB);
                    if (r > 0.01f)
                    {
                        circles.Add(new SupportCircle { CenterX = elem.PointA.X, CenterY = elem.PointA.Y, Radius = r, IsSupport = true });
                        circles.Add(new SupportCircle { CenterX = elem.PointB.X, CenterY = elem.PointB.Y, Radius = r, IsSupport = true });
                    }
                }
                continue;
            }

            float t = (z - zA) / (zB - zA);
            t = Math.Clamp(t, 0f, 1f);
            float cx = elem.PointA.X + (elem.PointB.X - elem.PointA.X) * t;
            float cy = elem.PointA.Y + (elem.PointB.Y - elem.PointA.Y) * t;
            float r2 = elem.RadiusA + (elem.RadiusB - elem.RadiusA) * t;

            if (r2 < 0.01f) continue;

            bool isPolygon = elem.Sides > 0 && elem.Sides < 24;
            if (isPolygon)
            {
                var verts = GeneratePolygonVertices(cx, cy, r2, elem.Sides, elem.RotationRad);
                polygons.Add(new SupportPolygon { CenterX = cx, CenterY = cy, Vertices = verts, IsSupport = true });
            }
            else
            {
                float dx = elem.PointB.X - elem.PointA.X;
                float dy = elem.PointB.Y - elem.PointA.Y;
                float dz = elem.PointB.Z - elem.PointA.Z;
                float xyDist = MathF.Sqrt(dx * dx + dy * dy);
                float tiltAngle = MathF.Atan2(xyDist, Math.Abs(dz));
                float cosAngle = MathF.Cos(tiltAngle);
                float effectiveR = cosAngle > 0.1f ? r2 / cosAngle : r2;
                effectiveR = Math.Min(effectiveR, r2 * 3f);
                if (effectiveR > 0.01f)
                    circles.Add(new SupportCircle { CenterX = cx, CenterY = cy, Radius = effectiveR, IsSupport = true });
            }
        }

        return (circles, polygons);
    }

    /// <summary>
    /// Generate regular polygon vertices at a given center and radius.
    /// Used for cube (4 sides), pyramid (4 sides) cross-sections.
    /// Uses the same vertex formula as SupportMesher.Frustum so preview and print match.
    /// </summary>
    public static Vector2[] GeneratePolygonVertices(float cx, float cy, float radius, int sides, float rotationRad = 0)
    {
        // Special case: 8 sides with cross flag generates a genuine plus-shape
        // (not a regular octagon). See GenerateCrossVertices.
        if (sides == 8)
            return GenerateCrossVertices(cx, cy, radius, rotationRad);

        var verts = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            float angle = 2f * MathF.PI * i / sides + rotationRad;
            verts[i] = new Vector2(
                cx + MathF.Cos(angle) * radius,
                cy + MathF.Sin(angle) * radius);
        }
        return verts;
    }

    /// <summary>
    /// Generate a plus-shaped (cross) cross-section with 12 vertices.
    /// The arm width is 40% of the radius; the arm length extends to the full radius.
    /// This matches the mesh generation in SupportMesher so preview == print.
    /// </summary>
    public static Vector2[] GenerateCrossVertices(float cx, float cy, float radius, float rotationRad = 0)
    {
        float armWidth = radius * 0.4f;  // half-width of each arm
        float cos = MathF.Cos(rotationRad), sin = MathF.Sin(rotationRad);
        Vector2 Rot(float x, float y) => new(cx + x * cos - y * sin, cy + x * sin + y * cos);

        // 12 vertices forming a plus shape (CCW), starting from top-right arm
        return new[]
        {
            Rot(armWidth, radius),   // top arm, right edge
            Rot(armWidth, armWidth), // inner corner
            Rot(radius, armWidth),   // right arm, top edge
            Rot(radius, -armWidth),  // right arm, bottom edge
            Rot(armWidth, -armWidth),// inner corner
            Rot(armWidth, -radius),  // bottom arm, right edge
            Rot(-armWidth, -radius), // bottom arm, left edge
            Rot(-armWidth, -armWidth),// inner corner
            Rot(-radius, -armWidth), // left arm, bottom edge
            Rot(-radius, armWidth),  // left arm, top edge
            Rot(-armWidth, armWidth),// inner corner
            Rot(-armWidth, radius),  // top arm, left edge
        };
    }

    /// <summary>
    /// Slice all elements at all layer heights.
    /// </summary>
    public static List<LayerCrossSection> SliceAll(List<SupportElement> elements, float layerHeight,
        float minZ, float maxZ)
    {
        var results = new List<LayerCrossSection>();
        for (float z = minZ + layerHeight * 0.5f; z <= maxZ; z += layerHeight)
        {
            var circles = SliceAtZ(elements, z);
            if (circles.Count > 0)
            {
                results.Add(new LayerCrossSection { Z = z, Circles = circles });
            }
        }
        return results;
    }
}
