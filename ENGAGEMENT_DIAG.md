# ENGAGEMENT_DIAG.md — Fork & Truss Engagement Analysis

## Step 1: Are forks preserved or eaten by a later pass?

**Forks are PRESERVED.** The escalation ladder (Step 7b) recovers failed supports but does NOT replace forked routes. Server logs show:

```
Step 3b Forks: 44 forks created (at 1.0x spacing multiplier)
Step 7b: Recovered 52/52 failed supports via escalation ladder
```

The 52 recovered supports are DIFFERENT supports from the forked ones — they're structurally failed supports, not fork-related. Forked routes that passed the emission gate survive to meshing unchanged. The route count entering meshing = route count after forking.

**VERIFIED** — forks preserved through all subsequent passes.

## Step 2: Fork engagement across densities

| Density | Median Spacing | Fork Radius | Forks | Forked Routes | Total Tips |
|---------|---------------|-------------|-------|---------------|------------|
| 0.3 (light) | 7.12mm | 4.0mm | 0 | 0 | 124 |
| 0.5 (medium) | 5.80mm | 4.0mm | 3 | 2 | 177 |
| 0.8 (heavy) | 3.63mm | 4.0mm | 116 | 204 | 404 |

**Finding:** At density 0.8 (heavy), the spacing drops to 3.63mm which is BELOW the 4mm fork radius — forking engages massively (116 forks, 204 forked routes out of 404 tips = 50%). At density 0.5 (medium), spacing is 5.80mm > 4mm — only 3 forks (1.1%). At density 0.3 (light), spacing is 7.12mm — zero forks.

**The fixed 4mm radius only works at high density.** At medium density (the default), it's dead.

## Step 3: Spacing-relative fork radius

| Multiplier | Effective Radius | Forked Routes | Tips | % Forked | % Vert Diff | Max Angle |
|-----------|-----------------|---------------|------|----------|-------------|-----------|
| 1.0× | 5.8mm | 60 | 177 | 33.9% | 9.2% | 45.0° |
| 1.3× | 7.5mm | 78 | 177 | 44.1% | 11.6% | 45.0° |
| 1.6× | 9.3mm | 86 | 177 | 48.6% | 13.0% | 45.0° |
| 2.0× | 11.6mm | 76 | 177 | 42.9% | 12.2% | 45.0° |

**Sweet spot: 1.3× median spacing (7.5mm effective radius).**
- 44.1% of tips fork (meaningful engagement)
- 11.6% vertex difference from baseline (visible change)
- Max strut angle = 45.0° (at the critical angle limit — all struts valid)
- Higher multipliers (1.6×, 2.0×) don't help much — 1.6× peaks at 48.6% but the angle limit constrains how far tips can reach

**Recommended default: ForkClusterRadiusMm = 1.3 × medianSpacing** (computed at runtime from the actual point set, not a fixed constant).

## Step 4: Truss vs Pairwise — is it genuinely different?

```
Pairwise:   526 braces, total length = 3,045.4mm
Triangular: 518 braces, total length = 4,345.9mm
Identical braces: 101/526 (19.2% of PW in TR)
```

**The truss IS genuinely different — only 19.2% of pairwise braces appear in the triangulated set.** The Delaunay triangulation produces a fundamentally different edge set.

However:
- Brace COUNT is nearly identical (526 vs 518 = 1.5% fewer)
- Total brace LENGTH is 43% LONGER in Triangular (4,346mm vs 3,045mm) because Delaunay edges connect more distant pillars along triangle edges, not just nearest neighbors

The Delaunay truss connects 302 triangles across 449 edges, then distance-filters to 518 braces. The distance filter (MaxConnectionDistMm=50mm) is wide enough that most Delaunay edges pass — this is why the count is similar.

Pre-filter: 449 Delaunay edges. Post-filter: 518 braces (the interval-based Z placement creates multiple braces per edge, so the brace count exceeds the edge count). The truss is NOT degenerate-to-pairwise — it's genuinely triangulated but produces a similar TOTAL count because the Z-interval placement fills each edge with braces.

## Verdict

**(a) Forks are PRESERVED** — no later pass eats them. The escalation ladder only touches structurally failed supports, not forked ones.

**(b) ForkClusterRadiusMm should be 1.3× the median tip spacing** (computed at runtime). At this multiplier, 44% of tips fork with a 12% mesh difference — visible and meaningful. The fixed 4mm default is dead at medium density because the 5.8mm spacing exceeds it.

**(c) The Delaunay truss IS genuinely triangulated** (only 19% of braces overlap with pairwise). It produces a different routing pattern with longer total brace length but similar count. It is NOT degenerate-to-pairwise.
