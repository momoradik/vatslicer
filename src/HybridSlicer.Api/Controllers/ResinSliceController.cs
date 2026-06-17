using HybridSlicer.Application.Interfaces.Repositories;
using HybridSlicer.Infrastructure.Persistence.Repositories;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Exporters;
using Microsoft.AspNetCore.Mvc;

namespace HybridSlicer.Api.Controllers;

[ApiController]
[Route("api/resin-slice")]
public sealed class ResinSliceController : ControllerBase
{
    private readonly IMachineProfileRepository _printerRepo;
    private readonly IResinPrintProfileRepository _profileRepo;
    private readonly ResinSlicerEngine _slicer;
    private readonly ILogger<ResinSliceController> _log;

    public ResinSliceController(
        IMachineProfileRepository printerRepo,
        IResinPrintProfileRepository profileRepo,
        ResinSlicerEngine slicer,
        ILogger<ResinSliceController> log)
    {
        _printerRepo = printerRepo;
        _profileRepo = profileRepo;
        _slicer = slicer;
        _log = log;
    }

    /// <summary>
    /// Slice an STL file using the specified printer and print profile.
    /// Returns the slice result metadata. Layer images are stored on disk.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(200_000_000)] // 200 MB max upload
    public async Task<IActionResult> Slice(
        [FromForm] IFormFile stlFile,
        [FromForm] string printerId,
        [FromForm] string printProfileId,
        [FromForm] float translateX = 0,
        [FromForm] float translateY = 0,
        [FromForm] float translateZ = 0,
        [FromForm] float scale = 1.0f,
        [FromForm] bool supportEnabled = false,
        [FromForm] string supportType = "normal",
        [FromForm] string supportPlacement = "buildplate",
        [FromForm] bool hollowEnabled = false,
        [FromForm] float hollowWallThicknessMm = 1.5f,
        [FromForm] string? manualSupportData = null,
        [FromForm] double autoSupportDensity = 0.5,
        [FromForm] double autoSupportOverhangAngle = 45,
        [FromForm] bool raftEnabled = false,
        [FromForm] string raftType = "grid",
        [FromForm] bool skirtEnabled = false,
        [FromForm] int skirtLayers = 3,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0)
            return BadRequest("STL file is required.");

        if (!Guid.TryParse(printerId, out var pid))
            return BadRequest("Invalid printer ID.");
        if (!Guid.TryParse(printProfileId, out var ppid))
            return BadRequest("Invalid print profile ID.");

        var printer = await _printerRepo.GetByIdAsync(pid, ct);
        if (printer is null) return BadRequest("Printer profile not found.");
        if (!printer.IsResinPrinter) return BadRequest("Selected printer is not a resin printer (MSLA/DLP).");

        var profile = await _profileRepo.GetByIdAsync(ppid, ct);
        if (profile is null) return BadRequest("Print profile not found.");

        // Validate printer parameters
        if (printer.ResolutionX <= 0 || printer.ResolutionY <= 0)
            return BadRequest($"Printer resolution must be positive (got {printer.ResolutionX}x{printer.ResolutionY}).");
        if (printer.BedWidthMm <= 0 || printer.BedDepthMm <= 0)
            return BadRequest("Printer build plate dimensions must be positive.");
        if (profile.LayerHeightMm <= 0 || profile.LayerHeightMm > 1.0)
            return BadRequest($"Layer height must be between 0.001 and 1.0 mm (got {profile.LayerHeightMm}).");

        // Validate scale
        if (scale <= 0 || scale > 100)
            return BadRequest($"Scale must be between 0.001 and 100 (got {scale}).");

        // Read STL bytes
        byte[] stlData;
        using (var ms = new MemoryStream())
        {
            await stlFile.CopyToAsync(ms, ct);
            stlData = ms.ToArray();
        }

        // Generate unique job directory
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var storageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fabrium", "slice-jobs", jobId);

        var hasManualSupports = !string.IsNullOrEmpty(manualSupportData);
        _log.LogInformation("Starting resin slice job {JobId}: printer={Printer}, profile={Profile}, STL={Size}KB, supports={SupEnabled} ({SupType}/{SupPlace}), manualEdits={HasManual}",
            jobId, printer.Name, profile.Name, stlData.Length / 1024, supportEnabled, supportType, supportPlacement, hasManualSupports);

        // Store manual support data if provided
        if (hasManualSupports)
        {
            Directory.CreateDirectory(storageRoot);
            System.IO.File.WriteAllText(Path.Combine(storageRoot, "manual_supports.json"), manualSupportData!);
        }

