using HybridSlicer.Application.Interfaces.Repositories;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;
using Microsoft.AspNetCore.Mvc;

namespace HybridSlicer.Api.Controllers;

[ApiController]
[Route("api/advanced-support")]
public sealed class AdvancedSupportController : ControllerBase
{
    private readonly IMachineProfileRepository _printerRepo;
    public AdvancedSupportController(IMachineProfileRepository printerRepo) => _printerRepo = printerRepo;

    [HttpPost]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Generate(
        [FromForm] IFormFile stlFile,
        [FromForm] string? printerId = null,
        [FromForm] string orientation = "BottomUp",
        [FromForm] string supportType = "medium",     // light | medium | heavy | tree | crossbraced
        [FromForm] string placement = "buildplate",
        [FromForm] double overhangAngleDeg = 45,
        [FromForm] double density = 0.5,
        [FromForm] bool crossBracingEnabled = true,
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

        var result = AdvancedSupportEngine.Generate(mesh, new AdvancedSupportEngine.AdvancedSupportConfig
        {
            Orientation = orient,
            SupportType = supportType,
            Placement = placement,
            OverhangAngleDeg = overhangAngleDeg,
            DensityFactor = density,
            CrossBracingEnabled = crossBracingEnabled || supportType == "crossbraced",
        });

        return Ok(new
        {
            supportCount = result.Supports.Count,
            braceCount = result.CrossBraces.Count,
            overhangFaceCount = result.OverhangFaceCount,
            elapsedMs = result.ElapsedMs,
            orientation = orient.ToString(),
            supportType,
            supports = result.Supports.Select(s => new
            {
                s.Id, s.Type,
                contactX = s.ContactX, contactY = s.ContactY, contactZ = s.ContactZ,
                normalX = s.NormalX, normalY = s.NormalY, normalZ = s.NormalZ,
                baseX = s.BaseX, baseY = s.BaseY, baseZ = s.BaseZ,
                mergeX = s.MergeX, mergeY = s.MergeY, mergeZ = s.MergeZ,
                parentTrunkId = s.ParentTrunkId,
                preset = new { s.Preset.Name, s.Preset.TipDiameterMm, s.Preset.ShaftDiameterMm, s.Preset.BaseDiameterMm,
                    s.Preset.TipShape, s.Preset.ShaftType, s.Preset.BaseType, s.Preset.NeckType, s.Preset.StructureType },
                segments = s.Segments.Select(seg => new
                {
                    seg.Part, seg.X1, seg.Y1, seg.Z1, seg.R1, seg.X2, seg.Y2, seg.Z2, seg.R2,
                }),
            }),
            crossBraces = result.CrossBraces.Select(b => new
            {
                b.SupportA, b.SupportB, b.X1, b.Y1, b.Z1, b.X2, b.Y2, b.Z2, b.Diameter,
            }),
        });
    }

    /// <summary>
    /// Generate supports AND validate them against the mesh.
    /// Checks: tip contact, overhang coverage, shaft collision.
    /// </summary>
    [HttpPost("validate")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> GenerateAndValidate(
        [FromForm] IFormFile stlFile,
        [FromForm] string? printerId = null,
        [FromForm] string orientation = "BottomUp",
        [FromForm] string supportType = "medium",
        [FromForm] string placement = "buildplate",
        [FromForm] double overhangAngleDeg = 45,
        [FromForm] double density = 0.5,
        [FromForm] bool crossBracingEnabled = true,
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

        var config = new AdvancedSupportEngine.AdvancedSupportConfig
        {
            Orientation = orient,
            SupportType = supportType,
            Placement = placement,
            OverhangAngleDeg = overhangAngleDeg,
            DensityFactor = density,
            CrossBracingEnabled = crossBracingEnabled || supportType == "crossbraced",
        };

        var result = AdvancedSupportEngine.Generate(mesh, config);
        var validation = AdvancedSupportEngine.Validate(mesh, result, config);

        return Ok(new
        {
            generation = new { supportCount = result.Supports.Count, braceCount = result.CrossBraces.Count, result.OverhangFaceCount },
            validation = new
            {
                validation.TotalSupports,
                validation.TotalOverhangs,
                validation.TipsTouchingModel,
                validation.TipsDetached,
                validation.OverhangsSupported,
                validation.OverhangsUnsupported,
                validation.CollisionsDetected,
                validation.ElapsedMs,
                issues = validation.Issues.Select(i => new
                {
                    i.SupportId, i.Category, i.Description, i.X, i.Y, i.Z,
                }),
            },
        });
    }
}
