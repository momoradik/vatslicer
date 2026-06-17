using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes cross-section area at each Z height using triangle-plane intersection.
/// Faster than full polygon cross-section — uses projected triangle area contribution.
/// Useful for volume estimation, peel force profiling, and density analysis.
/// </summary>
public static class CrossSectionAreaCalculator
{
    public sealed record AreaProfile
    {
        public required float[] AreasPerLayer { get; init; }
        public required float LayerHeightMm { get; init; }
        public required float MeshMinZ { get; init; }
        public required int LayerCount { get; init; }
        public required float MaxAreaMm2 { get; init; }
        public required float TotalVolumeMm3 { get; init; }
    }

    public static AreaProfile Compute(StlMesh mesh, float layerHeightMm = 0.1f)
    {
        float minZ = mesh.Min.Z;
        float maxZ = mesh.Max.Z;
        int layerCount = (int)Math.Ceiling((maxZ - minZ) / layerHeightMm);
        if (layerCount <= 0)
            return new AreaProfile { AreasPerLayer = Array.Empty<float>(), LayerHeightMm = layerHeightMm,
                MeshMinZ = minZ, LayerCount = 0, MaxAreaMm2 = 0, TotalVolumeMm3 = 0 };

        var areas = new float[layerCount];

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3];
            var v1 = mesh.Vertices[t * 3 + 1];
            var v2 = mesh.Vertices[t * 3 + 2];

            float ax = v1.X - v0.X, ay = v1.Y - v0.Y;
            float bx = v2.X - v0.X, by = v2.Y - v0.Y;
            float projArea = Math.Abs(ax * by - ay * bx) * 0.5f;

            float triMinZ = Math.Min(v0.Z, Math.Min(v1.Z, v2.Z));
            float triMaxZ = Math.Max(v0.Z, Math.Max(v1.Z, v2.Z));

            int startLayer = Math.Max(0, (int)((triMinZ - minZ) / layerHeightMm));
            int endLayer = Math.Min(layerCount - 1, (int)((triMaxZ - minZ) / layerHeightMm));

            // Distribute area across spanned layers
            int spanLayers = endLayer - startLayer + 1;
            float areaPerLayer = spanLayers > 0 ? projArea / spanLayers : projArea;
            for (int i = startLayer; i <= endLayer; i++)
                areas[i] += areaPerLayer;
        }

        float maxArea = areas.Max();
        float totalVol = areas.Sum() * layerHeightMm;

        return new AreaProfile
        {
            AreasPerLayer = areas,
            LayerHeightMm = layerHeightMm,
            MeshMinZ = minZ,
            LayerCount = layerCount,
            MaxAreaMm2 = maxArea,
            TotalVolumeMm3 = totalVol,
        };
    }
}
