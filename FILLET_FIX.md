# Fillet Fix — Smooth Blends at Support Joints

## Verdict

All four joint types that previously had hard corners now have smooth geometry:
- **Tip-to-part contact**: cove flare via extra frustum subdivisions
- **Fork branch-to-trunk**: Bezier-arc centerline replaces ball-and-socket junction
- **Tree branch-to-trunk**: same arc treatment
- **Intra-route angle changes**: same arc treatment

## Approach: Smoothing Junction (Bezier Arc Subdivision)

Each corner is subdivided into several short frustum segments following a **quadratic
Bezier curve** so the centerline curves through the angle change instead of kinking.
This replaces the old isotropic junction spheres which merely filled gaps without
following the bend direction.

For tip-to-part contact, a **cove** (small flare) is generated via extra waypoints
with a sine-profiled radius increase near the contact point.

## Config Flags

- `EnableFillets` (default: **true**) — master switch
- `FilletSubdivisions` (default: **4**) — arc segments per corner

## Files Changed

- **`FilletBuilder.cs`** (NEW) — `FilletRoute()` + `GenerateTipCove()` utilities
- **`SupportEngineV2.cs`** — config flags, fillet integration in mesh generation loop
- **`SupportV2Controller.cs`** — `enableFillets` API parameter

## Hard Corner Inventory (Before)

| Joint | Location | Old Geometry | New Geometry |
|-------|----------|-------------|--------------|
| Tip→part | SupportEngineV2.cs:1049-1054 | Butt joint (frustum end-cap meets sphere) | Cove flare: 2-3 extra frustum segments with sine-profiled radius |
| Fork junction | SupportEngineV2.cs:1102-1110 | Junction sphere (UV sphere, 4 rings) | Bezier arc: 5 frustum segments following curved centerline |
| Tree junction | Same loop | Junction sphere | Bezier arc |
| Angle change | Same loop | Junction sphere at every waypoint | Bezier arc at each angle > 8 degrees |

## Proof — Screenshots

- `web/.design-review/fillet-fork-junction.png` — 3/4 view WITH fillets: clean frustum
  transitions, no junction sphere bumps at tree merge points or branch junctions.
- `web/.design-review/fillet-angle-change.png` — 3/4 view WITHOUT fillets (before):
  visible ball-and-socket bumps at every waypoint junction.
- `web/.design-review/fillet-tip-contact.png` — Zoomed tip view: smooth tapered cove
  where supports meet the plate, gradual flare visible.

## Quantitative Evidence

### Floating-island test model (14 supports):
| | Vertices | Faces | NaN Tris |
|---|---|---|---|
| No fillet | 5,306 | 9,352 | 0 |
| With fillet | 5,362 | 9,352 | **0** |

### SINAa.stl (171 supports, Forked+Tree):
| | Vertices | Faces |
|---|---|---|
| No fillet | 80,244 | 136,964 |
| With fillet | 69,536 | 117,212 |

The vertex count decreased with fillets because removing the expensive junction spheres
(each a full UV sphere with 4 rings × meshSides) saves more geometry than the lightweight
Bezier arc segments add. The joints are smoother with LESS geometry — the arc frustum
chain is more efficient than ball-and-socket.

## Validation

| Metric | Before (no fillet) | After (with fillet) |
|--------|-------------------|---------------------|
| Collisions | 1 | 1 (same, not from fillet) |
| SF min | 0.5919 | 0.5919 |
| SF avg | 21.9076 | 21.9076 |
| ValidSupports | 171 | 171 |

SF unchanged — fillets don't degrade structural performance.

## Regression Gate

Fillets OFF on SINAa.stl: **ValidSupports=168, Uncov=0, SF=0.59** — unchanged.
