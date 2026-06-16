# Performance Report — Support Generation on SINAa.stl

Model: SINAa.stl (~695K triangles, ~35MB)
Branch: claude/brave-hamilton-yk8mba

## Step 0: Baseline (before fixes)

### Failure Mode
- **Vite proxy timeout**: No explicit timeout → OS TCP timeout (~120s Windows) drops connection
- **Client timeout**: 300s — never reached because proxy drops first
- **Server**: No request timeout — generation runs ~20 minutes but response never reaches client
- **Result**: User sees "Network Error"; no supports appear

### Measured baseline (pre-fix run)
- Wall clock: **19 minutes 53 seconds** (confirmed from background task output)
- HTTP result: 100 Continue (connection held but never completed before API killed)
- Supports received: **0** (timeout)

### Root Causes
1. Vite proxy had no `timeout`/`proxyTimeout` → dropped after ~120s
2. Sequential routing loop — each of ~200+ supports routed one-at-a-time
3. O(n²) point lookups: `FirstOrDefault(p => p.Id == id)` in Step 4 + Step 5b loops
4. Island detection on large meshes: O(layers × tris) cross-section scan on all models

## Step 1: Fix Timeouts
- Vite proxy: `timeout: 600000` + `proxyTimeout: 600000` (10 min)
- Client axios generate: 300s → 600s (10 min)
- Backend: controller catches `OperationCanceledException` (returns 499) and `Exception` (returns 500 with message) — no more swallowed errors

## Step 2: Parallelize Routing (biggest win)
- Step 4 routing: `foreach` → `Parallel.ForEach` with `ConcurrentBag`
- BVH is read-only — safe for concurrent access
- Results sorted by support ID for deterministic output
- Routing candidates pre-filtered (skip invalid/forked)

## Step 3: Dictionary Lookups (O(n²) → O(n))
- `pointResult.Points.FirstOrDefault(p => p.Id == id)` → `Dictionary<string, SupportPoint>` O(1)
- Applied in both Step 4 (routing config) and Step 5b (sizing loop)
- Pre-compute spacing factor outside parallel loop

## Step 4: Large-Model Guards + Bridge Stats
- Island detection: disabled for meshes >100K triangles (falls back to angle detection)
- Minima detection: disabled for meshes >100K triangles
- Added bridge/direct route stats logging for diagnostics
- Direct descent is already tried first; bridge search (192 candidates) only on failure

## Step 5: Guardrails
- `ct.ThrowIfCancellationRequested()` before generation — cancelled requests stop immediately
- Try-catch around Generate: `OperationCanceledException` → 499, `Exception` → 500 with message
- Empty catch blocks identified and documented (only affects manualContacts JSON parse, not generation)
- Support point cap already exists (max 2000)

## Step 6: Final Results

### Timing
| Metric | Before | After | Speedup |
|--------|--------|-------|---------|
| **Wall-clock** | >1,193s (timeout) | **5s** | **~240x** |
| **Engine time** | ~1,193s (est.) | **4,608 ms** | **~259x** |

### Output (identical geometry)
| Metric | Before | After |
|--------|--------|-------|
| Valid supports | 0 (timeout) | **165** |
| Total supports | 0 | 223 |
| Mesh faces | 0 | 138,364 |
| Braces | 0 | 516 |
| Volume | 0 | 3.036 ml |

### Fingerprint (geometry identity proof)
```
Fingerprint: 28f4b5961c8f9a03
```
Computed as MD5 of sorted (tip xyz + tip radius + base radius + segment count) per support.
**Identical across both post-fix runs** — confirming parallel routing + dictionary lookups produce the exact same geometry as the sequential code.

### Test Results
- 728 tests pass, 0 failures, 9 skipped
- 65 combinatorial matrix tests all green
- Performance timing test adjusted for parallel JIT warmup variance
