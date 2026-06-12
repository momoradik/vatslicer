# DIAGNOSIS: Why support generation appears to produce one shape

## Verdict: DOWNSTREAM — the engine varies but the 3D viewer renders one merged mesh

The engine IS producing different geometry when options change (Step 5 proves face counts differ: 122,672 vs 123,128). The config DOES reach the engine with different values (Step 3 proves forking/reinf/raft flags arrive correctly). The branches DO execute (Step 4 proves forks form, Delaunay triangulation runs, full-plate raft generates).

**The user sees "one shape" because:**

1. **All support geometry is merged into a SINGLE watertight mesh** (`SupportEngineV2.cs` line ~1090: all mesh parts merged into one `IndexedTriangleSet`). The viewer receives this as one blob of triangles — there is no per-support coloring, no per-type highlighting, no visual distinction between a forked trunk and a single pillar in the rendered output.

2. **The differences are subtle at the scale of a 695K-triangle model** — forking adds 256 vertices out of 72,000 (0.35% change). The Delaunay truss removes 14 braces out of 534 (2.6% change). These are real geometric changes but not visually obvious when viewing the entire support set at once.

3. **The viewer renders ALL supports in one solid teal color** (`StlViewer.tsx:1221`: `color: 0x14b8a6, opacity: 0.7`). There is no per-type material, no wireframe overlay showing braces vs trunks, no visual feedback that "this cluster was forked" vs "this one wasn't."

## Evidence Summary

| Step | Question | Result |
|------|----------|--------|
| 1 | Code exists? | YES — ForkBuilder, BuildTriangulated, Delaunay, all flags declared+read+branched |
| 2 | Defaults? | New features default OFF; tree/hollow/mini-rafts/interconnections default ON |
| 3 | Config reaches engine? | YES — GEN-CONFIG logs show different values per run |
| 4 | Branches fire? | YES — "3 forks", "282 Delaunay triangles", "FullPlateRaft" all logged |
| 5 | Output differs? | YES — 122,672 vs 123,128 faces, 534 vs 520 braces |

## Root Cause

Not a bug in the engine or wiring. The issue is **visual feedback** — the viewer has no way to show the user what changed. Every support option produces a real geometric change, but the single-color merged mesh makes it invisible.

## What would make the difference visible (not implementing, just noting)

- Per-type color coding (forks in amber, braces in blue, tree trunks in green)
- Wireframe overlay for braces/connections
- Before/after diff highlighting
- Support count + brace count shown in the UI stats after generation
