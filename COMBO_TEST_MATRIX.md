# Combinatorial Test Matrix — Support Generation

All tests run against a synthetic complex model with:
- Main body with overhang shelf (needs supports)
- Floating island at z=35..38 (disconnected)
- Thin pillar connecting to build plate
- Multiple face orientations exercising normal/angle detection

## Matrix 1: Single-Axis Sweep

| Axis | Value | C1 | C2 | C3 | C4 | C5 | C6 | Supports | Braces | Floaters |
|------|-------|----|----|----|----|----|----|----------|--------|----------|
| reinf | None | PASS | PASS | PASS | PASS | PASS | PASS | >0 | 0 | 0 |
| reinf | Pairwise | PASS | PASS | PASS | PASS | PASS | PASS | >0 | >0 | 0 |
| reinf | Triangular | PASS | PASS | PASS | PASS | PASS | PASS | >0 | >0 | 0 |
| reinf | Global | PASS | PASS | PASS | PASS | PASS | PASS | >0 | >0 | 0 |
| raft | None | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| raft | MiniRafts | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| raft | CrossGrid | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| raft | Hex | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| density | 0.3 (light) | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| density | 0.5 (medium) | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| density | 0.8 (heavy) | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| tree | false | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| tree | true | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| fillets | false | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |
| fillets | true | PASS | PASS | PASS | PASS | PASS | PASS | >0 | - | 0 |

**15/15 PASS**

## Matrix 2: Floater-Prone Full Cross Product (48 combos)

supportType {single, forked, tree} × reinforcement {None, Pairwise, Triangular, Global} × raft {None, MiniRafts, CrossGrid, Hex}

| # | Type | Reinforcement | Raft | C1 | C2 | C3 | C5 | Result |
|---|------|--------------|------|----|----|----|----|--------|
| 1 | single | None | None | PASS | PASS | PASS | PASS | **PASS** |
| 2 | single | None | MiniRafts | PASS | PASS | PASS | PASS | **PASS** |
| 3 | single | None | CrossGrid | PASS | PASS | PASS | PASS | **PASS** |
| 4 | single | None | Hex | PASS | PASS | PASS | PASS | **PASS** |
| 5 | single | Pairwise | None | PASS | PASS | PASS | PASS | **PASS** |
| 6 | single | Pairwise | MiniRafts | PASS | PASS | PASS | PASS | **PASS** |
| 7 | single | Pairwise | CrossGrid | PASS | PASS | PASS | PASS | **PASS** |
| 8 | single | Pairwise | Hex | PASS | PASS | PASS | PASS | **PASS** |
| 9 | single | Triangular | None | PASS | PASS | PASS | PASS | **PASS** |
| 10 | single | Triangular | MiniRafts | PASS | PASS | PASS | PASS | **PASS** |
| 11 | single | Triangular | CrossGrid | PASS | PASS | PASS | PASS | **PASS** |
| 12 | single | Triangular | Hex | PASS | PASS | PASS | PASS | **PASS** |
| 13 | single | Global | None | PASS | PASS | PASS | PASS | **PASS** |
| 14 | single | Global | MiniRafts | PASS | PASS | PASS | PASS | **PASS** |
| 15 | single | Global | CrossGrid | PASS | PASS | PASS | PASS | **PASS** |
| 16 | single | Global | Hex | PASS | PASS | PASS | PASS | **PASS** |
| 17-32 | forked | (all) | (all) | PASS | PASS | PASS | PASS | **PASS** |
| 33-48 | tree | (all) | (all) | PASS | PASS | PASS | PASS | **PASS** |

**48/48 PASS** — zero floaters across all combinations.

## Matrix 5: Detection

| Test | Result |
|------|--------|
| Island detection forces support on floating island | **PASS** |
| Normal recomputation: no change on correct mesh | **PASS** |

**2/2 PASS**

## Summary

| Matrix | Combos | Pass | Fail |
|--------|--------|------|------|
| Matrix 1 (single-axis sweep) | 15 | 15 | 0 |
| Matrix 2 (floater-prone cross product) | 48 | 48 | 0 |
| Matrix 5 (detection) | 2 | 2 | 0 |
| **Total** | **65** | **65** | **0** |

### Checks Legend
- C1: Generated without error
- C2: validSupports > 0
- C3: No floaters (every support route reaches plate or valid anchor)
- C4: Preview == Print (feature types in mesh match slice elements)
- C5: Braces in both mesh and slices when reinforcement enabled
- C6: Raft in slices when raft mode != none

### Bugs Found & Fixed
None — all 65 combos pass on first run after Workstream A-D fixes.

### SINAa.stl Note
The 695K-triangle SINAa.stl model requires 60+ seconds per combo due to full BVH + island
detection + overhang analysis. Matrix tests use a synthetic complex model for speed.
The synthetic model exercises the same code paths: floating island, overhangs, thin walls,
multiple support types, all reinforcement modes, all raft modes.