        try
        {
            // Generate supports: try V2 engine first, fallback to legacy
            AutoSupportEngine.AutoSupportResult? autoResult = null;
            SupportEngineV2.EngineResult? v2Result = null;

            if (supportEnabled || raftEnabled || skirtEnabled)
            {
                var parsedMesh = StlMesh.FromBinary(stlData);

                // V2 engine for supports (production-grade)
                if (supportEnabled)
                {
                    try
                    {
                        var (validatedMesh, _) = MeshValidator.ValidateAndRepair(stlData);
                        v2Result = SupportEngineV2.Generate(validatedMesh, new SupportEngineV2.EngineConfig
                        {
                            Orientation = printer.Orientation,
                            OverhangAngleDeg = (float)autoSupportOverhangAngle,
                            DensityFactor = (float)autoSupportDensity,
                        });
                        _log.LogInformation("V2 supports: {Count} supports, {Ms}ms, {Faces} mesh faces",
                            v2Result.ValidSupports, v2Result.TotalElapsedMs, v2Result.SupportMesh.FaceCount);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "V2 support engine failed, falling back to legacy");
                    }
                }

                // Legacy engine for raft/skirt (and fallback supports if V2 failed)
                if (raftEnabled || skirtEnabled || (supportEnabled && v2Result == null))
                {
                    autoResult = AutoSupportEngine.Generate(parsedMesh, new AutoSupportEngine.SupportConfig
                    {
                        Orientation = printer.Orientation,
                        OverhangAngleDeg = supportEnabled && v2Result == null ? autoSupportOverhangAngle : 0,
                        DensityFactor = supportEnabled && v2Result == null ? autoSupportDensity : 0,
                        SupportType = supportType,
                        Placement = supportPlacement,
                        RaftEnabled = raftEnabled,
                        RaftType = raftType,
                        SkirtEnabled = skirtEnabled,
                        SkirtLayers = skirtLayers,
                    });
                }
            }

            var result = _slicer.Slice(new ResinSlicerEngine.SliceRequest
            {
                StlData = stlData,
                Printer = printer,
                PrintProfile = profile,
                OutputDir = storageRoot,
                TranslateX = translateX,
                TranslateY = translateY,
                TranslateZ = translateZ,
                Scale = scale,
                SupportEnabled = supportEnabled,
                SupportType = supportType,
                SupportPlacement = supportPlacement,
                HollowEnabled = hollowEnabled,
                HollowWallThicknessMm = hollowWallThicknessMm,
                // V2 supports (analytical slicing into layer images)
                V2SupportElements = v2Result?.SliceElements,
                // Legacy supports/raft/skirt
                AutoSupports = v2Result == null ? autoResult?.Supports : null,
                Raft = autoResult?.Raft,
                Skirt = autoResult?.Skirt,
            });

            return Ok(new
            {
                jobId,
                result.LayerCount,
                result.BottomLayerCount,
                result.LayerHeightMm,
                result.ResolutionX,
                result.ResolutionY,
                result.NormalExposureMs,
                result.BottomExposureMs,
                result.TotalHeightMm,
                result.EstimatedPrintTimeMin,
                result.ElapsedMs,
                printerName = printer.Name,
                profileName = profile.Name,
                supportEnabled,
                supportType,
                supportPlacement,
                hasManualSupports,
                hollowEnabled,
                hollowWallThicknessMm,
                autoSupportCount = autoResult?.Supports.Count ?? 0,
                raftGenerated = autoResult?.Raft is not null,
                skirtGenerated = autoResult?.Skirt is not null,
                totalIslands = result.JobData?.Layers.Sum(l => l.IslandCount) ?? 0,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Slicing failed for job {JobId}", jobId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific layer image from a slice job.
    /// </summary>
    [HttpGet("{jobId}/layer/{layerIndex}")]
    public IActionResult GetLayerImage(string jobId, int layerIndex)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fabrium", "slice-jobs", jobId);

        var file = Path.Combine(dir, $"layer_{layerIndex:D5}.png");
        if (!System.IO.File.Exists(file))
            return NotFound($"Layer {layerIndex} not found.");

        return PhysicalFile(file, "image/png");
    }

    /// <summary>
    /// Get the slice metadata JSON for a job.
    /// </summary>
    [HttpGet("{jobId}/meta")]
    public IActionResult GetMeta(string jobId)
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fabrium", "slice-jobs", jobId, "slice_meta.json");

        if (!System.IO.File.Exists(file))
            return NotFound("Slice metadata not found.");

        return PhysicalFile(file, "application/json");
    }

    /// <summary>
    /// Get the full structured layer data (per-layer metadata) for a job.
    /// </summary>
    [HttpGet("{jobId}/layers")]
    public IActionResult GetLayerData(string jobId)
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fabrium", "slice-jobs", jobId, "slice_data.json");

        if (!System.IO.File.Exists(file))
            return NotFound("Layer data not found.");

