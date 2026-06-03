using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Generates lattice-pattern pedestals to replace solid base cones.
///
/// Solid pedestals waste resin and create large suction forces during peel (bottom-up)
/// or high shear during recoat (top-down). Lattice patterns provide:
///
/// - Reduced resin usage (60-80% less than solid, depending on pattern)
/// - Lower peel forces (resin drains through the lattice gaps)
/// - Easier part removal from the build plate
/// - Adequate structural support for the pillar above
///
/// Patterns:
/// - Grid: orthogonal struts in X/Y directions (simple, easy to print)
/// - Honeycomb: hexagonal arrangement (best strength-to-weight ratio)
/// - Cross: X-pattern diagonal struts (good torsional resistance)
/// - Solid: traditional solid frustum (fallback for critical supports)
///
/// Each strut is generated as an OrientedFrustum connecting lattice intersection
/// points, with a top ring connecting to the pillar and a bottom ring on the bed.
/// </summary>
public static class LatticeBase
{
    /// <summary>
    /// Available lattice patterns for support bases.
    /// </summary>
    public enum LatticePattern
    {
        /// <summary>Orthogonal grid of struts aligned with X/Y axes.</summary>
        Grid,
        /// <summary>Hexagonal honeycomb pattern for optimal strength-to-weight.</summary>
        Honeycomb,
        /// <summary>Diagonal X-pattern struts for torsional resistance.</summary>
        Cross,
        /// <summary>Traditional solid frustum (no lattice).</summary>
        Solid,
    }

    /// <summary>
    /// Generate a lattice base at the given position.
    /// </summary>
    /// <param name="position">Center position of the base on the build plate (Z = bed level).</param>
    /// <param name="topRadius">Radius at the top of the base (connects to pillar).</param>
    /// <param name="baseRadius">Radius at the bottom of the base (on the build plate).</param>
    /// <param name="height">Height of the base pedestal (mm).</param>
    /// <param name="pattern">Lattice pattern to use.</param>
    /// <param name="strutDiameter">Diameter of individual lattice struts (mm).</param>
    /// <param name="spacing">Distance between lattice strut centers (mm).</param>
    /// <param name="sides">Circumferential segments for strut tessellation.</param>
    /// <returns>Indexed triangle mesh of the lattice base.</returns>
    public static IndexedTriangleSet Generate(
        Vector3 position, float topRadius, float baseRadius, float height,
        LatticePattern pattern = LatticePattern.Grid,
        float strutDiameter = 0.4f, float spacing = 1.0f, int sides = 24)
    {
        if (pattern == LatticePattern.Solid || height < 0.5f)
        {
            return SupportMesher.Pedestal(position, topRadius, baseRadius, height, sides);
        }

        var mesh = new IndexedTriangleSet();
        float strutRadius = strutDiameter * 0.5f;

        // Generate the connection ring at the top (pillar attachment)
        var topRing = GenerateRing(
            new Vector3(position.X, position.Y, position.Z + height),
            topRadius, strutRadius, sides);
        mesh.Merge(topRing);

        // Generate the base ring at the bottom (build plate)
        var botRing = GenerateRing(position, baseRadius, strutRadius, sides);
        mesh.Merge(botRing);

        // Generate vertical edge pillars connecting top ring to base ring
        int edgePillars = Math.Max(4, sides / 3);
        var topEdgePoints = new Vector3[edgePillars];
        var botEdgePoints = new Vector3[edgePillars];

        for (int i = 0; i < edgePillars; i++)
        {
            float angle = 2f * MathF.PI * i / edgePillars;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            topEdgePoints[i] = new Vector3(
                position.X + cos * topRadius,
                position.Y + sin * topRadius,
                position.Z + height);
            botEdgePoints[i] = new Vector3(
                position.X + cos * baseRadius,
                position.Y + sin * baseRadius,
                position.Z);

            // Vertical edge strut
            var strut = SupportMesher.OrientedFrustum(
                botEdgePoints[i], topEdgePoints[i],
                strutRadius, strutRadius, Math.Max(6, sides / 4));
            mesh.Merge(strut);
        }

        // Generate internal lattice struts based on pattern
        switch (pattern)
        {
            case LatticePattern.Grid:
                GenerateGridStruts(mesh, position, topRadius, baseRadius, height,
                    strutRadius, spacing, sides);
                break;
            case LatticePattern.Honeycomb:
                GenerateHoneycombStruts(mesh, position, topRadius, baseRadius, height,
                    strutRadius, spacing, sides);
                break;
            case LatticePattern.Cross:
                GenerateCrossStruts(mesh, position, topRadius, baseRadius, height,
                    strutRadius, spacing, sides);
                break;
        }

        // Center vertical strut (connects pillar directly to bed)
        var centerStrut = SupportMesher.OrientedFrustum(
            position,
            new Vector3(position.X, position.Y, position.Z + height),
            strutRadius * 1.2f, strutRadius, Math.Max(6, sides / 4));
        mesh.Merge(centerStrut);

        return mesh;
    }

