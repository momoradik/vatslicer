using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Evaluates multiple mesh orientations and picks the one with minimum overhang area.
///
/// Inspired by PrusaSlicer's auto-orient: tests 36 candidate orientations
/// (6 axis-aligned faces + 30 intermediate rotations at 15-degree increments)
/// and scores each by overhang area and model height.
///
/// Lower score = better orientation for printing with fewer supports.
/// </summary>
public static class AutoOrientOptimizer
{
    /// <summary>
    /// Result of evaluating a single orientation.
    /// </summary>
    public sealed record OrientResult
    {
        /// <summary>Rotation quaternion to apply to the mesh for this orientation.</summary>
        public required Quaternion Rotation { get; init; }
        /// <summary>Total overhang area in mm^2 (triangles facing downward beyond threshold).</summary>
        public required float OverhangAreaMm2 { get; init; }
        /// <summary>Estimated support material volume in mL.</summary>
        public required float SupportVolumeMl { get; init; }
        /// <summary>Estimated number of support pillars needed.</summary>
        public required int EstimatedSupports { get; init; }
        /// <summary>Composite score — lower is better.</summary>
        public required float Score { get; init; }
        /// <summary>Human-readable description of this orientation.</summary>
        public required string Description { get; init; }
    }

    /// <summary>
    /// Configuration for the orientation optimizer.
    /// </summary>
    public sealed record OrientConfig
    {
        /// <summary>Number of candidate orientations to evaluate (default 36).</summary>
        public int CandidateCount { get; init; } = 36;
        /// <summary>Coarse layer height for support volume estimation (mm).</summary>
        public float LayerHeightMm { get; init; } = 1.0f;
    }

    // Cosine of 45-degree overhang threshold: faces with normal Z < this are overhangs.
    private const float OverhangCosThreshold = -0.7071f; // cos(135deg) = -cos(45deg)

    // Default support spacing for estimating support count.
    private const float DefaultSupportSpacingMm = 5.0f;

    /// <summary>
    /// Evaluate candidate orientations and return the top N sorted by score (best first).
    /// </summary>
    /// <param name="mesh">The mesh to evaluate.</param>
    /// <param name="config">Optional configuration; defaults to 36 candidates.</param>
    /// <param name="topN">How many top results to return.</param>
    /// <returns>List of orientation results sorted by score ascending (best first).</returns>
    public static List<OrientResult> Evaluate(StlMesh mesh, OrientConfig? config = null, int topN = 5)
    {
        config ??= new OrientConfig();

        var candidates = GenerateCandidateRotations(config.CandidateCount);
        var results = new List<OrientResult>(candidates.Count);

        foreach (var (rotation, description) in candidates)
        {
            var result = EvaluateOrientation(mesh, rotation, description, config.LayerHeightMm);
            results.Add(result);
        }

        // Sort by score ascending (lower = better)
        results.Sort((a, b) => a.Score.CompareTo(b.Score));

        // Return top N
        if (results.Count > topN)
            results = results.GetRange(0, topN);

        return results;
    }

    // ── Candidate generation ────────────────────────────────────────────

    private static List<(Quaternion rotation, string description)> GenerateCandidateRotations(int targetCount)
    {
        var candidates = new List<(Quaternion rotation, string description)>();

        // 6 axis-aligned faces: each face pointing down (toward build plate)
        // Identity: Z-down is the default (no rotation)
        candidates.Add((Quaternion.Identity, "Original (+Z up)"));

        // +X face down: rotate -90 around Y
        candidates.Add((Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f), "+X face down"));

