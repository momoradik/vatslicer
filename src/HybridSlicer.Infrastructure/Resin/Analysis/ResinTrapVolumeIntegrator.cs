using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Computes exact trapped resin volume by integrating cross-section area
/// differences between closing contours. More accurate than DrainHolePlacer's
/// heuristic — used for precise resin waste estimation.
/// </summary>
public static class ResinTrapVolumeIntegrator
{
    public sealed record TrapRegion
    {
        public required float StartZ { get; init; }
        public required float EndZ { get; init; }
        public required float VolumeMm3 { get; init; }
        public required float MaxAreaMm2 { get; init; }
        public required Vector3 Centroid { get; init; }
    }

    public sealed record IntegrationResult
    {
        public required List<TrapRegion> Traps { get; init; }
        public required float TotalTrappedVolumeMm3 { get; init; }
        public required float TotalTrappedVolumeMl { get; init; }
    }

    public static IntegrationResult Compute(StlMesh mesh, float layerHeightMm = 0.5f, float minTrapVolumeMm3 = 10f)
    {
        var areaProfile = CrossSectionAreaCalculator.Compute(mesh, layerHeightMm);
        var traps = new List<TrapRegion>();
        float totalVol = 0;

        // Scan top-down for area decreases (pocket closing = trap forming)
        bool inTrap = false;
        float trapStartZ = 0, trapMaxArea = 0, trapVolume = 0;
        float trapCz = 0;
        float meshCx = (mesh.Min.X + mesh.Max.X) / 2f;
        float meshCy = (mesh.Min.Y + mesh.Max.Y) / 2f;

        for (int i = areaProfile.LayerCount - 2; i >= 0; i--)
        {
            float currArea = areaProfile.AreasPerLayer[i];
            float aboveArea = areaProfile.AreasPerLayer[i + 1];
            float z = areaProfile.MeshMinZ + (i + 0.5f) * layerHeightMm;

            if (aboveArea > currArea * 1.3f && !inTrap)
            {
                // Area above is larger → pocket opening (scanning top-down)
                inTrap = true;
                trapStartZ = z + layerHeightMm;
                trapMaxArea = aboveArea;
                trapVolume = 0;
                trapCz = z;
            }

            if (inTrap)
            {
                float diff = aboveArea - currArea;
                if (diff > 0)
                    trapVolume += diff * layerHeightMm;

                if (currArea >= aboveArea * 0.9f || i == 0)
                {
                    // Pocket closed
                    if (trapVolume >= minTrapVolumeMm3)
                    {
                        traps.Add(new TrapRegion
                        {
                            StartZ = z,
                            EndZ = trapStartZ,
                            VolumeMm3 = trapVolume,
                            MaxAreaMm2 = trapMaxArea,
                            Centroid = new Vector3(meshCx, meshCy, (z + trapStartZ) / 2f),
                        });
                        totalVol += trapVolume;
                    }
                    inTrap = false;
                }
            }
        }

        return new IntegrationResult
        {
            Traps = traps,
            TotalTrappedVolumeMm3 = totalVol,
            TotalTrappedVolumeMl = totalVol / 1000f,
        };
    }
}