        return PhysicalFile(file, "application/json");
    }

    /// <summary>
    /// Get metadata for a specific layer.
    /// </summary>
    [HttpGet("{jobId}/layer/{layerIndex}/info")]
    public IActionResult GetLayerInfo(string jobId, int layerIndex)
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fabrium", "slice-jobs", jobId, "slice_data.json");

        if (!System.IO.File.Exists(file))
            return NotFound("Layer data not found.");

        var json = System.IO.File.ReadAllText(file);
        var data = System.Text.Json.JsonSerializer.Deserialize<ResinSlicerEngine.SliceResult>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Parse the layers array from the JSON directly
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("layers", out var layersElem))
            return NotFound("No layers array in data.");

        if (layerIndex < 0 || layerIndex >= layersElem.GetArrayLength())
            return NotFound($"Layer {layerIndex} out of range.");

        var layer = layersElem[layerIndex];
        return Content(layer.GetRawText(), "application/json");
    }

    /// <summary>
    /// Export a sliced job to a printer-specific file format.
    /// Supported formats: ctb, cbddlp, photon, pwmx, pwms, pwmb, sl1, zip
    /// </summary>
    [HttpGet("{jobId}/export/{format}")]
    public async Task<IActionResult> Export(string jobId, string format, CancellationToken ct)
    {
        var exporter = SliceExporterFactory.GetExporter(format);
        if (exporter == null)
            return BadRequest($"Unsupported format: {format}. Supported: {string.Join(", ", SliceExporterFactory.SupportedFormats.Select(f => f.format))}");

        var jobDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fabrium", "slice-jobs", jobId);

        if (!Directory.Exists(jobDir))
            return NotFound($"Job {jobId} not found.");

        // Read slice metadata
        var metaFile = Path.Combine(jobDir, "slice_data.json");
        if (!System.IO.File.Exists(metaFile))
            return NotFound("Slice data not found. Run slice first.");

        var metaJson = await System.IO.File.ReadAllTextAsync(metaFile, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(metaJson);
        var root = doc.RootElement;

        // Build export config from metadata
        int resX = root.TryGetProperty("resolutionX", out var rx) ? rx.GetInt32() : 1920;
        int resY = root.TryGetProperty("resolutionY", out var ry) ? ry.GetInt32() : 1080;
        float layerH = root.TryGetProperty("layerHeightMm", out var lh) ? lh.GetSingle() : 0.05f;
        float exposure = root.TryGetProperty("normalExposureMs", out var ne) ? ne.GetSingle() / 1000f : 2.0f;
        float bottomExposure = root.TryGetProperty("bottomExposureMs", out var be) ? be.GetSingle() / 1000f : 30f;
        int bottomLayers = root.TryGetProperty("bottomLayerCount", out var bl) ? bl.GetInt32() : 4;
        float liftDist = root.TryGetProperty("liftDistanceMm", out var ld) ? ld.GetSingle() : 5f;
        float liftSpeed = root.TryGetProperty("liftSpeedMmPerMin", out var ls) ? ls.GetSingle() : 120f;

        var config = new SliceExportConfig
        {
            ResolutionX = resX,
            ResolutionY = resY,
            BedWidthMm = root.TryGetProperty("bedWidthMm", out var bw) ? bw.GetSingle() : 192f,
            BedDepthMm = root.TryGetProperty("bedDepthMm", out var bd) ? bd.GetSingle() : 120f,
            BedHeightMm = 250f,
            MachineName = root.TryGetProperty("machineName", out var mn) ? mn.GetString() ?? "VATSlicer" : "VATSlicer",
            LayerHeightMm = layerH,
            BottomLayerCount = bottomLayers,
            ExposureTimeS = exposure,
            BottomExposureTimeS = bottomExposure,
            LiftDistanceMm = liftDist,
            LiftSpeedMmPerMin = liftSpeed,
            RetractSpeedMmPerMin = liftSpeed * 2,
            LightOffDelayS = 1.0f,
            BottomLiftDistanceMm = liftDist * 1.5f,
            BottomLiftSpeedMmPerMin = liftSpeed * 0.5f,
            TotalLayers = 0, // set below
        };

        // Collect layer PNG files
        var layerFiles = Directory.GetFiles(jobDir, "layer_*.png")
            .OrderBy(f => f)
            .ToList();

        if (layerFiles.Count == 0)
            return NotFound("No layer images found.");

        config = config with { TotalLayers = layerFiles.Count };

        var layerImages = new List<byte[]>(layerFiles.Count);
        foreach (var lf in layerFiles)
        {
            ct.ThrowIfCancellationRequested();
            layerImages.Add(await System.IO.File.ReadAllBytesAsync(lf, ct));
        }

        // Export
        var ms = new MemoryStream();
        exporter.Export(config, layerImages, ms);
        ms.Position = 0;

        var fileName = $"{Path.GetFileNameWithoutExtension(jobId)}{exporter.FileExtension}";
        return File(ms, "application/octet-stream", fileName);
    }

    /// <summary>
    /// List supported export formats.
    /// </summary>
    [HttpGet("formats")]
    public IActionResult GetFormats()
    {
        return Ok(SliceExporterFactory.SupportedFormats.Select(f => new
        {
            f.format, f.name, f.extension,
        }));
    }
}
