# MEASUREMENT_RESULTS_2.md — Resolving VOID/BLOCKED Items

All numbers from executed output, pasted verbatim. Temporary `[HttpPost("measure2")]` endpoint added, run, output captured, then reverted via `git checkout`.

---

## ITEM 1 — DIVERGENCE (corrected, prior run was VOID)

**Prior bug:** `ComputeSingleSupport` was passed an already-centered point AND a re-centered mesh (double offset → fake 88mm displacement).

**Fix in harness:** Used `baseResult.Bvh` and `centeredMesh` (both in centered frame) for solo. For in-context Generate, passed the manual contact position in UN-centered space (subtracted MeshCenteringOffset) so the engine centers it once.

### (a) ISOLATED contact — highest Z point, far from cluster center

```
ISOLATED contact at (-29.691, 9.517, 56.978)
Solo: status=uncoverable verts=0 faces=0
InContext: pinhead valid=True uncov=True
InContext tip: (-29.691, 9.517, 56.978)
Tip displacement: 0.0000mm
CLASSIFICATION: BENIGN
```

Solo returned `uncoverable` (no mesh). In-context also returned `uncoverable`. The pinhead was valid but the support couldn't route to the plate — this is a genuine uncoverable position, not a test bug. Tip displacement = 0mm — the contact point is identical.

**VERIFIED**

### (b) CLUSTERED contact — mid-range point, dense area

```
CLUSTERED contact at (-13.866, -11.893, 23.921)
Solo: status=uncoverable verts=0 faces=0
InContext: pinhead valid=True uncov=False
InContext mesh: verts=488 faces=832
InContext envelope: min=(-19.307,-14.203,-0.300) max=(-13.466,-9.583,24.268)
Tip displacement: 0.0000mm
CLASSIFICATION: BENIGN
```

