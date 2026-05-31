using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Generates watertight triangle meshes for support elements.
///
/// PrusaSlicer generates real geometry for each support component:
/// - Pinhead: two partial spheres connected by a tangent cone
/// - Pillar: frustum (tapered cylinder) with optional widening
/// - Junction: sphere at connection points
/// - Bridge: rotated frustum connecting two points
/// - Pedestal: truncated cone at the build plate base
///
/// All meshes are generated as IndexedTriangleSets with configurable
/// tessellation (default 24 sides for production quality).
/// </summary>
public static class SupportMesher
{
    private const int DEFAULT_SIDES = 24;

    // ── Frustum (tapered cylinder) ───────────────────────────────────────

    /// <summary>
    /// Generate a frustum (tapered cylinder) mesh along the Y axis,
    /// from Y=0 (bottom, rBottom) to Y=height (top, rTop).
    /// </summary>
    public static IndexedTriangleSet Frustum(float rTop, float rBottom, float height, int sides = DEFAULT_SIDES)
    {
        var mesh = new IndexedTriangleSet();
        if (height < 1e-6f) return mesh;

        // Generate top and bottom rings
        var topRing = new int[sides];
        var botRing = new int[sides];

        for (int i = 0; i < sides; i++)
        {
            float angle = 2f * MathF.PI * i / sides;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            topRing[i] = mesh.AddVertex(new Vector3(cos * rTop, height, sin * rTop));
            botRing[i] = mesh.AddVertex(new Vector3(cos * rBottom, 0, sin * rBottom));
        }

        // Side faces (quads as 2 triangles each)
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            mesh.AddFace(topRing[i], botRing[i], botRing[next]);
            mesh.AddFace(topRing[i], botRing[next], topRing[next]);
        }

        // Top cap (fan from center)
        if (rTop > 1e-4f)
        {
            int topCenter = mesh.AddVertex(new Vector3(0, height, 0));
            for (int i = 0; i < sides; i++)
                mesh.AddFace(topCenter, topRing[(i + 1) % sides], topRing[i]);
        }

        // Bottom cap
        if (rBottom > 1e-4f)
        {
            int botCenter = mesh.AddVertex(new Vector3(0, 0, 0));
            for (int i = 0; i < sides; i++)
                mesh.AddFace(botCenter, botRing[i], botRing[(i + 1) % sides]);
        }

