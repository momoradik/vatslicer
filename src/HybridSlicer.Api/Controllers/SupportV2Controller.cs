using HybridSlicer.Application.Interfaces.Repositories;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;
using HybridSlicer.Infrastructure.Resin.Meshing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;
using HybridSlicer.Infrastructure.Resin.Slicing;
using Microsoft.AspNetCore.Mvc;

namespace HybridSlicer.Api.Controllers;

/// <summary>
/// V2 support engine API — production-grade, BVH-accelerated, structurally validated.
/// </summary>
[ApiController]
[Route("api/support-v2")]
public sealed class SupportV2Controller : ControllerBase
{
    private readonly IMachineProfileRepository _printerRepo;
    public SupportV2Controller(IMachineProfileRepository printerRepo) => _printerRepo = printerRepo;

    /// <summary>
    /// Generate supports using the V2 engine (full pipeline with validation).
    /// Returns legacy segment format for frontend compatibility plus validation data.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Generate(
        [FromForm] IFormFile stlFile,
        [FromForm] string? printerId = null,
        [FromForm] string orientation = "BottomUp",
        [FromForm] double overhangAngleDeg = 45,
        [FromForm] double density = 0.5,
        [FromForm] float pinRadius = 0.2f,
        [FromForm] float backRadius = 0.5f,
        [FromForm] float pillarRadius = 0.5f,
        [FromForm] float baseRadius = 2.0f,
        [FromForm] float wideningFactor = 0.01f,
        [FromForm] bool enableInterconnections = true,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var orient = PrinterOrientation.BottomUp;
        if (!string.IsNullOrEmpty(printerId) && Guid.TryParse(printerId, out var pid))
        {
            var printer = await _printerRepo.GetByIdAsync(pid, ct);
            if (printer is not null) orient = printer.Orientation;
        }
        else if (Enum.TryParse<PrinterOrientation>(orientation, true, out var o)) orient = o;

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = orient,
            OverhangAngleDeg = (float)overhangAngleDeg,
            DensityFactor = (float)density,
            PinRadiusMm = pinRadius,
            BackRadiusMm = backRadius,
            PillarRadiusMm = pillarRadius,
            BaseRadiusMm = baseRadius,
            WideningFactor = wideningFactor,
            EnableInterconnections = enableInterconnections,
        });

        return Ok(new
        {
            // Core stats
            engine = "v2",
            totalSupports = result.TotalSupports,
            validSupports = result.ValidSupports,
            rejectedCollisions = result.RejectedCollisions,
            totalVolumeMm3 = result.TotalSupportVolumeMm3,
            totalVolumeMl = result.TotalSupportVolumeMm3 / 1000f,
            totalWeightG = result.TotalSupportVolumeMm3 * 1.1e-3f, // ~1.1 g/cm³ resin density
            estimatedCostUsd = result.TotalSupportVolumeMm3 / 1000f * 0.05f, // ~$0.05/ml typical resin
            supportLayerCount = result.SupportLayerCount,
            totalCrossSectionAreaMm2 = result.TotalSupportCrossSectionArea,
            elapsedMs = result.TotalElapsedMs,
            orientation = orient.ToString(),
            // Centering offset — frontend must apply same offset to align supports with model
            meshOffset = new { x = result.MeshCenteringOffset.X, y = result.MeshCenteringOffset.Y, z = result.MeshCenteringOffset.Z },

            // Validation summary
            validation = new
            {
                collision = new
                {
                    result.CollisionResult.CollisionFreeSupports,
                    result.CollisionResult.CollidingSupports,
                    result.CollisionResult.TotalCollisionPoints,
                },
                structural = new
                {
                    result.StructuralResult.PassedBuckling,
                    result.StructuralResult.FailedBuckling,
                    result.StructuralResult.PassedTensile,
                    result.StructuralResult.FailedTensile,
                    result.StructuralResult.OverhangRegionsCovered,
                    result.StructuralResult.OverhangRegionsUncovered,
                    result.StructuralResult.MinSafetyFactor,
                    result.StructuralResult.AvgSafetyFactor,
                    result.StructuralResult.ManifoldErrors,
                },
                issues = result.CollisionResult.Issues
                    .Select(i => new { i.SupportId, Element = i.Element, i.Description })
                    .Concat(result.StructuralResult.Issues.Select(i => new
                        { i.SupportId, Element = i.Category, i.Description }))
                    .Take(50), // limit response size
            },

            // Mesh info
            mesh = new
            {
                vertices = result.MergeInfo.WeldedVertices,
                faces = result.MergeInfo.FinalFaces,
                nonManifoldEdges = result.MergeInfo.NonManifoldEdges,
                // Base64-encoded binary STL for direct Three.js rendering
                // (avoids a second HTTP request for the mesh)
                stlBase64 = result.SupportMesh.FaceCount > 0
                    ? Convert.ToBase64String(result.SupportMesh.ToStlBinary())
                    : null,
            },

            // Legacy format for existing frontend rendering
            supportCount = result.LegacySupports.Count,
            braceCount = result.LegacyCrossBraces.Count,
            supports = result.LegacySupports.Select(s => new
            {
                s.Id, s.Type,
                contactX = s.ContactX, contactY = s.ContactY, contactZ = s.ContactZ,
                normalX = s.NormalX, normalY = s.NormalY, normalZ = s.NormalZ,
                baseX = s.BaseX, baseY = s.BaseY, baseZ = s.BaseZ,
                preset = new { s.Preset.Name, s.Preset.TipDiameterMm, s.Preset.ShaftDiameterMm, s.Preset.BaseDiameterMm },
                segments = s.Segments.Select(seg => new
                {
                    seg.Part, seg.X1, seg.Y1, seg.Z1, seg.R1, seg.X2, seg.Y2, seg.Z2, seg.R2,
                }),
            }),
            crossBraces = result.LegacyCrossBraces.Select(b => new
            {
                b.SupportA, b.SupportB, b.X1, b.Y1, b.Z1, b.X2, b.Y2, b.Z2, b.Diameter,
            }),
        });
    }

    /// <summary>
    /// Download the watertight support mesh as binary STL.
    /// </summary>
    [HttpPost("mesh")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> DownloadSupportMesh(
        [FromForm] IFormFile stlFile,
        [FromForm] string orientation = "BottomUp",
        [FromForm] double overhangAngleDeg = 45,
        [FromForm] double density = 0.5,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var orient = PrinterOrientation.BottomUp;
        if (Enum.TryParse<PrinterOrientation>(orientation, true, out var o)) orient = o;

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = orient,
            OverhangAngleDeg = (float)overhangAngleDeg,
            DensityFactor = (float)density,
        });

        var stlBytes = result.SupportMesh.ToStlBinary();
        return File(stlBytes, "application/octet-stream", "supports.stl");
    }

    /// <summary>
    /// Get a support-only layer image at a specific Z height (for preview).
    /// </summary>
    [HttpPost("layer-preview")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> LayerPreview(
        [FromForm] IFormFile stlFile,
        [FromForm] float z,
        [FromForm] int resX = 1920,
        [FromForm] int resY = 1080,
        [FromForm] float buildWidthMm = 192,
        [FromForm] float buildDepthMm = 120,
        [FromForm] string orientation = "BottomUp",
        [FromForm] double density = 0.5,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var orient = PrinterOrientation.BottomUp;
        if (Enum.TryParse<PrinterOrientation>(orientation, true, out var o)) orient = o;

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = orient,
            DensityFactor = (float)density,
        });

        var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, z);
        var pngBytes = SupportSliceIntegrator.RenderSupportOnlyLayer(
            circles, resX, resY, buildWidthMm, buildDepthMm);

        return File(pngBytes, "image/png", $"support_layer_z{z:F2}.png");
    }

    /// <summary>
    /// Run full validation with expensive BVH beam-cast collision detection.
    /// Use this for final verification before printing — slower but thorough.
    /// </summary>
    [HttpPost("validate")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> FullValidation(
        [FromForm] IFormFile stlFile,
        [FromForm] string orientation = "BottomUp",
        [FromForm] double overhangAngleDeg = 45,
        [FromForm] double density = 0.5,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var orient = PrinterOrientation.BottomUp;
        if (Enum.TryParse<PrinterOrientation>(orientation, true, out var o)) orient = o;

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = orient,
            OverhangAngleDeg = (float)overhangAngleDeg,
            DensityFactor = (float)density,
        });

        // Run collision validation — sample-based for speed on large models
        int maxSample = Math.Min(30, result.Pinheads.Count);
        var collisionResult = CollisionValidator.ValidateAll(
            result.Pinheads.Take(maxSample).ToList(),
            result.Routes.Take(maxSample).ToList(),
            result.Interconnections.Take(Math.Min(50, result.Interconnections.Count)).ToList(),
            result.Bvh);

        // Run structural validation (skip expensive overhang re-analysis — use empty regions)
        var coverageGrid = new SpatialGrid<string>(8f);
        foreach (var pt in result.Points)
            coverageGrid.Insert(pt.Position, pt.Id);

        var pinheadLookup = result.Pinheads.ToDictionary(p => p.id, p => p.pinhead);
        var structuralResult = StructuralValidator.Validate(
            result.Routes.Select(r => (r.id, r.route,
                pinheadLookup.TryGetValue(r.id, out var ph) ? ph.ContactPoint.Z : 0f)).ToList(),
            new(), coverageGrid, result.SupportMesh, 2.0f);

        // Check mesh manifoldness (skip for large meshes — too slow)
        int nonManifold = result.SupportMesh.FaceCount < 100000
            ? MeshMerger.CountNonManifoldEdges(result.SupportMesh) : -1;

        return Ok(new
        {
            engine = "v2-full-validation",
            elapsedMs = result.TotalElapsedMs,
            supports = result.ValidSupports,

            collision = new
            {
                collisionResult.TotalSupportsChecked,
                collisionResult.CollisionFreeSupports,
                collisionResult.CollidingSupports,
                collisionResult.TotalCollisionPoints,
                collisionResult.ElapsedMs,
                issues = collisionResult.Issues.Take(50).Select(i => new
                {
                    i.SupportId, i.Element, i.Description,
                    x = i.CollisionPoint.X, y = i.CollisionPoint.Y, z = i.CollisionPoint.Z,
                    i.PenetrationDepth,
                }),
            },

            structural = new
            {
                structuralResult.PassedBuckling, structuralResult.FailedBuckling,
                structuralResult.PassedTensile, structuralResult.FailedTensile,
                structuralResult.OverhangRegionsCovered, structuralResult.OverhangRegionsUncovered,
                structuralResult.MinSafetyFactor, structuralResult.AvgSafetyFactor,
                structuralResult.ManifoldErrors,
                structuralResult.ElapsedMs,
                issues = structuralResult.Issues.Take(50).Select(i => new
                {
                    i.SupportId, i.Category, i.Description, i.SafetyFactor,
                    i.X, i.Y, i.Z,
                }),
            },

            mesh = new
            {
                vertices = result.SupportMesh.VertexCount,
                faces = result.SupportMesh.FaceCount,
                nonManifoldEdges = nonManifold,
            },
        });
    }
}
