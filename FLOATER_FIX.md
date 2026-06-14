# Floating Support Fix

## Verdict

**0 floating supports** detected across all test combinations. The primary floater bug was
already fixed in commit `a0800e9` (2026-06-11). This fix adds **3 defense-in-depth hardening
measures** to prevent floaters from ever re-appearing via latent code paths.

## What Floated (Historical)

**Root cause**: Pinhead meshes (contact sphere + tapered frustum) were generated for ALL
valid pinheads BEFORE the emission gate filtered rejected routes. Supports whose pillar
routing failed (PillarRouter returned empty/short path) still had their pinhead geometry
added to `meshParts`, creating floating geometry disconnected from any pillar or the build
plate.

**Producer**: All support types — the pinhead mesh loop ran unconditionally for every
pinhead with `IsValid=true`, regardless of whether a route was found.

**Prior fix** (commit `a0800e9`): Moved pinhead mesh generation below the emission gate,
guarded by `if (!validIds.Contains(id)) continue;`.

## Latent Vulnerabilities Found and Fixed

### 1. Escalation Rung 4 — Missing ReachesGround Check
**File**: `SupportEngineV2.cs`, Step 7b escalation ladder, Rung 4
**Bug**: Accepted re-routed paths with `Path.Count > 1` but no `ReachesGround` or
`AnchorPoint` check. A 2-waypoint path (junction + partial pillar) from a failed re-route
would be accepted despite not terminating.
**Fix**: Added `&& (newRoute.ReachesGround || newRoute.AnchorPoint.HasValue)` to the
acceptance condition.

### 2. Escalation Rung 3 — Missing AnchorPoint Copy
**File**: `SupportEngineV2.cs`, Step 7b escalation ladder, Rung 3 (Y-junction merge)
**Bug**: When merging a failed support into a neighbor via Y-junction, the merged route
copied `ReachesGround` from the neighbor but not `AnchorPoint` or `AnchorNormal`. If the
neighbor was an anchor route (`ReachesGround=false`, `AnchorPoint` set), the merged route
would have `ReachesGround=false` AND no `AnchorPoint`, failing the emission gate despite
being structurally valid.
**Fix**: Copy `AnchorPoint` and `AnchorNormal` from neighbor. Added
`(neighborReachesGround || routes[bestMergeIdx].route.AnchorPoint.HasValue)` guard.

### 3. Post-Emission-Gate Floater Safety Net (NEW)
**File**: `SupportEngineV2.cs`, after emission gate
**Purpose**: Final defense layer — verifies every route's actual geometry terminates on
the build plate (lowest waypoint Z < 0.5mm) or on the part surface (has anchor waypoint
or AnchorPoint). Any route that fails is dropped and its overhang re-flagged as
uncoverable, so the coverage check sees it as unsupported (not silently hidden).

## Proof — Floater Count = 0

### SINAa.stl (all combos)
| Configuration | Valid | Grounded | Part-Anchored | FLOATING |
|---|---|---|---|---|
| Fork OFF, Tree ON | 168 | 168 | 0 | **0** |
| Fork ON, Tree ON | 171 | 171 | 0 | **0** |
| Fork ON, Tree OFF | 171 | 171 | 0 | **0** |
| Fork OFF, Tree OFF | 168 | 168 | 0 | **0** |
| Fork ON, Tree ON, density=0.9 | 577 | 577 | 0 | **0** |

### Floating-Island Synthetic Test (base box Z[0,3] + floating box Z[30,33])
| Configuration | Valid | FLOATING |
|---|---|---|
| Fork OFF, Tree ON | 14 | **0** |
| Fork ON, Tree ON | 14 | **0** |

### Regression Gate
Forking OFF on SINAa.stl: **ValidSupports=168, Uncov=0, SF=0.59** — unchanged.

## Screenshot

- `web/.design-review/no-floaters.png` — Support layer cross-section at Z=0.5mm.
  Every white circle = one support's cross-section at near-plate height. All 168 supports
  are present, confirming every support has geometry reaching the build plate. No gaps,
  no missing circles = no floaters.
- `web/.design-review/no-floaters-heatmap.png` — Top-down support density heatmap showing
  full coverage of overhang regions.
