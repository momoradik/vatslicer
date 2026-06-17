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
        [FromForm] float interconnectDistMm = 50f,
        // V2 advanced features
        [FromForm] bool enableTreeSupports = true,
        [FromForm] bool enableHollowSupports = true,
        [FromForm] float hollowMinHeightMm = 20f,
        [FromForm] float hollowWallThicknessMm = 0.6f,
        [FromForm] string baseLatticePattern = "Grid",
        [FromForm] bool enableMiniRafts = true,
        [FromForm] float raftMarginMm = 1.5f,
        [FromForm] float raftThicknessMm = 0.3f,
        [FromForm] string materialPreset = "standard",
        [FromForm] string? manualContacts = null,
        // New V2 features from visual picker
        [FromForm] bool enableForking = false,
        [FromForm] int maxTipsPerFork = 4,
        [FromForm] float forkClusterRadiusMm = 0f,
        [FromForm] bool enableLineContact = false,
        [FromForm] float lineContactSpacingMm = 0f,
        [FromForm] bool enableFaceContact = false,
        [FromForm] float faceGridSpacingMm = 3f,
        [FromForm] string reinforcementMode = "Pairwise",
        [FromForm] string raftMode = "MiniRafts",
        [FromForm] string fullPlateRaftPattern = "Grid",
        [FromForm] bool enableDrainageAwareSupports = false,
        [FromForm] bool enableForceDrivenPlacement = false,
        [FromForm] bool enableFillets = true,
        [FromForm] float fullPlateRaftHeightMm = 1.5f,
        [FromForm] float fullPlateRaftWallThicknessMm = 0.4f,
        [FromForm] float fullPlateRaftCellSizeMm = 3.0f,
        [FromForm] float raftAreaRatioPct = 115f,
        [FromForm] float raftSlopeDeg = 45f,
        [FromForm] float gridCellMm = 2.0f,
        [FromForm] float gridStrutMm = 0.4f,
        // Advanced Settings (ChiTuBox-style manual sizing)
        [FromForm] string sizingMode = "auto",
        [FromForm] string supportPreset = "custom",
        [FromForm] string topTouchShape = "sphere",
        [FromForm] float? topContactDepthMm = null,
        [FromForm] float? topTipUpperDiaMm = null,
        [FromForm] float? topTipLowerDiaMm = null,
        [FromForm] float? topTipAngleDeg = null,
        [FromForm] string topConnectionShape = "cone",
        [FromForm] float? topConnectionLengthMm = null,
        [FromForm] float? middlePillarDiaMm = null,
        [FromForm] string middlePillarShape = "cylinder",
        [FromForm] float? bottomBaseDiaMm = null,
        [FromForm] float? bottomBaseThicknessMm = null,
        [FromForm] float? raftCustomThicknessMm = null,
        // User transforms are baked into STL vertices by the frontend — no rotation/scale params
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        // Parse manual contacts from JSON
        List<SupportEngineV2.EngineConfig.ManualContact>? manualContactList = null;
        if (!string.IsNullOrEmpty(manualContacts))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<ManualContactDto>>(manualContacts);
                if (parsed?.Count > 0)
                    manualContactList = parsed.Select(c => new SupportEngineV2.EngineConfig.ManualContact
                    {
                        FrontendId = c.id,
                        Position = new System.Numerics.Vector3(c.x, c.y, c.z),
                        Normal = new System.Numerics.Vector3(c.nx, c.ny, c.nz),
                        TipDiameterMm = c.tipDiameterMm,
                        ShaftDiameterMm = c.shaftDiameterMm,
                        BaseDiameterMm = c.baseDiameterMm,
                        TouchShape = c.touchShape,
                        ConnectionShape = c.connectionShape,
                        PillarShape = c.pillarShape,
                    }).ToList();
            }
            catch { /* ignore parse errors */ }
        }

        var orient = PrinterOrientation.BottomUp;
        if (!string.IsNullOrEmpty(printerId) && Guid.TryParse(printerId, out var pid))
        {
            var printer = await _printerRepo.GetByIdAsync(pid, ct);
            if (printer is not null) orient = printer.Orientation;
        }
        else if (Enum.TryParse<PrinterOrientation>(orientation, true, out var o)) orient = o;

        // Parse lattice pattern
        var lattice = LatticeBase.LatticePattern.Grid;
        if (Enum.TryParse<LatticeBase.LatticePattern>(baseLatticePattern, true, out var lp))
            lattice = lp;

        // Parse model file — auto-detects binary STL, ASCII STL, OBJ, 3MF
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        // B2: On large meshes, recompute normals from vertex winding to guard against
        // flipped/zero normals that cause mis-detected overhangs
        if (mesh.TriangleCount > 50_000)
            mesh = mesh.RecomputeNormals();

        // Parse reinforcement mode
        var reinfMode = HybridSlicer.Infrastructure.Resin.Routing.ReinforcementMode.Pairwise;
        if (Enum.TryParse<HybridSlicer.Infrastructure.Resin.Routing.ReinforcementMode>(reinforcementMode, true, out var rm))
            reinfMode = rm;

        // Parse raft mode
        var raftModeEnum = HybridSlicer.Infrastructure.Resin.RaftMode.MiniRafts;
        if (Enum.TryParse<HybridSlicer.Infrastructure.Resin.RaftMode>(raftMode, true, out var rmE))
            raftModeEnum = rmE;

        // Parse full-plate raft pattern — accept both "Honeycomb" (enum name) and "Hex" (shorthand)
        var fpRaftPattern = LatticeBase.LatticePattern.Grid;
        if (Enum.TryParse<LatticeBase.LatticePattern>(fullPlateRaftPattern, true, out var fpRp))
            fpRaftPattern = fpRp;
        else if (string.Equals(fullPlateRaftPattern, "Hex", StringComparison.OrdinalIgnoreCase))
            fpRaftPattern = LatticeBase.LatticePattern.Honeycomb;

        // Parse advanced settings enums
        var sizingModeEnum = SupportSizingMode.Auto;
        Enum.TryParse<SupportSizingMode>(sizingMode, true, out sizingModeEnum);
        var presetEnum = SupportPreset.Custom;
        Enum.TryParse<SupportPreset>(supportPreset, true, out presetEnum);
        var touchShapeEnum = TouchShape.Sphere;
        Enum.TryParse<TouchShape>(topTouchShape, true, out touchShapeEnum);
        var connShapeEnum = SupportShape.Cone;
        Enum.TryParse<SupportShape>(topConnectionShape, true, out connShapeEnum);
        var pillarShapeEnum = SupportShape.Cylinder;
        Enum.TryParse<SupportShape>(middlePillarShape, true, out pillarShapeEnum);

        // Step 5: Check cancellation before expensive generation; surface exceptions
        ct.ThrowIfCancellationRequested();
        SupportEngineV2.EngineResult result;
        try
        {
        result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = orient,
            OverhangAngleDeg = (float)overhangAngleDeg,
            DensityFactor = (float)density,
            PinRadiusMm = pinRadius,
            BackRadiusMm = backRadius,
            PillarRadiusMm = pillarRadius,
            BaseRadiusMm = baseRadius,
            WideningFactor = wideningFactor,
            EnableInterconnections = enableInterconnections && reinfMode != HybridSlicer.Infrastructure.Resin.Routing.ReinforcementMode.None,
            InterconnectDistMm = interconnectDistMm,
            EnableTreeSupports = enableTreeSupports,
            EnableHollowSupports = enableHollowSupports,
            HollowMinHeightMm = hollowMinHeightMm,
            HollowWallThicknessMm = hollowWallThicknessMm,
            BaseLatticePattern = lattice,
            EnableMiniRafts = raftModeEnum == HybridSlicer.Infrastructure.Resin.RaftMode.MiniRafts,
            RaftMarginMm = raftMarginMm,
            RaftThicknessMm = raftCustomThicknessMm ?? raftThicknessMm,
            ManualContacts = manualContactList,
            // New features from visual picker
            EnableForking = enableForking,
            MaxTipsPerFork = maxTipsPerFork,
            ForkClusterRadiusMm = forkClusterRadiusMm,
            EnableLineContact = enableLineContact,
            LineContactSpacingMm = lineContactSpacingMm,
            EnableFaceContact = enableFaceContact,
            FaceGridSpacingMm = faceGridSpacingMm,
            ReinforcementMode = reinfMode,
            RaftMode = raftModeEnum,
            FullPlateRaftPattern = fpRaftPattern,
            FullPlateRaftHeightMm = fullPlateRaftHeightMm,
            FullPlateRaftWallThicknessMm = fullPlateRaftWallThicknessMm,
            FullPlateRaftCellSizeMm = fullPlateRaftCellSizeMm,
            RaftAreaRatioPct = raftAreaRatioPct,
            RaftSlopeDeg = raftSlopeDeg,
            GridCellMm = gridCellMm,
            GridStrutMm = gridStrutMm,
            EnableDrainageAwareSupports = enableDrainageAwareSupports,
            EnableForceDrivenPlacement = enableForceDrivenPlacement,
            EnableFillets = enableFillets,
            // Advanced Settings
            SizingMode = sizingModeEnum,
            Preset = presetEnum,
            TopTouchShape = touchShapeEnum,
            TopContactDepthMm = topContactDepthMm,
            TopTipUpperDiaMm = topTipUpperDiaMm,
            TopTipLowerDiaMm = topTipLowerDiaMm,
            TopTipAngleDeg = topTipAngleDeg,
            TopConnectionShape = connShapeEnum,
            TopConnectionLengthMm = topConnectionLengthMm,
            MiddlePillarDiaMm = middlePillarDiaMm,
            MiddlePillarShape = pillarShapeEnum,
            BottomBaseDiaMm = bottomBaseDiaMm,
            BottomBaseThicknessMm = bottomBaseThicknessMm,
            RaftCustomThicknessMm = raftCustomThicknessMm,
            // Adhesion calibration
            ResinCategory = materialPreset switch
            {
                "standard" => "Standard",
                "abs-like" or "abs" => "ABS-Like",
                "flexible" => "Flexible",
                "castable" or "wax" => "Castable",
                "ceramic" => "Ceramic",
                "water-washable" => "Water-Washable",
                _ => null,
            },
        });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Generation cancelled by client" });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Support generation failed");
            return StatusCode(500, new { error = ex.Message, type = ex.GetType().Name });
        }

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
            // Manual tips that could not be routed (uncoverable)
            uncoverableManualIds = result.UncoverableManualIds,
            // Overhang points dropped by the 500-point cap (0 = none dropped)
            droppedByCapCount = result.DroppedByCapCount,
            // Centering offset — frontend must apply same offset to align supports with model
            meshOffset = new { x = result.MeshCenteringOffset.X, y = result.MeshCenteringOffset.Y, z = result.MeshCenteringOffset.Z },
            // Per-manual-support meshes (real generated triangles) for ground-truth comparison.
            // Every manual support with a valid pinhead + route produces faces. Supports that
            // fail pinhead/emission are not in ManualSupportMeshes — they're in uncoverableManualIds.
            manualSupportMeshes = result.ManualSupportMeshes
                .ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value.ToStlBinary())),

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
                // Base64-encoded STL for direct rendering (only for meshes <100k faces,
                // larger meshes must be fetched via /mesh endpoint to avoid JSON size limits)
                stlBase64 = result.SupportMesh.FaceCount > 0 && result.SupportMesh.FaceCount < 100000
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

    // ── BVH cache for interactive single-support calls ──
    // Caches only geometry-derived data (BVH, centered mesh, centering offset).
    // Config is NOT cached — it varies per call (preset tier, orientation).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (AabbBvh bvh, StlMesh mesh, System.Numerics.Vector3 offset)> _bvhCache = new();

    /// <summary>
    /// Compute a single support in real time — same engine as auto, for ONE tip.
    /// Uses cached BVH for speed (target &lt;16ms compute).
    /// </summary>
    [HttpPost("single")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> ComputeSingle(
        [FromForm] IFormFile stlFile,
        [FromForm] float tipX, [FromForm] float tipY, [FromForm] float tipZ,
        [FromForm] float normalX, [FromForm] float normalY, [FromForm] float normalZ,
        [FromForm] string orientation = "BottomUp",
        [FromForm] float pinRadius = 0.2f, [FromForm] float pillarRadius = 0.5f, [FromForm] float baseRadius = 2.0f,
        [FromForm] string? existingRoutesJson = null,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        // Cache key: hash of the STL data — caches BVH + centered mesh only, NOT config
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))[..16];

        if (!_bvhCache.TryGetValue(hash, out var cached))
        {
            var rawMesh = StlMesh.FromBinary(data);
            // Center mesh (same as Generate Step 0)
            float meshW = rawMesh.Max.X - rawMesh.Min.X, meshD = rawMesh.Max.Y - rawMesh.Min.Y;
            float offX = -(rawMesh.Min.X + meshW / 2), offY = -(rawMesh.Min.Y + meshD / 2), offZ = -rawMesh.Min.Z;
            rawMesh = rawMesh.Transform(new System.Numerics.Vector3(offX, offY, offZ), 1.0f);
            var bvh = AabbBvh.Build(rawMesh);
            cached = (bvh, rawMesh, new System.Numerics.Vector3(offX, offY, offZ));
            _bvhCache[hash] = cached;
        }

        // Build config fresh from THIS call's parameters (never stale from cache)
        var orient2 = PrinterOrientation.BottomUp;
        if (Enum.TryParse<PrinterOrientation>(orientation, true, out var o2)) orient2 = o2;
        var config = new SupportEngineV2.EngineConfig
        {
            Orientation = orient2,
            PinRadiusMm = pinRadius,
            PillarRadiusMm = pillarRadius,
            BaseRadiusMm = baseRadius,
        };

        // Apply centering to the tip position (same offset as mesh centering)
        var tipPos = new System.Numerics.Vector3(tipX, tipY, tipZ) + cached.offset;
        var tipNormal = new System.Numerics.Vector3(normalX, normalY, normalZ);

        var result = SupportEngineV2.ComputeSingleSupport(
            tipPos, tipNormal, cached.bvh, cached.mesh, config,
            overridePillarRadius: pillarRadius > 0 ? pillarRadius : null,
            overrideBaseRadius: baseRadius > 0 ? baseRadius : null);

        // Return mesh as base64 + status
        var stlBytes = result.Mesh.FaceCount > 0 ? result.Mesh.ToStlBinary() : Array.Empty<byte>();

        return Ok(new
        {
            status = result.Status,
            pillarAxis = new { x = result.PillarAxis.X, y = result.PillarAxis.Y, z = result.PillarAxis.Z },
            baseZ = result.BaseZ,
            bundledIntoId = result.BundledIntoId,
            computeMs = result.ComputeMs,
            meshOffset = new { x = cached.offset.X, y = cached.offset.Y, z = cached.offset.Z },
            mesh = new
            {
                faces = result.Mesh.FaceCount,
                stlBase64 = stlBytes.Length > 0 ? Convert.ToBase64String(stlBytes) : null,
            },
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

    /// <summary>
    /// Render a top-down support density heatmap.
    /// Green = well-supported, red dots = uncovered overhangs.
    /// </summary>
    [HttpPost("heatmap")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> SupportHeatmap(
        [FromForm] IFormFile stlFile,
        [FromForm] int resX = 800, [FromForm] int resY = 800,
        [FromForm] float buildWidthMm = 200, [FromForm] float buildDepthMm = 200,
        [FromForm] double density = 0.5,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");
        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = (float)density });
        var positions = result.Points.Select(p => new System.Numerics.Vector2(p.Position.X, p.Position.Y)).ToList();
        var png = SupportHeatmapRenderer.Render(positions, null, resX, resY, buildWidthMm, buildDepthMm);
        return File(png, "image/png", "support_heatmap.png");
    }

    /// <summary>
    /// Evaluate multiple orientations and return the top 5 sorted by overhang score.
    /// Lower score = fewer supports needed = better orientation for printing.
    /// </summary>
    [HttpPost("auto-orient")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> AutoOrient(
        [FromForm] IFormFile stlFile,
        [FromForm] int candidateCount = 36,
        [FromForm] int topN = 5,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var results = AutoOrientOptimizer.Evaluate(mesh, new AutoOrientOptimizer.OrientConfig
        {
            CandidateCount = candidateCount,
        }, topN);

        return Ok(new
        {
            engine = "v2-auto-orient",
            candidatesEvaluated = candidateCount,
            orientations = results.Select(r => new
            {
                rotation = new { r.Rotation.X, r.Rotation.Y, r.Rotation.Z, r.Rotation.W },
                overhangAreaMm2 = r.OverhangAreaMm2,
                supportVolumeMl = r.SupportVolumeMl,
                estimatedSupports = r.EstimatedSupports,
                score = r.Score,
                description = r.Description,
            }),
        });
    }

    /// <summary>
    /// Analyze mesh for resin traps (concave pockets that trap uncured resin)
    /// and suggest drain hole positions.
    /// </summary>
    [HttpPost("drain-holes")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> SuggestDrainHoles(
        [FromForm] IFormFile stlFile,
        [FromForm] float holeDiameterMm = 2.5f,
        [FromForm] float minTrapVolumeMm3 = 50f,
        [FromForm] float layerHeightMm = 1.0f,
        CancellationToken ct = default)
    {
        if (stlFile is null || stlFile.Length == 0) return BadRequest("STL file required.");

        byte[] data;
        using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }

        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var holes = DrainHolePlacer.Suggest(mesh, new DrainHolePlacer.DrainConfig
        {
            HoleDiameterMm = holeDiameterMm,
            MinTrapVolumeMm3 = minTrapVolumeMm3,
            LayerHeightMm = layerHeightMm,
        });

        return Ok(new
        {
            engine = "v2-drain-holes",
            totalTrapsFound = holes.Count,
            drainHoles = holes.Select(h => new
            {
                position = new { x = h.Position.X, y = h.Position.Y, z = h.Position.Z },
                normal = new { x = h.Normal.X, y = h.Normal.Y, z = h.Normal.Z },
                diameterMm = h.DiameterMm,
                trapVolumeMm3 = h.TrapVolumeMm3,
                reason = h.Reason,
            }),
        });
    }

    /// <summary>Run ALL analysis checks on a model in one pass.</summary>
    [HttpPost("analyze")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Analyze(
        [FromForm] IFormFile stlFile,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        var report = HybridSlicer.Infrastructure.Resin.Analysis.ComprehensiveModelAnalyzer.Analyze(mesh);
        return Ok(report);
    }

    /// <summary>Generate comprehensive print job report with all analysis engines.</summary>
    [HttpPost("full-report")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> FullReport(
        [FromForm] IFormFile stlFile,
        [FromForm] string resinType = "standard",
        [FromForm] int supportCount = 0,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        var report = HybridSlicer.Infrastructure.Resin.Analysis.PrintJobReportGenerator.Generate(mesh, resinType, supportCount);
        return Ok(new
        {
            score = report.Score.Total,
            grade = report.Score.Grade,
            verdict = report.Risk.Verdict,
            triangles = report.Analysis.TriangleCount,
            volumeMm3 = report.Analysis.VolumeMm3,
            overhangPct = report.Overhangs.OverhangPct,
            finishQuality = report.Finish.AvgQualityScore,
            issueCount = report.Analysis.Issues.Count,
            issues = report.Analysis.Issues,
            postProcessSteps = report.PostProcess.Steps.Count,
            elapsedMs = report.TotalElapsedMs,
        });
    }

    /// <summary>Get post-processing instructions for a print job.</summary>
    [HttpGet("post-process")]
    public IActionResult PostProcess(
        [FromQuery] string resinType = "standard",
        [FromQuery] bool hasSupports = true,
        [FromQuery] bool isHollow = false)
    {
        var plan = HybridSlicer.Infrastructure.Resin.Analysis.PostProcessingAdvisor.Generate(
            resinType, hasSupports, isHollow);
        return Ok(plan);
    }

    /// <summary>Predict island/floating geometry risks before slicing.</summary>
    [HttpPost("island-check")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> IslandCheck(
        [FromForm] IFormFile stlFile,
        [FromForm] float overhangAngleDeg = 45f,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        var result = HybridSlicer.Infrastructure.Resin.Analysis.IslandPredictor.Predict(mesh, overhangAngleDeg);
        return Ok(new
        {
            riskCount = result.Risks.Count,
            highRiskCount = result.HighRiskCount,
            lowestUnsupportedZ = result.LowestUnsupportedZ,
            risks = result.Risks.Select(r => new
            {
                zMm = r.ZMm, areaMm2 = r.AreaMm2, risk = r.Risk,
                x = r.Centroid.X, y = r.Centroid.Y, z = r.Centroid.Z,
            }),
        });
    }

    /// <summary>Check for thin walls below minimum printable thickness.</summary>
    [HttpPost("thin-wall-check")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> ThinWallCheck(
        [FromForm] IFormFile stlFile,
        [FromForm] float minThicknessMm = 0.5f,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        var bvh = HybridSlicer.Infrastructure.Resin.Spatial.AabbBvh.Build(mesh);
        var result = HybridSlicer.Infrastructure.Resin.Analysis.ThinWallDetector.Detect(bvh, mesh, minThicknessMm);
        return Ok(new
        {
            thinWallCount = result.ThinWallCount,
            minWallThicknessMm = result.MinWallThicknessMm,
            warnings = result.Warnings.Take(20).Select(w => new
            {
                x = w.Position.X, y = w.Position.Y, z = w.Position.Z,
                wallThicknessMm = w.WallThicknessMm,
            }),
        });
    }

    /// <summary>Compute per-layer peel force profile.</summary>
    [HttpPost("peel-force")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> PeelForce(
        [FromForm] IFormFile stlFile,
        [FromForm] float layerHeightMm = 0.05f,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        var profile = HybridSlicer.Infrastructure.Resin.Analysis.PeelForceProfiler.Compute(mesh, layerHeightMm);
        return Ok(new
        {
            maxPeelForceN = profile.MaxPeelForceN,
            maxPeelForceZ = profile.MaxPeelForceZ,
            avgPeelForceN = profile.AvgPeelForceN,
            highStressLayers = profile.HighStressLayers,
            totalLayers = profile.Layers.Count,
        });
    }

    /// <summary>Detect suction cup geometry that risks print failure.</summary>
    [HttpPost("suction-check")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> SuctionCheck(
        [FromForm] IFormFile stlFile,
        CancellationToken ct = default)
    {
        if (stlFile is null) return BadRequest("STL required.");
        byte[] data; using (var ms = new MemoryStream()) { await stlFile.CopyToAsync(ms, ct); data = ms.ToArray(); }
        var mesh = StlMesh.FromFile(data, stlFile.FileName);
        var warnings = HybridSlicer.Infrastructure.Resin.Analysis.SuctionCupDetector.Detect(mesh);
        return Ok(new
        {
            engine = "suction-check",
            warningCount = warnings.Count,
            warnings = warnings.Select(w => new
            {
                x = w.Position.X, y = w.Position.Y, z = w.Position.Z,
                depthMm = w.DepthMm, areaRatio = w.AreaRatio,
                severity = w.Severity, description = w.Description,
            }),
        });
    }

    /// <summary>
    /// Export both model and support meshes as a combined ZIP containing two STL files.
    /// Compatible with 3MF-style workflows where model and supports are separate objects.
    /// </summary>
    [HttpPost("export-combined")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> ExportCombined(
        [FromForm] IFormFile stlFile,
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

        // Create ZIP with model.stl and supports.stl
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            // Model STL (original, re-centered)
            var modelEntry = archive.CreateEntry("model.stl");
            using (var entryStream = modelEntry.Open())
                await entryStream.WriteAsync(data, ct);

            // Support STL (watertight mesh)
            var supportEntry = archive.CreateEntry("supports.stl");
            using (var entryStream = supportEntry.Open())
            {
                var supportStl = result.SupportMesh.ToStlBinary();
                await entryStream.WriteAsync(supportStl, ct);
            }

            // Metadata JSON
            var metaEntry = archive.CreateEntry("support_info.json");
            using (var writer = new StreamWriter(metaEntry.Open()))
            {
                await writer.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    engine = "v2",
                    supports = result.ValidSupports,
                    volumeMl = result.TotalSupportVolumeMm3 / 1000f,
                    meshFaces = result.SupportMesh.FaceCount,
                    orientation = orient.ToString(),
                }));
            }
        }

        zipStream.Position = 0;
        return File(zipStream.ToArray(), "application/zip", "model_with_supports.zip");
    }

    private record ManualContactDto(float x, float y, float z, float nx, float ny, float nz,
        float? tipDiameterMm = null, float? shaftDiameterMm = null, float? baseDiameterMm = null,
        string? id = null,
        string? touchShape = null, string? connectionShape = null, string? pillarShape = null);
}