        // -X face down: rotate +90 around Y
        candidates.Add((Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), "-X face down"));

        // +Y face down: rotate +90 around X
        candidates.Add((Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f), "+Y face down"));

        // -Y face down: rotate -90 around X
        candidates.Add((Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f), "-Y face down"));

        // -Z face down (upside down): rotate 180 around X
        candidates.Add((Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI), "-Z face down (flipped)"));

        // Intermediate rotations: 15-degree increments around each axis
        // This gives 30 more candidates (10 per axis, skipping 0/90/180/270 which overlap axis-aligned)
        float step = MathF.PI / 12f; // 15 degrees

        for (int i = 1; i < 12; i++)
        {
            float angle = i * step;

            // Skip angles that duplicate axis-aligned faces (0, 90, 180, 270)
            if (i == 6) continue; // 90 degrees (already covered)

            if (candidates.Count >= targetCount) break;

            // Around X axis
            candidates.Add((
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle),
                $"X-axis {i * 15}deg"));

            if (candidates.Count >= targetCount) break;

            // Around Y axis
            candidates.Add((
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle),
                $"Y-axis {i * 15}deg"));

            if (candidates.Count >= targetCount) break;

            // Around Z axis (only useful combined with other rotations, but included
            // for coverage of asymmetric models)
            if (i % 3 == 0) // every 45 degrees around Z to limit count
            {
                // Compound rotation: tilt + Z-rotation
                var compound = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f) *
                               Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
                candidates.Add((
                    compound,
                    $"Compound tilt+Z {i * 15}deg"));

                if (candidates.Count >= targetCount) break;
            }
        }

        return candidates;
    }

    // ── Per-orientation evaluation ──────────────────────────────────────

    private static OrientResult EvaluateOrientation(
        StlMesh mesh, Quaternion rotation, string description, float layerHeight)
    {
        float overhangArea = 0f;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;

        // Transform vertices temporarily (no allocation of new StlMesh)
        // Process each triangle: rotate vertices, compute face normal, check overhang
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = Vector3.Transform(mesh.Vertices[t * 3], rotation);
            var v1 = Vector3.Transform(mesh.Vertices[t * 3 + 1], rotation);
            var v2 = Vector3.Transform(mesh.Vertices[t * 3 + 2], rotation);

            // Track Z extent
            float triMinZ = MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z));
            float triMaxZ = MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z));
            if (triMinZ < minZ) minZ = triMinZ;
            if (triMaxZ > maxZ) maxZ = triMaxZ;

            // Compute face normal
            var edge1 = v1 - v0;
            var edge2 = v2 - v0;
            var cross = Vector3.Cross(edge1, edge2);
            float crossLen = cross.Length();
            if (crossLen < 1e-10f) continue; // degenerate triangle

            float normalZ = cross.Z / crossLen;

            // Overhang check: face normal Z component below threshold
            // (normal pointing downward = overhang needing support)
            if (normalZ < OverhangCosThreshold)
            {
                // Triangle area = 0.5 * |cross product|
                float triArea = crossLen * 0.5f;
                overhangArea += triArea;
            }
        }

        float modelHeight = maxZ - minZ;
        if (modelHeight < 0.01f) modelHeight = 0.01f;

        // Estimate support count: overhangArea / (spacing^2)
        float spacingSq = DefaultSupportSpacingMm * DefaultSupportSpacingMm;
        int estimatedSupports = (int)MathF.Ceiling(overhangArea / spacingSq);

        // Estimate support volume: each support is a frustum from overhang down to base
        // Approximate as cylinder: V = pi * r^2 * h, with r ~ 0.5mm pillar, avg height ~ modelHeight/2
        float avgSupportHeight = modelHeight * 0.5f;
        float pillarRadius = 0.5f;
        float perSupportVolume = MathF.PI * pillarRadius * pillarRadius * avgSupportHeight;
        float totalVolumeMm3 = estimatedSupports * perSupportVolume;
        float supportVolumeMl = totalVolumeMm3 / 1000f;

        // Score: weighted combination of overhang area and model height
        // Prefer orientations with low overhang AND low height (faster print, shorter supports)
        float score = overhangArea * 1.0f + modelHeight * 0.5f;

        return new OrientResult
        {
            Rotation = rotation,
            OverhangAreaMm2 = overhangArea,
            SupportVolumeMl = supportVolumeMl,
            EstimatedSupports = estimatedSupports,
            Score = score,
            Description = description,
        };
    }
}
