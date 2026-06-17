using HybridSlicer.Application.Interfaces.Repositories;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;
using Microsoft.AspNetCore.Mvc;

namespace HybridSlicer.Api.Controllers;

[ApiController]
[Route("api/prep-tools")]
public sealed class PrepToolsController : ControllerBase
{
    private readonly IMachineProfileRepository _printerRepo;
    public PrepToolsController(IMachineProfileRepository printerRepo) => _printerRepo = printerRepo;

    /// <summary>Suggest drain hole positions for a hollowed model.</summary>
    [HttpPost("suggest-drain-holes")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> SuggestDrainHoles(
        [FromForm] IFormFile stlFile,
        [FromForm] double holeDiameterMm = 2.5,
        [FromForm] double holeDepthMm = 5.0,
        [FromForm] int maxHoles = 3,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromBinary(data);
        var holes = DrainHoleEngine.SuggestDrainHoles(mesh, holeDiameterMm, holeDepthMm, maxHoles);
        return Ok(new { count = holes.Count, holes = holes.Select(h => new {
            h.X, h.Y, h.Z, h.DiameterMm, h.DepthMm, h.NormalX, h.NormalY, h.NormalZ
        }) });
    }

    /// <summary>Auto-arrange multiple parts on the build plate.</summary>
    [HttpPost("nest")]
    public IActionResult Nest(
        [FromBody] NestRequest request)
    {
        var parts = request.Parts.Select(p => new BuildPlateNester.PartFootprint
        {
            Id = p.Id, WidthMm = p.WidthMm, DepthMm = p.DepthMm,
        }).ToList();

        var result = BuildPlateNester.Arrange(parts, new BuildPlateNester.NestConfig
        {
            PlateWidthMm = request.PlateWidthMm,
            PlateDepthMm = request.PlateDepthMm,
            PartGapMm = request.PartGapMm,
            PlateMarginMm = request.PlateMarginMm,
        });

        return Ok(new
        {
            placements = result.Placements.Select(p => new { p.Id, p.CenterX, p.CenterY, p.Rotated90 }),
            overflow = result.Overflow,
            utilization = result.Utilization,
        });
    }

    public sealed record NestRequest(
        List<NestPartDto> Parts,
        float PlateWidthMm = 192f,
        float PlateDepthMm = 120f,
        float PartGapMm = 2f,
        float PlateMarginMm = 3f);

    public sealed record NestPartDto(string Id, float WidthMm, float DepthMm);

    /// <summary>Run support optimization with recoater/tall-thin analysis.</summary>
    [HttpPost("optimize-supports")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> OptimizeSupports(
        [FromForm] IFormFile stlFile,
        [FromForm] string? printerId = null,
        [FromForm] string orientation = "BottomUp",
        [FromForm] double density = 0.5,
        [FromForm] bool recoaterAware = false,
        [FromForm] bool tallThinReinforce = true,
        [FromForm] bool reduceSupports = false,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        // Resolve printer
        Domain.Entities.MachineProfile? printer = null;
        if (!string.IsNullOrEmpty(printerId) && Guid.TryParse(printerId, out var pid))
            printer = await _printerRepo.GetByIdAsync(pid, ct);

        var orient = printer?.Orientation ??
            (Enum.TryParse<Domain.Enums.PrinterOrientation>(orientation, true, out var o) ? o : Domain.Enums.PrinterOrientation.BottomUp);

        // Generate base supports
        var baseResult = AutoSupportEngine.Generate(mesh, new AutoSupportEngine.SupportConfig
        {
            Orientation = orient, DensityFactor = density,
        });

        // Create a temporary printer for optimization if none provided
        var effectivePrinter = printer ?? Domain.Entities.MachineProfile.Create("temp", Domain.Enums.MachineType.MSLA, 100, 100, 100);

        // Optimize
        var optResult = SupportOptimizer.Optimize(baseResult.Supports, mesh, effectivePrinter,
            new SupportOptimizer.OptimizationConfig
            {
                RecoaterAware = recoaterAware && (printer?.HasRecoater ?? false),
                RecoaterSpeedMmPerS = printer?.RecoaterSpeedMmPerS ?? 0,
                RecoaterClearanceMm = printer?.RecoaterClearanceMm ?? 2,
                RecoaterType = printer?.RecoaterType ?? "blade",
                RecoaterDirection = printer?.RecoaterDirection ?? "X",
                TallThinReinforcement = tallThinReinforce,
                ReduceSupports = reduceSupports,
            });

        return Ok(new
        {
            originalCount = optResult.OriginalCount,
            finalCount = optResult.Supports.Count,
            addedForReinforcement = optResult.AddedForReinforcement,
            removedForReduction = optResult.RemovedForReduction,
            recoaterReinforcements = optResult.RecoaterReinforcements,
            warnings = optResult.Warnings,
            supports = optResult.Supports.Select(s => new {
                s.X, s.Y, contactZ = s.ContactZ, baseZ = s.BaseZ,
                s.TipDiameter, s.ColumnDiameter, s.BaseDiameter,
            }),
        });
    }

    /// <summary>Get print history and resin usage stats.</summary>
    [HttpGet("print-history")]
    public IActionResult GetPrintHistory()
    {
        var tracker = new HybridSlicer.Infrastructure.Resin.Analysis.PrintHistoryTracker();
        var history = tracker.GetHistory();
        return Ok(new
        {
            totalPrints = history.TotalPrints,
            totalResinMl = history.TotalResinMl,
            totalCostUsd = history.TotalCostUsd,
            totalPrintTimeMinutes = history.TotalPrintTimeMinutes,
            recentPrints = history.Records.TakeLast(10).Reverse().Select(r => new
            {
                r.JobId, r.ModelName, r.PrintedAt, r.ResinVolumeMl, r.ResinCostUsd,
                r.PrintTimeMinutes, r.LayerCount, r.ExportFormat, r.PrinterName,
            }),
        });
    }
}
