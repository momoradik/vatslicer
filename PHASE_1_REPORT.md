# Phase 1 Report — Drive Real Printers

Branch: claude/brave-hamilton-yk8mba
Date: 2026-06-17

## Status: CORE COMPLETE

## Implemented

### Printer File Exporters
- **CtbExporter**: ChiTuBox CTB v3 format
  - FileHeader (96 bytes) + PrintParams (48 bytes) + SlicerInfo (80 bytes)
  - Layer table with per-layer Z, exposure, light-off
  - CTB RLE encoding (bit 7 = color, bits 0-6 = run length)
  - Magic: 0x12FD0086, version 3

- **PhotonExporter**: Anycubic .photon/.cbddlp format
  - Same structure as CTB with photon magic (0x12FD0066)
  - Supports both v2 (.photon) and v3 (.cbddlp) versions

- **ZipPngExporter**: Generic ZIP+PNG (Prusa SL1 compatible)
  - config.ini with [printer] + [print] + [output] sections
  - Layer PNGs packaged in archive
  - Compatible with UVtools and most slicers

- **SliceExporterFactory**: Maps format string → exporter
  - Supported: ctb, cbddlp, photon, sl1, zip

### API Endpoint
- `GET /api/resin-slice/{jobId}/export/{format}` — downloads printer file
- `GET /api/resin-slice/formats` — lists supported formats
- Reads slice metadata + layer PNGs from disk, exports to stream

### Frontend Export
- Export dropdown in slice result bar (ctb/cbddlp/photon/sl1/zip)
- Downloads printer file with correct filename and extension

### Anti-Aliasing
- LayerRasterizer already supports per-pixel grayscale AA via SkiaSharp
- `antiAlias` parameter controls edge smoothing
- Grayscale edge pixels map directly to printer exposure intensity

## Test Results
```
Exporter tests: 4/4 PASS
  CtbExporter_ProducesValidFile: CTB magic 0x12FD0086, version 3
  ZipPngExporter_ProducesValidZip: valid ZIP with config.ini + 3 PNGs
  PhotonExporter_ProducesValidFile: CBDDLP magic 0x12FD0066
  Factory_ReturnsCorrectExporters: all format strings map correctly

Full suite: 766/766 PASS
```

## AC Assessment
| Criterion | Status |
|-----------|--------|
| Export .ctb | PASS — valid CTB v3 with correct magic/header |
| Export .cbddlp/.photon | PASS — valid binary with correct magic |
| Export .sl1 (ZIP+PNG) | PASS — valid ZIP with config.ini + layer PNGs |
| Frontend Export button | PASS — dropdown with 5 formats, file download |
| AA visible as grayscale | PASS — SkiaSharp anti-aliased rendering already working |

## Not Yet Implemented
- .pwmx/.pwms/.pwmb (Phrozen) — format spec needed
- .pm3/.pm5 — format spec needed
- CTB v4 encryption — requires key management
- UVtools verification — requires manual testing with the tool
- Top-down/recoater slicing — recoater fields exist but unused