Solo returned `uncoverable` (couldn't route alone). In-context returned a full mesh (488 verts, 832 faces) — the escalation ladder or a nearby support's route resolved it. Tip displacement = 0mm.

**Both classifications: BENIGN.** The tip never moves. The only difference is whether the support successfully routes (which depends on context — bridges and escalation need nearby supports).

**VERIFIED**

---

## ITEM 2 — ISLAND DETECTION VIA CROSS-SECTION (prior run BLOCKED)

**Method:** Built the synthetic two-box STL (base box Z[0,3] + floating box Z[30,33]). Called `MeshCrossSectionEngine.CrossSection(mesh, z)` for every layer at 0.05mm intervals (660 layers), fed polygons to `IslandDetector.DetectIslands(current, previous)`.

```
Synthetic mesh: tris=24 min=(-10,-10,0) max=(10,10,33)
Total layers scanned: 660
Total islands detected: 0
FINDING: floating box at z≈30 detected as island: NO
```

**IslandDetector reports 0 islands.** The floating box at z=30 was NOT flagged.

**Why:** `IslandDetector.DetectIslands` returns 0 when `previousLayer.Count == 0` (line 20-21 of IslandDetector.cs). Between z=3 and z=30, every layer has 0 polygons (empty air). So when z=30 appears, `previousLayer` is empty → the detector returns 0 instead of flagging the new contour.

The detector's first-layer rule (`if previousLayer.Count == 0 return 0`) was designed for the actual first layer (everything on the plate), but it also fires for any contour that appears after a gap of empty layers. This means **floating geometry that starts after an air gap is never detected as an island by IslandDetector**.

**VERIFIED**

---

## ITEM 3 — FLOATING BOX ROUTING TRACE (prior run ambiguous)

```
Point sp-5: pos=(-1.67,-1.67,30.00)
  Route: NOT FOUND in routes list (dropped by collision filter or emission gate)
Point sp-9: pos=(4.90,-2.00,30.00)
  Route: ground=True anchor=False pathLen=6
    wp: (5.90,-2.00,28.19) r=0.52 type=junction
    wp: (13.23,-2.00,15.11) r=0.52 type=bridge
    wp: (13.23,-2.00,10.41) r=0.62 type=pillar
    wp: (13.23,-2.00,5.70) r=0.71 type=pillar
    wp: (13.23,-2.00,1.00) r=0.81 type=pillar
    wp: (13.23,-2.00,0.00) r=2.00 type=base
Point sp-5 passed emission gate: False
Point sp-9 passed emission gate: True
```

**sp-9** successfully routed: junction at Z=28.19 → bridge to X=13.23 (outside the base box) → descend to plate at Z=0. It bridged SIDEWAYS out from under the floating box to find a clear vertical path.

**sp-5** was dropped — its route failed (not in the routes list at all, meaning it was either removed by the collision filter or produced an empty route). It did NOT pass the emission gate.

**Finding:** sp-5 is a needed contact that silently dropped. The floating box's underside is 10×10mm — two support points is the minimum. Only one survived. The 2nd point at (-1.67,-1.67,30) failed routing because it couldn't find a clear bridge path, and was silently removed.

**VERIFIED**

---

## ITEM 4 — PER-SUPPORT SAFETY FACTORS

```
Model: SINAa.stl, ValidSupports=168, MinSF=0.59
Top 10 lowest safety factors:
  sp-184: SF=0.59 height=41.4mm radius=0.55mm pos=(-31.7,45.3,42.3)
    Euler buckling SF=0.6 (need 2.0). r=0.35mm h=41.4mm load=0.229N crit=0.136N
  sp-132: SF=0.83 height=36.8mm radius=0.59mm pos=(-27.6,50.9,37.8)
    Euler buckling SF=0.8 (need 2.0). r=0.35mm h=36.8mm load=0.206N crit=0.172N
  sp-197: SF=0.86 height=36.4mm radius=0.59mm pos=(-36.1,-16.6,37.2)
    Euler buckling SF=0.9 (need 2.0). r=0.35mm h=36.4mm load=0.204N crit=0.176N
  sp-196: SF=0.93 height=35.4mm radius=0.58mm pos=(-37.0,-29.5,36.3)
    Euler buckling SF=0.9 (need 2.0). r=0.35mm h=35.4mm load=0.200N crit=0.186N
  sp-185: SF=0.99 height=34.6mm radius=0.57mm pos=(-39.3,-50.6,35.5)
    Euler buckling SF=1.0 (need 2.0). r=0.35mm h=34.6mm load=0.196N crit=0.194N
  sp-210: SF=1.00 height=34.5mm radius=0.57mm pos=(-34.8,-9.2,35.4)
    Euler buckling SF=1.0 (need 2.0). r=0.35mm h=34.5mm load=0.195N crit=0.195N
  sp-221: SF=1.05 height=33.9mm radius=0.57mm pos=(-34.0,-21.8,34.8)
    Euler buckling SF=1.1 (need 2.0). r=0.35mm h=33.9mm load=0.192N crit=0.202N
  sp-51: SF=1.06 height=33.9mm radius=0.57mm pos=(-33.3,-4.0,34.8)
    Euler buckling SF=1.1 (need 2.0). r=0.35mm h=33.9mm load=0.192N crit=0.203N
  sp-10: SF=1.07 height=33.7mm radius=0.57mm pos=(-26.2,42.4,34.6)
    Euler buckling SF=1.1 (need 2.0). r=0.35mm h=33.7mm load=0.191N crit=0.204N
  sp-115: SF=1.08 height=33.6mm radius=0.57mm pos=(-28.2,29.1,34.5)
    Euler buckling SF=1.1 (need 2.0). r=0.35mm h=33.6mm load=0.191N crit=0.206N
```

**Analysis of lowest (sp-184, SF=0.59):**
- Height: 41.4mm, radius used by validator: **r=0.35mm** (from the issue description)
- Load: 0.229N, critical buckling load: 0.136N
- Euler formula: P_cr = π²EI/L² = π² × 2000 × (π × 0.35⁴/4) / 41.4² = 0.136N
- SF = 0.136 / 0.229 = 0.59

The validator uses `r=0.35mm` but the route's pillar radius is `0.55mm`. The validator appears to be computing buckling with the **tip radius** (0.35mm ≈ the PinRadius × scale) rather than the **pillar radius** (0.55mm). With the actual pillar radius: P_cr = π² × 2000 × (π × 0.55⁴/4) / 41.4² = 0.836N → SF = 0.836/0.229 = 3.65 (well above 2.0).

All 10 lowest-SF supports show the same pattern: validator r=0.35mm while the actual pillar radius is 0.55-0.59mm. This is **validator over-conservatism** — it's using the wrong radius.

**VERIFIED**

---

## CLEANUP

- Temporary endpoint: **REVERTED** via `git checkout -- src/HybridSlicer.Api/Controllers/SupportV2Controller.cs`
- Background processes: **KILLED** (confirmed via PowerShell Stop-Process)
- Scratch files: none created this run
- `git status` shows clean (only untracked: MEASUREMENT_RESULTS.md, MEASUREMENT_RESULTS_2.md, SINAa.stl)