    // ── Grid pattern ────────────────────────────────────────────────────

    /// <summary>
    /// Generate orthogonal grid struts at mid-height within the base envelope.
    /// </summary>
    private static void GenerateGridStruts(
        IndexedTriangleSet mesh, Vector3 position,
        float topRadius, float baseRadius, float height,
        float strutRadius, float spacing, int sides)
    {
        float midZ = position.Z + height * 0.5f;
        float midRadius = (topRadius + baseRadius) * 0.5f;
        int strutSides = Math.Max(4, sides / 6);

        // X-aligned struts
        for (float y = -midRadius + spacing; y < midRadius; y += spacing)
        {
            // Compute chord length at this Y offset
            float chordHalf = ChordHalfLength(midRadius, y);
            if (chordHalf < spacing * 0.5f) continue;

            var p1 = new Vector3(position.X - chordHalf, position.Y + y, midZ);
            var p2 = new Vector3(position.X + chordHalf, position.Y + y, midZ);
            mesh.Merge(SupportMesher.OrientedFrustum(p1, p2, strutRadius, strutRadius, strutSides));
        }

        // Y-aligned struts
        for (float x = -midRadius + spacing; x < midRadius; x += spacing)
        {
            float chordHalf = ChordHalfLength(midRadius, x);
            if (chordHalf < spacing * 0.5f) continue;

            var p1 = new Vector3(position.X + x, position.Y - chordHalf, midZ);
            var p2 = new Vector3(position.X + x, position.Y + chordHalf, midZ);
            mesh.Merge(SupportMesher.OrientedFrustum(p1, p2, strutRadius, strutRadius, strutSides));
        }

        // Vertical struts at grid intersections
        for (float x = -midRadius + spacing; x < midRadius; x += spacing)
        for (float y = -midRadius + spacing; y < midRadius; y += spacing)
        {
            if (x * x + y * y > midRadius * midRadius) continue;

            // Interpolate radii at top and bottom for this XY offset
            float dist = MathF.Sqrt(x * x + y * y);
            if (dist > midRadius * 0.9f) continue;

            var bot = new Vector3(position.X + x, position.Y + y, position.Z);
            var top = new Vector3(position.X + x, position.Y + y, position.Z + height);
            mesh.Merge(SupportMesher.OrientedFrustum(bot, top, strutRadius * 0.8f, strutRadius * 0.8f, strutSides));
        }
    }

    // ── Honeycomb pattern ───────────────────────────────────────────────

    /// <summary>
    /// Generate hexagonal honeycomb struts within the base envelope.
    /// </summary>
    private static void GenerateHoneycombStruts(
        IndexedTriangleSet mesh, Vector3 position,
        float topRadius, float baseRadius, float height,
        float strutRadius, float spacing, int sides)
    {
        float midZ = position.Z + height * 0.5f;
        float midRadius = (topRadius + baseRadius) * 0.5f;
        int strutSides = Math.Max(4, sides / 6);

        // Hexagonal grid: offset every other row by spacing/2
        float rowSpacing = spacing * MathF.Sqrt(3f) * 0.5f;
        int row = 0;

        for (float y = -midRadius + rowSpacing; y < midRadius; y += rowSpacing)
        {
            float xOffset = (row % 2 == 0) ? 0 : spacing * 0.5f;

            for (float x = -midRadius + spacing + xOffset; x < midRadius; x += spacing)
            {
                if (x * x + y * y > midRadius * midRadius * 0.85f) continue;

                var center = new Vector3(position.X + x, position.Y + y, midZ);

                // Connect to 3 neighbors (60-degree increments: 0, 120, 240)
                for (int dir = 0; dir < 3; dir++)
                {
                    float angle = dir * MathF.PI * 2f / 3f;
                    float nx = x + MathF.Cos(angle) * spacing * 0.5f;
                    float ny = y + MathF.Sin(angle) * spacing * 0.5f;
                    if (nx * nx + ny * ny > midRadius * midRadius) continue;

                    var neighbor = new Vector3(position.X + nx, position.Y + ny, midZ);
                    mesh.Merge(SupportMesher.OrientedFrustum(center, neighbor, strutRadius, strutRadius, strutSides));
                }

                // Vertical strut
                var bot = new Vector3(center.X, center.Y, position.Z);
                var top = new Vector3(center.X, center.Y, position.Z + height);
                mesh.Merge(SupportMesher.OrientedFrustum(bot, top, strutRadius * 0.7f, strutRadius * 0.7f, strutSides));
            }
            row++;
        }
    }

