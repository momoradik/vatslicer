# Fast Engine Status — 38% speedup achieved

## Timing (SINAa.stl, 695K tris, UseFastSupportEngine=true)
| Rotation | Baseline | Fast | Speedup |
|----------|----------|------|---------|
| rot0     | 81.3s    | 50.4s| 38%    |
| rotX45   | 28.4s    | 20.8s| 27%    |
| rotY90   | 24.4s    | 17.1s| 30%    |
| rotX30Z60| 94.4s    | 62.3s| 34%    |

## Remaining bottleneck to 3s
- Point generation: 15s (per-triangle, needs bitwise layer ops — Task 2 full)
- Mesh gen + fillet: 12s (sequential fillet)
- BVH build: 5s (each rotation = different mesh hash)
- Routing fallback + interconnects: 13s
- Other: 5s

## Old engine intact: UseFastSupportEngine=false (A/B verified by fingerprint)
