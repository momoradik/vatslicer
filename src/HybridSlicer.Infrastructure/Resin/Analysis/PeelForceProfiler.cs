using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes per-layer peel force profile for a model.
/// Useful for identifying high-stress layers where supports are most critical
/// and where lift speed should be reduced.
/// </summary>
public static class PeelForceProfiler
{
    public sealed record LayerForce
    {
        public required float ZMm { get; init; }
        public required float CrossSectionAreaMm2 { get; init; }
        public required float PeelForceN { get; init; }
        public required bool IsHighStress { get; init; }
    }

    public sealed record ForceProfile
    {
        public required List<LayerForce> Layers { get; init; }
        public required float MaxPeelForceN { get; init; }
        public required float MaxPeelForceZ { get; init; }
        public required float AvgPeelForceN { get; init; }
        public required int HighStressLayers { get; init; }
    }

    /// <summary>
    /// Compute peel force at each layer height.
    /// </summary>
    public static ForceProfile Compute(
        StlMesh mesh,
        float layerHeightMm = 0.05f,
        float pAdhNPerMm2 = SupportSizer.P_ADH_DEFAULT)
    {
        float meshMinZ = mesh.Min.Z;
        float meshMaxZ = mesh.Max.Z;
        int layerCount = (int)Math.Ceiling((meshMaxZ - meshMinZ) / layerHeightMm);
        if (layerCount <= 0) return new ForceProfile
        {
            Layers = new(), MaxPeelForceN = 0, MaxPeelForceZ = 0, AvgPeelForceN = 0, HighStressLayers = 0,
        };

        // Approximate cross-section area per layer
        var areas = new float[layerCount];
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3]; var v1 = mesh.Vertices[t * 3 + 1]; var v2 = mesh.Vertices[t * 3 + 2];
            float ax = v1.X - v0.X, ay = v1.Y - v0.Y;
            float bx = v2.X - v0.X, by = v2.Y - v0.Y;
            float projArea = Math.Abs(ax * by - ay * bx) * 0.5f;
            float triMinZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
            float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));
            int s = Math.Max(0, (int)((triMinZ - meshMinZ) / layerHeightMm));
            int e = Math.Min(layerCount - 1, (int)((triMaxZ - meshMinZ) / layerHeightMm));
            for (int i = s; i <= e; i++) areas[i] += projArea;
        }

        // Normalize and compute force
        float maxForce = 0; float maxZ = 0; float totalForce = 0;
        var layers = new List<LayerForce>(layerCount);
        float avgArea = areas.Average();
        float highThreshold = avgArea * 2f; // layers with 2x avg area are high-stress

        for (int i = 0; i < layerCount; i++)
        {
            float z = meshMinZ + (i + 0.5f) * layerHeightMm;
            float force = areas[i] * pAdhNPerMm2;
            bool isHigh = areas[i] > highThreshold;
            if (force > maxForce) { maxForce = force; maxZ = z; }
            totalForce += force;
            layers.Add(new LayerForce
            {
                ZMm = z, CrossSectionAreaMm2 = areas[i], PeelForceN = force, IsHighStress = isHigh,
            });
        }

        return new ForceProfile
        {
            Layers = layers,
            MaxPeelForceN = maxForce,
            MaxPeelForceZ = maxZ,
            AvgPeelForceN = layerCount > 0 ? totalForce / layerCount : 0,
            HighStressLayers = layers.Count(l => l.IsHighStress),
        };
    }
}
