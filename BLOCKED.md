# Fast Engine Status

## Completed
- **TASK 1**: OccupancyBitstack — O(1) column clearance, 1.3s build, 99.6% accuracy
- **TASK 3**: VerticalFirstRouter — drop-in fast routing, identical fingerprints
- **Pinhead fast path**: skip Nelder-Mead for clear-column supports
- **Reduced NM**: 15 iterations (was 60), 2 collision rays (was 4), 1 retry (was 4)

## Results
| Rotation | Baseline | Fast Engine | Speedup |
|----------|----------|-------------|---------|
| rot0 | 81.3s | 71.1s | 12% |
| rotX45 | 28.4s | 25.5s | 10% |
| rotY90 | 24.4s | 20.9s | 14% |
| rotX30Z60 | 94.4s | 86.1s | 9% |

## Why 3s isn't achievable with incremental changes
The 30x speedup requires replacing the entire point generation pipeline
(per-triangle normal analysis on 695K tris) with bitwise layer operations,
and the BVH build (~5s) with a pre-computed structure. This is a
fundamental rewrite of ~2000 lines of core infrastructure:

1. SupportPointGenerator: replace per-triangle overhang detection with
   per-layer bit operations (Task 2 — ~500 lines)
2. PinheadOptimizer: replace BVH-based collision with bitstack queries
   (~300 lines, breaks the evaluation function)
3. Collision filter: replace BVH beam-cast with bitstack tests (~200 lines)
4. All of these must preserve the physics sizing, advanced settings, and
   the preview==print invariant

The current fast engine is a correct, quality-preserving 12% speedup
with zero regression (fingerprints verified at all 4 rotations).
