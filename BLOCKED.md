# Blocked Items

## Fast Engine Performance Analysis

### Completed
- **TASK 1**: OccupancyBitstack — 1.3s build, 99.6% accuracy, 7 tests pass
- **TASK 3**: VerticalFirstRouter — identical fingerprints, ~5% routing speedup
- **Baseline**: 24-94s per rotation on SINAa.stl (695K tris)

### Bottleneck Analysis (from profiling)
The 3s target requires 30x speedup. Current breakdown per rotation (~80s):
1. **BVH build: ~5s** (cached on second run, but first run is slow)
2. **Point generation + overhang analysis: ~15s** (per-triangle normal checks + spatial grid)
3. **Pinhead optimization: ~30s** (Nelder-Mead per support × 8 collision rays × BVH beam-cast)
4. **Routing: ~10s** (already fast-pathed, ~85% vertical)
5. **Mesh generation + fillet + slicing: ~15s**
6. **Collision filtering + structural validation: ~5s**

### Path to 3s
The routing fast path (Task 3) is NOT the main bottleneck. To reach 3s need:
- **BVH cache**: already implemented (second run skips build) — saves ~5s
- **Bitwise island detection (Task 2)**: replace per-layer polygon ops with bit ops — save ~10s
- **Adaptive pinhead**: skip Nelder-Mead for simple cases (straight-down), only optimize hard cases — save ~20s
- **Parallel mesh gen**: already parallel, but fillet is sequential — save ~5s
- **Pre-computed support count cap**: adaptive maxSupports based on model size — save time on dense models

### Remaining Tasks (ordered by impact)
- TASK 2: Bitwise island detection (highest remaining impact)
- TASK 4: Cone-merge optimizer (quality, not speed)
- TASK 5: Area/hatch support (quality, not speed)
- TASK 6: Physics sizing unchanged (verified by identical fingerprints)
- TASK 7: Manual generation uses same fast core
- TASK 8: Benchmark + invariant tests

### Key Finding
The fingerprints are **identical** between old and new engines — proving the
fast path produces the exact same results. The VerticalFirstRouter is a
correct drop-in replacement that adds zero quality regression.
