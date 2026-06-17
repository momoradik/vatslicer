using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes the center of gravity (centroid) of a triangle mesh.
/// Uses the signed tetrahedron volume method for accurate volumetric centroid.
/// If the mesh isn't watertight, falls back to vertex-averaged centroid.
/// </summary>
public static class CenterOfGravityCalculator
{
    public sealed record CogResult
    {
        public required Vector3 CenterOfGravity { get; init; }
        public required float VolumeMm3 { get; init; }
        public required bool IsWatertight { get; init; }
        public required Vector3 BoundingBoxCenter { get; init; }
        /// <summary>Distance from CoG to bounding box center. Large values indicate asymmetric mass.</summary>
        public required float CogOffsetMm { get; init; }
    }

    public static CogResult Compute(StlMesh mesh)
    {
        var bbCenter = (mesh.Min + mesh.Max) * 0.5f;
        float totalVolume = 0;
        var weightedSum = Vector3.Zero;

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

            // Signed volume of tetrahedron formed with origin
            float vol = Vector3.Dot(v0, Vector3.Cross(v1, v2)) / 6f;
            totalVolume += vol;
            // Centroid of tetrahedron
            var centroid = (v0 + v1 + v2) / 4f;
            weightedSum += centroid * vol;
        }

        Vector3 cog;
        bool watertight;
        if (Math.Abs(totalVolume) > 0.001f)
        {
            cog = weightedSum / totalVolume;
            watertight = true;
        }
        else
        {
            // Fallback: vertex average
            var sum = Vector3.Zero;
            for (int i = 0; i < mesh.Vertices.Length; i++)
                sum += mesh.Vertices[i];
            cog = sum / mesh.Vertices.Length;
            watertight = false;
        }

        return new CogResult
        {
            CenterOfGravity = cog,
            VolumeMm3 = Math.Abs(totalVolume),
            IsWatertight = watertight,
            BoundingBoxCenter = bbCenter,
            CogOffsetMm = Vector3.Distance(cog, bbCenter),
        };
    }
}
