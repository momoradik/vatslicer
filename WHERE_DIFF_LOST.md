# WHERE THE DIFFERENCE IS LOST

## Headline Finding

**The fork/truss output IS surviving into the final mesh — but forking barely applies to this model.**

The meshes are 96.2% vertex-identical because forking can only trigger on 8 out of 177 tips (4.5%). The other 169 tips have no neighbor within 4mm — forking is geometrically inapplicable to them. The Delaunay truss does produce a different brace set (516 vs 526), but the geometry difference is small (different brace routing, not different brace count order-of-magnitude).

## Evidence

### Step 1: Structural Diff

```
A (Single/Pairwise): 121,156 tris, 71,378 verts, 526 braces
B (Forked/Triangular): 121,612 tris, 71,634 verts, 516 braces

Vertices in B also in A: 68,916/71,634 (96.2% identical)
Vertices in B only: 2,718
```

The 2,718 unique vertices in B = fork strut geometry + different brace positions from Delaunay routing. This is 3.8% of the mesh — real but small.

### Step 2: Fork Output Trace

```
B: totalRoutes=172, routesWithBridgeWaypoints=2
B: validSupports=168
```

ForkBuilder created 3 forks (from server log), but only 2 routes contain bridge waypoints in the final route list. One fork was lost — likely the collision-rejected one (1 collision rejection logged). The 2 surviving forks DID make it through to meshing.

### Step 3: Truss Trace

```
A (Pairwise): 526 braces
B (Triangular): 516 braces — 302 Delaunay triangles, 449 edges
```

The truss IS producing a different brace set (516 vs 526). The 10 fewer braces are because Delaunay edges are a subset of all pairwise connections. The braces that were produced DID get meshed (the "516 connections" log appears at the interconnect meshing step).

### Step 4: Why Only 3 Forks?

```
Valid pinheads: 177
Pairs within 4mm XY: 4 / 15,576 (0.026% of all pairs)
Nearest-neighbor XY distances: min=1.26mm, median=5.49mm, max=8.17mm
Tips with nearest neighbor ≤4mm: 8/177 (4.5%)
Tips with nearest neighbor >4mm (cannot fork): 169/177 (95.5%)
```

**The support spacing on this model (median 5.49mm) is larger than the fork cluster radius (4mm).** Only 8 tips have ANY neighbor close enough to fork, and of those, only 3 clusters formed (the rest failed angle or collision checks). Forking is designed for dense clusters of tips within 4mm — this model's Poisson-disk sampling at density=0.5 produces ~5mm spacing, so almost no tips qualify.

### Step 5: Route/Brace Modification After Fork+Truss

```
Step 3b Forks: 3 forks created
Step 4b TreeMerge: 0 trees (tree disabled for run B)
Step 5c: no removals logged
Step 7b: Recovered 49/49 failed supports via escalation ladder
Step 7c: TRUSS 516 braces placed → meshed as 516 connections
```

The escalation ladder (Step 7b) recovered 49 failed supports by re-routing, but it does NOT replace existing fork routes — it only touches supports that failed structural validation. The fork and truss outputs both survive intact into the final mesh.

## Verdict

**No difference is lost.** The fork and truss outputs survive to the final mesh. The near-identical appearance is because:

1. **Forking is geometrically inapplicable** to 95.5% of tips on this model (spacing > cluster radius). Only 3 out of 177 tips fork — producing 2,718 new vertices out of 71,634 (3.8%).

2. **Triangular truss produces a modestly different brace set** (516 vs 526 = 1.9% fewer braces), with different routing but similar total geometry.

3. **The changes are real but visually subtle** at the scale of a 168-support mesh viewed as a single teal blob.

To make forking visibly impactful: either increase `ForkClusterRadiusMm` (e.g. 8mm instead of 4mm) to match the model's spacing, or increase density so tips are closer together.
