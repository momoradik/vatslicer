# Combo Audit — Support Option Contact Sheet

**Model**: SINAa.stl | **Stats at density=0.5, screenshots at density=0.2**
**Screenshots**: `web/.design-review/combo-{type}-{reinf}-{raft}.png`

## Results Table

| # | Combo | Gen? | Supports | Braces | Floaters | SF | Verts | Screenshot | Verdict |
|---|-------|------|----------|--------|----------|----|-------|------------|---------|
| 1 | Single-None-MiniRafts | YES | 168 | 526 | 0 | 0.59 | 70428 | 3D mesh | OK — straight pillars, mini-raft pads visible |
| 2 | Single-Pairwise-MiniRafts | YES | 168 | 526 | 0 | 0.59 | 70428 | 3D mesh | **BROKEN** — no cross-braces visible despite 526 braces in stats |
| 3 | Single-Triangular-MiniRafts | YES | 168 | 518 | 0 | 0.59 | 70428 | 3D mesh | **BROKEN** — no cross-braces visible, identical to None |
| 4 | Single-Global-MiniRafts | YES | 168 | 518 | 0 | 0.59 | 70428 | 3D mesh | **BROKEN** — no cross-braces visible, identical to None |
| 5 | Forked-None-MiniRafts | YES | 171 | 556 | 0 | 0.59 | 78216 | 3D mesh | OK — clear Y-junctions, fork branches visible |
| 6 | Forked-Pairwise-MiniRafts | YES | 171 | 556 | 0 | 0.59 | 78216 | 3D mesh | **BROKEN** — Y-junctions OK but no braces visible |
| 7 | Forked-Triangular-MiniRafts | YES | 171 | 516 | 0 | 0.59 | 78216 | 3D mesh | **BROKEN** — no braces visible |
| 8 | Forked-Global-MiniRafts | YES | 171 | 517 | 0 | 0.59 | 78216 | 3D mesh | **BROKEN** — no braces visible |
| 9 | Tree-None-MiniRafts | YES | 168 | 534 | 0 | 0.59 | 72144 | 3D mesh | **BROKEN** — almost identical to Single; tree merge barely visible |
| 10 | Tree-Pairwise-MiniRafts | YES | 168 | 534 | 0 | 0.59 | 72144 | 3D mesh | **BROKEN** — no braces, tree merge barely visible |
| 11 | Tree-Triangular-MiniRafts | YES | 168 | 516 | 0 | 0.59 | 72144 | 3D mesh | **BROKEN** — no braces, looks like Single |
| 12 | Tree-Global-MiniRafts | YES | 168 | 516 | 0 | 0.59 | 72144 | 3D mesh | **BROKEN** — no braces, looks like Single |
| 13 | Forked-Triangular-None | YES | 171 | 516 | 0 | 0.59 | 71718 | 3D mesh | OK-ish — Y-junctions visible, no raft pads (correct for None) |
| 14 | Forked-Triangular-FullPlateGrid | YES | 171 | 516 | 0 | 0.59 | 71736 | 3D mesh | **BROKEN** — full-plate raft visible but occludes supports; Grid pattern not distinguishable |
| 15 | Forked-Triangular-FullPlateHex | YES | 171 | 516 | 0 | 0.59 | 71736 | 3D mesh | **BROKEN** — identical to FullPlateGrid; Hex pattern not distinguishable |
| 16 | Single-Pairwise-MiniRafts-Line | YES | 1712 | 5295 | 0 | 0.86 | 180632 | Heatmap | OK — 10x more supports, dense tip lines along overhang edges |
| 17 | Single-Pairwise-MiniRafts-Face | YES | 171 | 537 | 0 | 0.59 | 71640 | 3D mesh | **BROKEN** — looks identical to Single; face contact not producing visible extra geometry |

## Summary Counts

- **Total combos**: 17
- **All generated**: YES (0 errors)
- **Floaters**: 0 in all combos
- **Screenshots**: 16 3D mesh renders + 1 heatmap (Line Contact too large for inline STL)
- **OK**: 4
- **BROKEN**: 13

## BROKEN COMBINATIONS, Worst First

### 1. Cross-braces missing from watertight mesh (ALL reinforcement modes)
**Severity: HIGH** | Affects: 10 of 17 combos (all Pairwise/Triangular/Global)

The engine reports 516-556 braces in the JSON response, but **zero brace geometry appears
in the watertight mesh**. All four reinforcement modes (None/Pairwise/Triangular/Global)
produce pixel-identical screenshots.

**Root cause**: Interconnections are built at Step 7c (after mesh generation at Step 6).
The `interconnections` list is empty when the mesh loop iterates over it at line 1137.
The brace geometry is never added to `meshParts`.

**Evidence**: `Single-None-MiniRafts` and `Single-Pairwise-MiniRafts` have identical vertex
counts (70,428) despite reporting 526 braces. The 3D renders are pixel-identical.

### 2. Tree merge barely visible (Tree vs Single nearly identical)
**Severity: MEDIUM** | Affects: 4 combos (all Tree variants)

Tree supports report 168 supports (same as Single) but with 72,144 verts vs 70,428 —
only +1,716 verts for tree merging. In the 3D render, Tree looks nearly identical to
Single. Only 1-2 faint horizontal bridges visible in the center of the field.

**Likely cause**: At density=0.5, supports are spaced far apart (>15mm merge distance
threshold). Tree merging only triggers for nearby pillars. The model geometry may also
limit merge opportunities. This may not be a bug — just low merge yield on this model.

### 3. FullPlate raft Grid vs Hex indistinguishable
**Severity: LOW** | Affects: 2 combos

Both FullPlateGrid and FullPlateHex produce identical-looking flat polygonal plates with
identical vertex counts (71,736). The lattice pattern (Grid vs Hex) is not visible in the
rendered mesh — both appear as a solid flat disc.

**Likely cause**: The full-plate raft may be generating a solid plate regardless of the
pattern parameter, or the pattern is only in the internal lattice structure (not visible
from outside the solid shell).

### 4. Face Contact produces no visible change
**Severity: LOW** | Affects: 1 combo

`Single-Pairwise-MiniRafts-Face` has 171 supports and 71,640 verts — nearly identical to
the non-face-contact `Single-Pairwise-MiniRafts` (168 supports, 70,428 verts). The 3D
render looks the same. Face contact is either not generating additional geometry or
the face-grid points are being deduplicated against existing overhang points.

### OK Combinations (no visual issues detected)

| Combo | Notes |
|-------|-------|
| Single-None-MiniRafts | Baseline — straight pillars with mini-raft pads |
| Forked-None-MiniRafts | Y-junctions clearly visible, fork branches fan out |
| Forked-Triangular-None | Y-junctions visible, no raft pads (correct) |
| Single-Pairwise-MiniRafts-Line | 1712 supports, dense lines along edges — Line Contact works |

## Notes

- Forked supports generate correctly across all raft/reinforcement combos — Y-junctions
  are always clearly visible. The fork feature is the most visually distinctive.
- Line Contact is the only contact-style that produces a visible, dramatic change
  (10x support count).
- SF is stable at 0.59 across all combos except Line Contact (0.86 — higher because
  dense supports distribute load better).
- Zero floaters across all 17 combos.
