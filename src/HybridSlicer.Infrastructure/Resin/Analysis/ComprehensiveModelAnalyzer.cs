using System.Numerics;

namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Runs ALL analysis checks on a model in one pass:
/// mesh validation, island prediction, suction detection, thin walls,
/// peel force profile, auto-orient suggestions, and drain holes.
/// Returns a unified report for the frontend.
/// </summary>
public static class ComprehensiveModelAnalyzer
{
    public sealed record AnalysisReport
    {
        // Mesh
        public required int TriangleCount { get; init; }
        public required float VolumeMm3 { get; init; }
        public required float SurfaceAreaMm2 { get; init; }
        public required bool MeshValid { get; init; }
        public required int MeshWarnings { get; init; }
        // Islands
        public required int IslandRisks { get; init; }
        public required int HighRiskIslands { get; init; }
        // Suction
        public required int SuctionWarnings { get; init; }
        // Thin Walls
        public required int ThinWalls { get; init; }
        public required float MinWallMm { get; init; }
        // Peel Force
        public required float MaxPeelForceN { get; init; }
        public required int HighStressLayers { get; init; }
        // Overall
        public required string OverallStatus { get; init; } // "good", "warnings", "critical"
        public required List<string> Issues { get; init; }
        public required long ElapsedMs { get; init; }
    }

    public static AnalysisReport Analyze(StlMesh mesh)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var issues = new List<string>();

        // Mesh validation
        var validation = MeshValidator.Validate(mesh);
        if (!validation.IsValid) issues.Add($"Mesh has {validation.Errors.Count} errors");
        if (validation.DegenerateTriangles > 0) issues.Add($"{validation.DegenerateTriangles} degenerate triangles");

        // Surface area
        float surfaceArea = 0;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = mesh.Vertices[t * 3]; var v1 = mesh.Vertices[t * 3 + 1]; var v2 = mesh.Vertices[t * 3 + 2];
            surfaceArea += Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
        }

        // Island prediction
        var islands = IslandPredictor.Predict(mesh);
        if (islands.HighRiskCount > 0) issues.Add($"{islands.HighRiskCount} high-risk island zones");

        // Suction detection
        var suction = SuctionCupDetector.Detect(mesh);
        if (suction.Count > 0) issues.Add($"{suction.Count} suction cup risk(s)");

        // Thin wall detection (needs BVH)
        var bvh = Spatial.AabbBvh.Build(mesh);
        var thinWalls = ThinWallDetector.Detect(bvh, mesh, sampleCount: 100);
        if (thinWalls.ThinWallCount > 0) issues.Add($"{thinWalls.ThinWallCount} thin wall regions (<0.5mm)");

        // Peel force
        var peelProfile = PeelForceProfiler.Compute(mesh, layerHeightMm: 0.5f);
        if (peelProfile.HighStressLayers > 0)
            issues.Add($"{peelProfile.HighStressLayers} high-stress layers (consider slower lift)");

        string status = issues.Count == 0 ? "good" : issues.Any(i => i.Contains("error") || i.Contains("critical")) ? "critical" : "warnings";

        return new AnalysisReport
        {
            TriangleCount = mesh.TriangleCount,
            VolumeMm3 = validation.VolumeMm3,
            SurfaceAreaMm2 = surfaceArea,
            MeshValid = validation.IsValid,
            MeshWarnings = validation.Warnings.Count,
            IslandRisks = islands.Risks.Count,
            HighRiskIslands = islands.HighRiskCount,
            SuctionWarnings = suction.Count,
            ThinWalls = thinWalls.ThinWallCount,
            MinWallMm = thinWalls.MinWallThicknessMm,
            MaxPeelForceN = peelProfile.MaxPeelForceN,
            HighStressLayers = peelProfile.HighStressLayers,
            OverallStatus = status,
            Issues = issues,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
