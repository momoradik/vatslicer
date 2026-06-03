using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Meshing;

/// <summary>
/// Generates individual mini-raft pads under each support base.
///
/// Mini rafts improve build plate adhesion and part removal by providing:
/// - A thin disc slightly larger than the support base for better adhesion
/// - Chamfered edges to reduce peel stress and enable easier removal
/// - Low profile (0.3mm default) so they peel off cleanly
///
/// Unlike a full-plate raft that spans the entire build area, mini rafts are
/// individual pads — one per support. This uses less resin and makes post-processing
/// easier since each raft pad separates independently.
///
/// Geometry: a flat disc with a slight chamfer on the bottom edge.
/// The chamfer angle creates a smooth transition from the build plate to the
/// raft pad, reducing stress concentration during peel.
/// </summary>
public static class MiniRaft
{
    /// <summary>
    /// Default margin beyond the support base radius (mm).
    /// </summary>
    public const float DefaultMarginMm = 1.5f;

    /// <summary>
    /// Default raft thickness (mm). Thin for easy removal.
    /// </summary>
    public const float DefaultThicknessMm = 0.3f;

    /// <summary>
    /// Generate a mini-raft disc under a support base.
    /// </summary>
    /// <param name="baseCenter">Center of the support base on the build plate.</param>
    /// <param name="baseRadius">Radius of the support base (mm).</param>
    /// <param name="raftMarginMm">Extra radius beyond the base (mm).</param>
    /// <param name="raftThicknessMm">Thickness of the raft disc (mm).</param>
    /// <param name="sides">Number of circumferential segments.</param>
    /// <returns>Indexed triangle mesh of the mini-raft disc.</returns>
    public static IndexedTriangleSet Generate(
        Vector3 baseCenter, float baseRadius,
        float raftMarginMm = DefaultMarginMm,
        float raftThicknessMm = DefaultThicknessMm,
        int sides = 24)
    {
        var mesh = new IndexedTriangleSet();

        float raftRadius = baseRadius + raftMarginMm;
        float chamferHeight = raftThicknessMm * 0.4f; // 40% of thickness is chamfer
        float bodyHeight = raftThicknessMm - chamferHeight;

        // The raft sits below the base center (on the build plate).
        // Z layout: baseCenter.Z is the top of the raft.
        // Bottom of raft = baseCenter.Z - raftThicknessMm
        // Chamfer ring = baseCenter.Z - bodyHeight
        float topZ = baseCenter.Z;
        float chamferZ = topZ - bodyHeight;
        float botZ = topZ - raftThicknessMm;

        // Chamfer radius: slightly smaller at the very bottom for easy peel
        float chamferRadius = raftRadius - chamferHeight * 0.5f;
        if (chamferRadius < baseRadius) chamferRadius = baseRadius;

        // Generate vertex rings
        var topRing = new int[sides];         // full radius at top
        var chamferRing = new int[sides];     // full radius at chamfer line
        var botRing = new int[sides];         // chamfered (smaller) radius at bottom

        for (int i = 0; i < sides; i++)
        {
            float angle = 2f * MathF.PI * i / sides;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            topRing[i] = mesh.AddVertex(new Vector3(
                baseCenter.X + cos * raftRadius,
                baseCenter.Y + sin * raftRadius,
                topZ));

            chamferRing[i] = mesh.AddVertex(new Vector3(
                baseCenter.X + cos * raftRadius,
                baseCenter.Y + sin * raftRadius,
                chamferZ));

            botRing[i] = mesh.AddVertex(new Vector3(
                baseCenter.X + cos * chamferRadius,
                baseCenter.Y + sin * chamferRadius,
                botZ));
        }

        // Top face (disc cap)
        int topCenter = mesh.AddVertex(new Vector3(baseCenter.X, baseCenter.Y, topZ));
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            mesh.AddFace(topCenter, topRing[next], topRing[i]);
        }

        // Upper wall (vertical section: top ring to chamfer ring)
        if (bodyHeight > 1e-4f)
        {
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                mesh.AddFace(topRing[i], chamferRing[i], chamferRing[next]);
                mesh.AddFace(topRing[i], chamferRing[next], topRing[next]);
            }
        }

        // Chamfer wall (angled section: chamfer ring to bottom ring)
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            if (bodyHeight > 1e-4f)
            {
                mesh.AddFace(chamferRing[i], botRing[i], botRing[next]);
                mesh.AddFace(chamferRing[i], botRing[next], chamferRing[next]);
            }
            else
            {
                // No body section, go directly from top to bottom
                mesh.AddFace(topRing[i], botRing[i], botRing[next]);
                mesh.AddFace(topRing[i], botRing[next], topRing[next]);
            }
        }

        // Bottom face (disc cap with reversed winding for downward normal)
        int botCenter = mesh.AddVertex(new Vector3(baseCenter.X, baseCenter.Y, botZ));
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            mesh.AddFace(botCenter, botRing[i], botRing[next]);
        }

        return mesh;
    }
}
