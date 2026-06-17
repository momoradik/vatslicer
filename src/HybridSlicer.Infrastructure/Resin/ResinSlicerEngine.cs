using System.Diagnostics;
using HybridSlicer.Domain.Entities;
using HybridSlicer.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace HybridSlicer.Infrastructure.Resin;

/// <summary>
/// Complete resin slicing pipeline: STL → cross-section → rasterize → layer PNGs.
/// Outputs layer images at the printer's native resolution.
/// </summary>
public sealed class ResinSlicerEngine
{
    private readonly ILogger<ResinSlicerEngine> _log;

    public ResinSlicerEngine(ILogger<ResinSlicerEngine> log) => _log = log;

    public record SliceRequest
    {
        public required byte[] StlData { get; init; }
        public required MachineProfile Printer { get; init; }
        public required ResinPrintProfile PrintProfile { get; init; }
        public required string OutputDir { get; init; }
        // Transform applied to the mesh (from the viewport)
        public float TranslateX { get; init; }
        public float TranslateY { get; init; }
        public float TranslateZ { get; init; }
        public float Scale { get; init; } = 1.0f;
        // Support settings (resolved from job/profile/per-object chain)
        public bool SupportEnabled { get; init; }
        public string SupportType { get; init; } = "normal";
        public string SupportPlacement { get; init; } = "buildplate";
        // Hollowing
        public bool HollowEnabled { get; init; }
        public float HollowWallThicknessMm { get; init; } = 1.5f;
        // Infill (honeycomb/lattice)
        public string InfillPattern { get; init; } = "none"; // none | honeycomb | grid | triangular | gyroid
        public double InfillDensityPct { get; init; }
        public double InfillCellSizeMm { get; init; } = 3.0;
        // Drain holes
        public List<DrainHoleEngine.DrainHole>? DrainHoles { get; init; }
        // Auto-generated support/raft/skirt data (from AutoSupportEngine)
        public List<AutoSupportEngine.GeneratedSupport>? AutoSupports { get; init; }
        public AutoSupportEngine.GeneratedRaft? Raft { get; init; }
        public AutoSupportEngine.GeneratedSkirt? Skirt { get; init; }
        // V2 support engine: analytical slice elements (preferred over AutoSupports if set)
        public List<Slicing.AnalyticalSupportSlicer.SupportElement>? V2SupportElements { get; init; }
        /// <summary>Exposure intensity for support pixels (0-255). 255=full, 178=70% for easy removal.</summary>
        public byte SupportExposureIntensity { get; init; } = 255;
    }

    public record SliceResult
    {
        public int LayerCount { get; init; }
        public int BottomLayerCount { get; init; }
        public double LayerHeightMm { get; init; }
        public int ResolutionX { get; init; }
        public int ResolutionY { get; init; }
        public double NormalExposureMs { get; init; }
        public double BottomExposureMs { get; init; }
        public double TotalHeightMm { get; init; }
        public double EstimatedPrintTimeMin { get; init; }
        public string OutputDir { get; init; } = "";
        public long ElapsedMs { get; init; }
        public SliceJobData? JobData { get; init; }
    }