    // ── Cross pattern ───────────────────────────────────────────────────

    /// <summary>
    /// Generate diagonal X-pattern struts within the base envelope.
    /// </summary>
    private static void GenerateCrossStruts(
        IndexedTriangleSet mesh, Vector3 position,
        float topRadius, float baseRadius, float height,
        float strutRadius, float spacing, int sides)
    {
        float midRadius = (topRadius + baseRadius) * 0.5f;
        int strutSides = Math.Max(4, sides / 6);

        // Diagonal struts: from base corners to opposite top corners
        for (float x = -midRadius + spacing; x < midRadius; x += spacing)
        for (float y = -midRadius + spacing; y < midRadius; y += spacing)
        {
            if (x * x + y * y > midRadius * midRadius * 0.8f) continue;

            // Forward diagonal (\)
            var p1 = new Vector3(position.X + x, position.Y + y, position.Z);
            var p2 = new Vector3(position.X + x + spacing * 0.5f,
                                 position.Y + y + spacing * 0.5f,
                                 position.Z + height);
            if (DistFromCenter2D(p2, position) < midRadius)
                mesh.Merge(SupportMesher.OrientedFrustum(p1, p2, strutRadius, strutRadius, strutSides));

            // Back diagonal (/)
            var p3 = new Vector3(position.X + x + spacing, position.Y + y, position.Z);
            var p4 = new Vector3(position.X + x + spacing * 0.5f,
                                 position.Y + y + spacing * 0.5f,
                                 position.Z + height);
            if (DistFromCenter2D(p3, position) < midRadius && DistFromCenter2D(p4, position) < midRadius)
                mesh.Merge(SupportMesher.OrientedFrustum(p3, p4, strutRadius, strutRadius, strutSides));
        }
    }

    // ── Ring generation ─────────────────────────────────────────────────

    /// <summary>
    /// Generate a thin ring (torus-like) at the given position and radius.
    /// Used for top/bottom connection rings of the lattice.
    /// </summary>
    private static IndexedTriangleSet GenerateRing(Vector3 center, float ringRadius,
        float tubeRadius, int sides)
    {
        var mesh = new IndexedTriangleSet();
        int tubeSides = Math.Max(4, sides / 6);

        // Generate a series of short frustums around the ring circumference
        for (int i = 0; i < sides; i++)
        {
            float angle1 = 2f * MathF.PI * i / sides;
            float angle2 = 2f * MathF.PI * ((i + 1) % sides) / sides;

            var p1 = new Vector3(
                center.X + MathF.Cos(angle1) * ringRadius,
                center.Y + MathF.Sin(angle1) * ringRadius,
                center.Z);
            var p2 = new Vector3(
                center.X + MathF.Cos(angle2) * ringRadius,
                center.Y + MathF.Sin(angle2) * ringRadius,
                center.Z);

            mesh.Merge(SupportMesher.OrientedFrustum(p1, p2, tubeRadius, tubeRadius, tubeSides));
        }

        return mesh;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Compute half-length of a chord at distance d from center of circle with radius r.
    /// </summary>
    private static float ChordHalfLength(float radius, float offset)
    {
        float d2 = radius * radius - offset * offset;
        return d2 > 0 ? MathF.Sqrt(d2) : 0;
    }

    /// <summary>
    /// Compute XY distance from a 3D point to a center position (ignoring Z).
    /// </summary>
    private static float DistFromCenter2D(Vector3 point, Vector3 center)
    {
        float dx = point.X - center.X;
        float dy = point.Y - center.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
