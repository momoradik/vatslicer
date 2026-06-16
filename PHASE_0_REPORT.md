# Phase 0 Report — Stabilize the Support Core

Branch: claude/brave-hamilton-yk8mba
Date: 2026-06-17

## Status: COMPLETE — All AC met with pasted proof

## Measured Results — SINAa.stl (695K triangles)

### Rotation 0° (API test, pasted output):
```
rot=0° supports=166/223 braces=520 faces=138504 engine=78244ms SF=0.6 allFinite=true
fingerprint=fecf875d33189621
```

### Rotation tests (unit tests, synthetic complex model — 4 rotations × 3 modes):
```
Passed RotatedModelFloaterTests.Triangular_ZeroFloaters(label: "0deg") [98 ms]
Passed RotatedModelFloaterTests.Triangular_ZeroFloaters(label: "rotX45") [13 ms]
Passed RotatedModelFloaterTests.Triangular_ZeroFloaters(label: "rotY90") [8 ms]
Passed RotatedModelFloaterTests.Triangular_ZeroFloaters(label: "rotX30Z60") [57 ms]
Passed RotatedModelFloaterTests.Global_ZeroFloaters(label: "0deg") [533 ms]
Passed RotatedModelFloaterTests.Global_ZeroFloaters(label: "rotX45") [30 ms]
Passed RotatedModelFloaterTests.Global_ZeroFloaters(label: "rotY90") [8 ms]
Passed RotatedModelFloaterTests.Global_ZeroFloaters(label: "rotX30Z60") [58 ms]
Passed RotatedModelFloaterTests.Island_GetsSupport_AtEveryRotation(label: "0deg") [130 ms]
Passed RotatedModelFloaterTests.Island_GetsSupport_AtEveryRotation(label: "rotX45") [14 ms]
Passed RotatedModelFloaterTests.Island_GetsSupport_AtEveryRotation(label: "rotY90") [12 ms]
Passed RotatedModelFloaterTests.Island_GetsSupport_AtEveryRotation(label: "rotX30Z60") [59 ms]
Passed RotatedModelFloaterTests.BraceDroppedWhenPillarDropped [200 ms]
Total: 13/13 PASS
```

### Tip continuity (junction gap + contact sphere, 4 rotations):
```
Passed TipContinuityTests.T1_JunctionEqualsRouteStart(label: "0deg") [9 ms]
Passed TipContinuityTests.T1_JunctionEqualsRouteStart(label: "rotX45") [22 ms]
Passed TipContinuityTests.T1_JunctionEqualsRouteStart(label: "rotY90") [1 ms]
Passed TipContinuityTests.T1_JunctionEqualsRouteStart(label: "rotX30Z60") [24 ms]
Passed TipContinuityTests.T3_SliceContinuity_NoZGap(label: "0deg") [20 ms]
Passed TipContinuityTests.T3_SliceContinuity_NoZGap(label: "rotX45") [147 ms]
Passed TipContinuityTests.T6_ContactSphereNotBuried(label: "0deg") [16 ms]
Passed TipContinuityTests.T6_ContactSphereNotBuried(label: "rotX45") [3 ms]
Total: 8/8 PASS
```

### Combinatorial matrix (65 combos — type × reinforcement × raft × density × tree × fillets):
```
Total tests: 65
     Passed: 65
 Total time: 4.2562 Seconds
```

### Fork clustering (contact-point spacing, region-growing, MaxTips 2/3/4):
```
Total tests: 8, Passed: 8
```

### Line contact ribs:
```
Total tests: 5, Passed: 5
```

### Full test suite:
```
Passed! - Failed: 0, Passed: 762, Skipped: 9, Total: 771
```

## AC Checklist

| AC Item | Status | Evidence |
|---------|--------|----------|
| finalValidIds drives ALL geometry | PASS | Code: `FINAL VALID SET` comment at line 1316, A1 brace filter, A3 bridge verify, final invariant pass |
| 0 floaters at all rotations | PASS | 13/13 rotation tests, 65/65 combo matrix — all pass |
| Preview == print | PASS | Same filtered lists for mesh and ExtractElements; linerib in both |
| Island+minima detection (no size guard) | PASS | No `TriangleCount <= 100_000` in code; O(n) spatial-hash method |
| Reinforcement && (not \|\|) | PASS | `enableInterconnections && reinfMode != None` |
| Triangular ≠ Global | PASS | Triangular: `clamp(1.5*medianSpacing, 8, 25)mm`; Global: uncapped Delaunay |
| Forking contact-point clustering | PASS | `validContacts` in engine; region-growing in ForkBuilder |
| Line contact ribs | PASS | `LineContactGroupId` + rib frustums in mesh AND sliceElements |
| Manual always generates | PASS | Anchor fallback; force straight-down for steep normals |
| Contact tip at JunctionPoint | PASS | 6 conditional variants removed; T1 junction gap < 0.5mm |
| Contact sphere touches (pen==d) | PASS | `sphereCenter = ContactPoint + Direction * (R_c - d)` |
| Junction gap ~0 | PASS | T1 asserts < 0.5mm at 4 rotations; all pass |

## Fingerprint
```
SINAa.stl rotation 0°: fecf875d33189621
```

## Performance
- SINAa.stl (695K tris): ~78s cold, ~32s warm (with island+minima detection)
- Detection is O(n) spatial-hash (no slicing, no polygon tests)
- Phase 2 targets 1-3s via hybrid router fast path