    public SliceResult Slice(SliceRequest req)
    {
        var sw = Stopwatch.StartNew();
        var printer = req.Printer;
        var profile = req.PrintProfile;

        // Validate
        if (printer.ResolutionX <= 0 || printer.ResolutionY <= 0)
            throw new InvalidOperationException("Printer resolution must be set (ResolutionX/Y > 0).");
        if (printer.BedWidthMm <= 0 || printer.BedDepthMm <= 0)
            throw new InvalidOperationException("Printer build volume must be set.");

        // Parse + validate + repair STL
        _log.LogInformation("Parsing STL ({Bytes} bytes)...", req.StlData.Length);
        var (mesh, validation) = MeshValidator.ValidateAndRepair(req.StlData);

        if (validation.Repaired)
            _log.LogWarning("Mesh repaired: {Removed} degenerate triangles removed, {Fixed} normals fixed",
                validation.TrianglesRemoved, validation.NormalsFixed);
        if (validation.Warnings.Count > 0)
            foreach (var w in validation.Warnings) _log.LogWarning("Mesh warning: {Warning}", w);
        if (validation.Errors.Count > 0)
            foreach (var e in validation.Errors) _log.LogError("Mesh error: {Error}", e);
        if (!validation.IsValid && validation.TriangleCount == 0)
            throw new InvalidOperationException("Mesh is invalid and cannot be sliced: " + string.Join("; ", validation.Errors));

        _log.LogInformation("Mesh: {Tris} triangles, bounds [{MinX},{MinY},{MinZ}] → [{MaxX},{MaxY},{MaxZ}], volume={Vol}mm³",
            mesh.TriangleCount, mesh.Min.X, mesh.Min.Y, mesh.Min.Z, mesh.Max.X, mesh.Max.Y, mesh.Max.Z, validation.VolumeMm3);

        // Apply transform if provided
        if (req.TranslateX != 0 || req.TranslateY != 0 || req.TranslateZ != 0 || req.Scale != 1.0f)
        {
            mesh = mesh.Transform(
                new System.Numerics.Vector3(req.TranslateX, req.TranslateY, req.TranslateZ),
                req.Scale);
        }

        // Center mesh on build plate (XY) and place on bed (Z=0)
        float meshWidth = mesh.Max.X - mesh.Min.X;
        float meshDepth = mesh.Max.Y - mesh.Min.Y;
        float meshHeight = mesh.Max.Z - mesh.Min.Z;

        // Shift so mesh sits on Z=0 and is centered in XY on the build plate
        float offsetX = (float)(printer.BedWidthMm / 2) - (mesh.Min.X + meshWidth / 2);
        float offsetY = (float)(printer.BedDepthMm / 2) - (mesh.Min.Y + meshDepth / 2);
        float offsetZ = -mesh.Min.Z;

        mesh = mesh.Transform(new System.Numerics.Vector3(offsetX, offsetY, offsetZ), 1.0f);

        // Apply the same centering offset to auto-support/raft/skirt positions
        List<AutoSupportEngine.GeneratedSupport>? centeredSupports = null;
        AutoSupportEngine.GeneratedRaft? centeredRaft = null;
        AutoSupportEngine.GeneratedSkirt? centeredSkirt = null;
        if (req.AutoSupports is { Count: > 0 })
        {
            centeredSupports = req.AutoSupports.Select(s => s with
            {
                X = s.X + offsetX,
                Y = s.Y + offsetY,
                ContactZ = s.ContactZ + offsetZ,
                BaseZ = s.BaseZ + offsetZ,
            }).ToList();
        }
        if (req.Raft is not null)
        {
            centeredRaft = req.Raft with
            {
                MinX = req.Raft.MinX + offsetX,
                MinY = req.Raft.MinY + offsetY,
                MaxX = req.Raft.MaxX + offsetX,
                MaxY = req.Raft.MaxY + offsetY,
            };
        }
        if (req.Skirt is not null)
        {
            centeredSkirt = req.Skirt with
            {
                MinX = req.Skirt.MinX + offsetX,
                MinY = req.Skirt.MinY + offsetY,
                MaxX = req.Skirt.MaxX + offsetX,
                MaxY = req.Skirt.MaxY + offsetY,
            };
        }

        float totalHeight = mesh.Max.Z - mesh.Min.Z;
        float layerHeight = (float)profile.LayerHeightMm;
        int layerCount = (int)Math.Ceiling(totalHeight / layerHeight);

        _log.LogInformation("Slicing: {Layers} layers at {LayerH}mm, total height {H}mm",
            layerCount, layerHeight, totalHeight);

        // Ensure output directory
        Directory.CreateDirectory(req.OutputDir);

        // Resolution and build size
        int resX = printer.ResolutionX;
        int resY = printer.ResolutionY;
        float buildW = (float)printer.BedWidthMm;
        float buildD = (float)printer.BedDepthMm;
        bool aa = profile.AntiAliasing != AntiAliasingLevel.None;
        bool mirrorX = printer.MirrorX;
        bool mirrorY = printer.MirrorY;

        // Handle top-down vs bottom-up orientation
        bool bottomUp = printer.Orientation == PrinterOrientation.BottomUp;
        int bottomLayerCount = (int)printer.DefaultBottomLayerCount;

        // Exposure/lift values (needed per-layer and for time estimation)
        var normalExposure = printer.DefaultNormalExposureMs;
        var bottomExposure = printer.DefaultBottomExposureMs;
        var liftDist = printer.LiftDistanceMm;
        var liftSpeed = printer.LiftSpeedMmPerMin;

        // Slice each layer — reuse render context for performance
        int emptyLayers = 0;
        int totalIslands = 0;
        double totalVolumeMm3 = 0;
        float pixelAreaMm2 = (buildW / resX) * (buildD / resY); // area of one pixel in mm2
        var layerRecords = new List<LayerRecord>(layerCount);
        List<List<System.Numerics.Vector2>> prevPolygons = new();
        using var ctx = new LayerRasterizer.RenderContext(resX, resY, buildW, buildD, aa, mirrorX, mirrorY);
        bool doHollow = req.HollowEnabled && req.HollowWallThicknessMm > 0;

        if (doHollow)
            _log.LogInformation("Hollowing enabled: wall={WallMm}mm", req.HollowWallThicknessMm);

        for (int i = 0; i < layerCount; i++)
        {
            float z = mesh.Min.Z + (i + 0.5f) * layerHeight;

            var polygons = MeshCrossSectionEngine.CrossSection(mesh, z);

            byte[] png;
            if (doHollow && polygons.Count > 0)
            {
                png = LayerRasterizer.RasterizeHollow(ctx, polygons, req.HollowWallThicknessMm);
                // Add infill pattern inside hollow
                if (req.InfillPattern != "none" && req.InfillDensityPct > 0)
                {
                    HoneycombInfillEngine.DrawInfill(ctx, polygons, req.HollowWallThicknessMm,
                        new HoneycombInfillEngine.InfillConfig
                        {
                            Pattern = req.InfillPattern,
                            DensityPct = req.InfillDensityPct,
                            CellSizeMm = req.InfillCellSizeMm,
                        }, z);
                    png = EncodePngFromCtx(ctx);
                }
            }
            else
                png = LayerRasterizer.Rasterize(ctx, polygons);

            // Subtract drain holes
            if (req.DrainHoles is { Count: > 0 })
            {
                DrainHoleEngine.SubtractDrainHoles(ctx, req.DrainHoles, z);
                png = EncodePngFromCtx(ctx);
            }

            // Add support geometry to the layer image
            if (req.V2SupportElements is { Count: > 0 })
            {
                // V2 analytical slicing — exact circle cross-sections
                var circles = Slicing.AnalyticalSupportSlicer.SliceAtZ(req.V2SupportElements, z);
                if (circles.Count > 0)
                {
                    Slicing.SupportSliceIntegrator.RenderSupportsOnLayer(
                        ctx.Canvas, circles, ctx.ScaleX, ctx.ScaleY,
                        ctx.ResX / 2f, ctx.ResY / 2f, req.SupportExposureIntensity);
                    png = EncodePngFromCtx(ctx);
                }
            }

            // Legacy support columns, raft, and skirt
            bool hasExtraGeom = centeredSupports is { Count: > 0 } || centeredRaft is not null || centeredSkirt is not null;
            if (hasExtraGeom)
            {
                LayerRasterizer.DrawSupportsRaftSkirt(ctx, centeredSupports, centeredRaft, centeredSkirt, z, layerHeight);
                png = EncodePngFromCtx(ctx);
            }

            // Volume: count non-black pixels in rendered surface → cross-section area
            {
                var pixmap = ctx.Surface.PeekPixels();
                if (pixmap != null)
                {
                    int whitePixels = 0;
                    var span = pixmap.GetPixelSpan<byte>();
                    for (int px = 0; px < span.Length; px++)
                        if (span[px] > 0) whitePixels++;
                    totalVolumeMm3 += whitePixels * pixelAreaMm2 * layerHeight;
                }
            }

            // Island detection
            int layerIslands = IslandDetector.DetectIslands(polygons, prevPolygons);
            totalIslands += layerIslands;
            prevPolygons = polygons;

            bool isEmpty = polygons.Count == 0 && !hasExtraGeom;
            if (isEmpty) emptyLayers++;

            var fileName = $"layer_{i:D5}.png";
            File.WriteAllBytes(Path.Combine(req.OutputDir, fileName), png);

            // Determine layer type and exposure
            LayerType layerType;
            double layerExposure;
            if (i < bottomLayerCount) { layerType = LayerType.Bottom; layerExposure = bottomExposure; }
            else { layerType = LayerType.Normal; layerExposure = normalExposure; }

            layerRecords.Add(new LayerRecord
            {
                Index = i,
                ZHeightMm = z,
                LayerThicknessMm = layerHeight,
                Type = layerType,
                ExposureMs = layerExposure,
                LiftDistanceMm = i < bottomLayerCount ? printer.BottomLiftDistanceMm : printer.LiftDistanceMm,
                LiftSpeedMmPerMin = i < bottomLayerCount ? printer.BottomLiftSpeedMmPerMin : printer.LiftSpeedMmPerMin,
                LightOffDelayMs = printer.LightOffDelayMs,
                ImageFileName = fileName,
                ContourCount = polygons.Count,
                ImageSizeBytes = png.Length,
                IsEmpty = isEmpty,
                IslandCount = layerIslands,
            });

            if (i % 100 == 0 || i == layerCount - 1)
                _log.LogInformation("  Layer {I}/{Total} ({Polys} contours, {Bytes} bytes)",
                    i + 1, layerCount, polygons.Count, png.Length);
        }

        // Estimate print time (rough)
        double liftTimePerLayer = (liftDist * 2) / (liftSpeed / 60.0); // up + down
        double normalLayerTime = normalExposure / 1000.0 + liftTimePerLayer + printer.LightOffDelayMs / 1000.0;
        double bottomLayerTime = bottomExposure / 1000.0 + liftTimePerLayer + printer.LightOffDelayMs / 1000.0;
        double totalTimeSec = bottomLayerCount * bottomLayerTime + (layerCount - bottomLayerCount) * normalLayerTime;

        // Volume-based estimates
        double resinVolumeMl = totalVolumeMm3 / 1000.0;
        double resinCostUsd = resinVolumeMl * 0.05; // ~$0.05/ml typical resin
        double resinWeightG = totalVolumeMm3 * 1.1e-3; // ~1.1 g/cm3 resin density

        var meta = new
        {
            layerCount, bottomLayerCount, layerHeightMm = layerHeight,
            resolutionX = resX, resolutionY = resY,
            buildWidthMm = buildW, buildDepthMm = buildD,
            normalExposureMs = normalExposure, bottomExposureMs = bottomExposure,
            orientation = printer.Orientation.ToString(),
            antiAliasing = profile.AntiAliasing.ToString(),
            mirrorX, mirrorY,
            totalHeightMm = totalHeight,
            estimatedPrintTimeMin = totalTimeSec / 60.0,
            resinVolumeMl = Math.Round(resinVolumeMl, 2),
            resinWeightG = Math.Round(resinWeightG, 1),
            estimatedCostUsd = Math.Round(resinCostUsd, 2),
            printerName = printer.Name,
            profileName = profile.Name,
            slicedAt = DateTime.UtcNow,
            emptyLayers,
            supportEnabled = req.SupportEnabled,
            supportType = req.SupportType,
            supportPlacement = req.SupportPlacement,
            hollowEnabled = req.HollowEnabled,
            hollowWallThicknessMm = req.HollowWallThicknessMm,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(meta,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(req.OutputDir, "slice_meta.json"), json);

        sw.Stop();
        _log.LogInformation("Slicing complete: {Layers} layers in {Ms}ms ({Empty} empty)",
            layerCount, sw.ElapsedMilliseconds, emptyLayers);

        // Build structured job data
        var jobData = new SliceJobData
        {
            JobId = Path.GetFileName(req.OutputDir),
            OutputDir = req.OutputDir,
            LayerCount = layerCount,
            BottomLayerCount = bottomLayerCount,
            LayerHeightMm = layerHeight,
            ResolutionX = resX, ResolutionY = resY,
            BuildWidthMm = buildW, BuildDepthMm = buildD,
            TotalHeightMm = totalHeight,
            NormalExposureMs = normalExposure,
            BottomExposureMs = bottomExposure,
            EstimatedPrintTimeMin = totalTimeSec / 60.0,
            ElapsedMs = sw.ElapsedMilliseconds,
            PrinterName = printer.Name,
            ProfileName = profile.Name,
            Orientation = printer.Orientation.ToString(),
            AntiAliasing = profile.AntiAliasing.ToString(),
            MirrorX = mirrorX, MirrorY = mirrorY,
            HollowEnabled = req.HollowEnabled,
            HollowWallThicknessMm = req.HollowWallThicknessMm,
            SupportEnabled = req.SupportEnabled,
            SupportType = req.SupportType,
            SupportPlacement = req.SupportPlacement,
            SlicedAt = DateTime.UtcNow,
            Layers = layerRecords,
        };

        // Write layer data JSON (full structured layer metadata)
        var layerDataJson = System.Text.Json.JsonSerializer.Serialize(jobData,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(req.OutputDir, "slice_data.json"), layerDataJson);

        return new SliceResult
        {
            LayerCount = layerCount,
            BottomLayerCount = bottomLayerCount,
            LayerHeightMm = layerHeight,
            ResolutionX = resX, ResolutionY = resY,
            NormalExposureMs = normalExposure,
            BottomExposureMs = bottomExposure,
            TotalHeightMm = totalHeight,
            EstimatedPrintTimeMin = totalTimeSec / 60.0,
            OutputDir = req.OutputDir,
            ElapsedMs = sw.ElapsedMilliseconds,
            JobData = jobData,
        };
    }

    private static byte[] EncodePngFromCtx(LayerRasterizer.RenderContext ctx)
    {
        using var img = ctx.Surface.Snapshot();
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
