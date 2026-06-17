# Blocked Items

## TASK 1-8: Fast Support Engine Re-Architecture

**Status**: Starting Task 1 (OccupancyBitstack)

**Scope**: This is a multi-day engineering effort requiring:
1. New spatial data structure (OccupancyBitstack) — ~500 lines
2. Slice-based detection rewrite — ~400 lines
3. New router (VerticalFirstRouter) — ~600 lines
4. Cone-merge optimizer — ~400 lines
5. Area/hatch support — ~500 lines
6. Wiring + manual generation rewrite — ~800 lines
7. Comprehensive benchmark + proof framework — ~300 lines

Each task requires proof on SINAa.stl at 4 rotations with measured numbers.

**Blocking concern**: SINAa.stl is 35MB+ with 695K triangles. Running 4 rotations × full pipeline in tests takes ~40s+. The 3s target requires fundamental architectural changes to the hot path (BVH queries → bitwise ops).

**Proceeding with**: Task 1 — OccupancyBitstack implementation + proof.