        return mesh;
    }

    // ── Sphere ───────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a UV sphere centered at origin.
    /// </summary>
    public static IndexedTriangleSet Sphere(float radius, int rings = 12, int sides = DEFAULT_SIDES)
    {
        var mesh = new IndexedTriangleSet();
        if (radius < 1e-6f) return mesh;

        // Top pole
        int topPole = mesh.AddVertex(new Vector3(0, radius, 0));

        // Ring vertices
        var ringIndices = new int[rings - 1][];
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            float y = MathF.Cos(phi) * radius;
            float ringR = MathF.Sin(phi) * radius;
            ringIndices[r - 1] = new int[sides];

            for (int s = 0; s < sides; s++)
            {
                float theta = 2f * MathF.PI * s / sides;
                ringIndices[r - 1][s] = mesh.AddVertex(new Vector3(
                    MathF.Cos(theta) * ringR, y, MathF.Sin(theta) * ringR));
            }
        }

        // Bottom pole
        int botPole = mesh.AddVertex(new Vector3(0, -radius, 0));

        // Top cap (pole to first ring)
        for (int s = 0; s < sides; s++)
            mesh.AddFace(topPole, ringIndices[0][s], ringIndices[0][(s + 1) % sides]);

        // Middle strips
        for (int r = 0; r < rings - 2; r++)
        {
            for (int s = 0; s < sides; s++)
            {
                int next = (s + 1) % sides;
                mesh.AddFace(ringIndices[r][s], ringIndices[r + 1][s], ringIndices[r + 1][next]);
                mesh.AddFace(ringIndices[r][s], ringIndices[r + 1][next], ringIndices[r][next]);
            }
        }

        // Bottom cap (last ring to pole)
        int lastRing = rings - 2;
        for (int s = 0; s < sides; s++)
            mesh.AddFace(ringIndices[lastRing][s], botPole, ringIndices[lastRing][(s + 1) % sides]);

        return mesh;
    }

    // ── Partial sphere (hemisphere or portion) ───────────────────────────

    /// <summary>
    /// Generate a partial sphere from polarStart to polarEnd (radians).
    /// 0 = north pole, PI = south pole.
    /// Returns an open mesh (no cap at the cut).
    /// </summary>
    public static IndexedTriangleSet PartialSphere(float radius, float polarStart, float polarEnd,
        int rings = 6, int sides = DEFAULT_SIDES)
    {
        var mesh = new IndexedTriangleSet();
        if (radius < 1e-6f || rings < 1) return mesh;

        // If starting from the pole, add pole vertex
        bool hasTopPole = polarStart < 0.01f;
        bool hasBotPole = polarEnd > MathF.PI - 0.01f;

        int topPoleIdx = -1;
        if (hasTopPole)
            topPoleIdx = mesh.AddVertex(new Vector3(0, radius, 0));

        int totalRings = rings;
        var ringIndices = new List<int[]>();

        for (int r = (hasTopPole ? 1 : 0); r <= totalRings - (hasBotPole ? 1 : 0); r++)
        {
            float t = (float)r / totalRings;
            float phi = polarStart + (polarEnd - polarStart) * t;
            float y = MathF.Cos(phi) * radius;
            float ringR = MathF.Sin(phi) * radius;
            var ring = new int[sides];

            for (int s = 0; s < sides; s++)
            {
                float theta = 2f * MathF.PI * s / sides;
                ring[s] = mesh.AddVertex(new Vector3(MathF.Cos(theta) * ringR, y, MathF.Sin(theta) * ringR));
            }
            ringIndices.Add(ring);
        }

        int botPoleIdx = -1;
        if (hasBotPole)
            botPoleIdx = mesh.AddVertex(new Vector3(0, -radius, 0));

        // Top cap from pole
        if (hasTopPole && ringIndices.Count > 0)
        {
            for (int s = 0; s < sides; s++)
                mesh.AddFace(topPoleIdx, ringIndices[0][s], ringIndices[0][(s + 1) % sides]);
        }

        // Strips between rings
        for (int r = 0; r < ringIndices.Count - 1; r++)
        {
            for (int s = 0; s < sides; s++)
            {
                int next = (s + 1) % sides;
                mesh.AddFace(ringIndices[r][s], ringIndices[r + 1][s], ringIndices[r + 1][next]);
                mesh.AddFace(ringIndices[r][s], ringIndices[r + 1][next], ringIndices[r][next]);
            }
        }

        // Bottom cap to pole
        if (hasBotPole && ringIndices.Count > 0)
        {
            int last = ringIndices.Count - 1;
            for (int s = 0; s < sides; s++)
                mesh.AddFace(ringIndices[last][s], botPoleIdx, ringIndices[last][(s + 1) % sides]);
        }

        return mesh;
    }

    // ── Pinhead (PrusaSlicer-style: pin sphere + tangent cone + back sphere) ──

    /// <summary>
    /// Generate a pinhead mesh: small sphere (pin) connected by tangent cone to large sphere (back).
    /// Oriented along -Y by default (pin at top, back at bottom).
    /// </summary>
    public static IndexedTriangleSet Pinhead(float rPin, float rBack, float width, int sides = DEFAULT_SIDES)
    {
        var mesh = new IndexedTriangleSet();
        float totalH = rPin + width + rBack;

        // Tangent angle phi that makes the cone tangent to both spheres
        float h = totalH;
        float phi = MathF.PI / 2f - MathF.Acos(Math.Clamp((rBack - rPin) / h, -1f, 1f));

        // Pin sphere (top): from north pole to (PI/2 + phi)
        var pinSphere = PartialSphere(rPin, 0, MathF.PI / 2f + phi, 4, sides);
        // Translate pin sphere to its position (top of the pinhead)
        for (int i = 0; i < pinSphere.Vertices.Count; i++)
            pinSphere.Vertices[i] += new Vector3(0, totalH - rPin, 0); // pin at top
        mesh.Merge(pinSphere);

        // Back sphere (bottom): from (PI/2 + phi) to south pole
        var backSphere = PartialSphere(rBack, MathF.PI / 2f + phi, MathF.PI, 4, sides);
        // Back sphere at bottom, centered at (0, rBack, 0)
        for (int i = 0; i < backSphere.Vertices.Count; i++)
            backSphere.Vertices[i] += new Vector3(0, rBack, 0);
        mesh.Merge(backSphere);

        // Connecting cone (stitch the two edge rings)
        // For simplicity, add a frustum between the sphere edges
        var cone = Frustum(
            rPin * MathF.Cos(phi), // top radius at tangent point
            rBack * MathF.Cos(phi), // bottom radius at tangent point
            width, // height of connecting section
            sides);
        // Position: between pin and back spheres
        float coneBottom = rBack + rBack * MathF.Sin(phi);
        for (int i = 0; i < cone.Vertices.Count; i++)
            cone.Vertices[i] += new Vector3(0, coneBottom, 0);
        mesh.Merge(cone);

        return mesh;
    }

    // ── Oriented frustum between two 3D points ───────────────────────────

    /// <summary>
    /// Generate a frustum connecting point A to point B with given radii.
    /// Properly rotated to align with the A→B direction.
    /// </summary>
    public static IndexedTriangleSet OrientedFrustum(Vector3 pointA, Vector3 pointB,
        float radiusA, float radiusB, int sides = DEFAULT_SIDES)
    {
        float height = Vector3.Distance(pointA, pointB);
        if (height < 1e-6f) return new IndexedTriangleSet();

        var mesh = Frustum(radiusA, radiusB, height, sides);

        // Rotate from default (Y-up) to actual direction
        var dir = Vector3.Normalize(pointB - pointA);
        var defaultDir = Vector3.UnitY;
        Quaternion rotation;
        if (Vector3.Dot(dir, defaultDir) < -0.999f)
            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        else if (Vector3.Dot(dir, defaultDir) > 0.999f)
            rotation = Quaternion.Identity;
        else
            rotation = QuaternionFromTo(defaultDir, dir);

        // Transform: rotate then translate to pointB (bottom of frustum)
        mesh.Transform(rotation, pointB);

        return mesh;
    }

    /// <summary>
    /// Generate a sphere at a specific position.
    /// </summary>
    public static IndexedTriangleSet OrientedSphere(Vector3 center, float radius,
        int rings = 8, int sides = DEFAULT_SIDES)
    {
        var mesh = Sphere(radius, rings, sides);
        mesh.Transform(Quaternion.Identity, center);
        return mesh;
    }

    // ── Pedestal (build plate base) ──────────────────────────────────────

    /// <summary>
    /// Generate a pedestal: truncated cone from pillar radius to base radius.
    /// Flat bottom at Z = baseZ.
    /// </summary>
    public static IndexedTriangleSet Pedestal(Vector3 position, float rTop, float rBottom,
        float height, int sides = DEFAULT_SIDES)
    {
        var mesh = Frustum(rTop, rBottom, height, sides);
        mesh.Transform(Quaternion.Identity, position - new Vector3(0, 0, 0)); // position at base
        return mesh;
    }

    // ── Helper ───────────────────────────────────────────────────────────

    private static Quaternion QuaternionFromTo(Vector3 from, Vector3 to)
    {
        var cross = Vector3.Cross(from, to);
        float dot = Vector3.Dot(from, to);
        float w = 1f + dot;
        if (w < 1e-6f)
        {
            // Nearly opposite: pick perpendicular axis
            var perp = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            cross = Vector3.Cross(from, perp);
            return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 0));
        }
        return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, w));
    }
}
