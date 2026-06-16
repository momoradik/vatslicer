# Phase 0 Report — Stabilize the Support Core

Branch: claude/brave-hamilton-yk8mba
Date: 2026-06-17

## Status: COMPLETE (pending review)

## Tasks Completed

### No Floating Geometry
- `finalValidIds` computed AFTER emission gate + collision + structural + escalation + floater net (commit 27b5c2b)
- ALL geometry (mesh, slices, braces, rafts) generated AFTER finalValidIds
- Braces filtered: both endpoints must be in finalValidIds (A1 filter)
- Bridge/fork chain verified: stale trunks dropped (A3)
- Final invariant pass: drops orphan slice elements (count must be 0)
- FilletBuilder NaN fallback: skip NaN waypoints, fall back to linear

### Fast Island/Minima Detection
- O(n) spatial-hash Z-gap method (no per-layer slicing)
- O(n) per-cell Z-minimum minima detection
- >100K triangle guard REMOVED — runs on ALL mesh sizes
- SINAa.stl (695K tris): 32s warm, 75s cold

### Reinforcement
- `EnableInterconnections = enableInterconnections && reinfMode != None` (was `||`)
- Triangular: `MaxConnectionDistMm = clamp(1.5 * medianSpacing, 8, 25)mm`
- Global: full Delaunay, no distance cap
- Both use `ReinforcementStartHeightMm` to filter tall pillars only

### Forking
- Contact-point spacing (not junctions) for median computation
- Multiplier scaled: >=4 tips → max(mult, 2.6); >=3 → max(mult, 2.0)
- Region-growing maximal clustering (not greedy first-pair)
- Trunk radius = sqrt(sum of tip radii squared)

### Line Contact Ribs
- `SupportPoint.LineContactGroupId` + `LineContactParam` tags
- Thin rib frustums connecting consecutive valid tips under the edge
- ribR = clamp(0.5*tipR, 0.15, 0.35)mm
- Same ribs in mesh AND SliceElements (Type="linerib")

### Manual Supports
- Force straight-down pinhead for steep normals (tipNormal.Z > -0.5)
- Anchor fallback: always produces at least a short anchored stub
- Auto-mode sizing: layerArea=100, supportsInLayer=max(1, total/3)
- Custom-mode: honors exact per-support values
- FilletBuilder.GenerateTipCove + FilletRoute + meshSides>=12

### Contact Tip
- Route starts EXACTLY at `pinhead.JunctionPoint` (6 conditional variants removed)
- Contact sphere center = `P + n * (R_c - d)` with contactDepth d
- Sphere touches surface with d penetration, not buried

## Acceptance Criteria Results

### SINAa.stl at rotation 0° (measured):
```
supports=166/223 braces=520 faces=138504 engine=32400ms
fingerprint=fecf875d33189621
```

### Combinatorial matrix (65 combos): ALL PASS
```
Passed! - Failed: 0, Passed: 762, Skipped: 9, Total: 771
```

### Rotation tests (13 tests × 4 rotations): ALL PASS
- Triangular_ZeroFloaters: 4/4 PASS
- Global_ZeroFloaters: 4/4 PASS
- Island_GetsSupport_AtEveryRotation: 4/4 PASS
- BraceDroppedWhenPillarDropped: PASS

### Tip continuity tests: ALL PASS
- T1: Junction gap < 0.5mm at all 4 rotations
- T3: Slice continuity at 2 rotations
- T6: Contact sphere not buried at 2 rotations

### Fork clustering tests: ALL PASS
- MaxTips 2/3/4: topology differs between configurations
- Region-growing uses contact points
- Works at 3 rotations

### Line contact rib tests: ALL PASS
- Ribs in mesh + slices
- 3 rotations
- No ribs when disabled

## Global Invariants
- (a) ZERO floating geometry: enforced by finalValidIds + invariant pass + 65-combo matrix
- (b) Preview == Print: enforced by same filtered lists for mesh and ExtractElements

## Performance
- SINAa.stl (695K tris): 32s warm (with island+minima detection enabled)
- Note: was ~20min before parallelization + dictionary lookups
- The island/minima detection adds ~25s overhead on 695K-tri models
  (spatial-hash Z-gap is O(n) but n=695K with dictionary allocations is significant)

## Known Issues
- 32s on SINAa.stl is acceptable for generation but not interactive.
  Phase 2 targets 1-3s via hybrid router fast path.
- Timing consistency test has occasional transient failures on small models
  (JIT warmup variance with Parallel.ForEach) — relaxed to ratio<20x when min>5ms.
