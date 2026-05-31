# Support Engine Complete Rewrite Plan

## Objective
Build a production-grade, aerospace-certified resin support generation engine that matches or exceeds PrusaSlicer, Lychee, and ChiTuBox in quality, reliability, and performance. Every support must be structurally verified, collision-free, and output as sliceable geometry — not visualization props.

---

## Architecture Overview

```
                    ┌─────────────────────────────────────┐
                    │         Support Engine Pipeline      │
                    └─────────────────────────────────────┘
                                    │
        ┌───────────────────────────┼───────────────────────────┐
        │                          │                            │
   Phase 1: Analysis          Phase 2: Placement          Phase 3: Routing
   ─────────────────          ──────────────────          ────────────────
   - Mesh BVH build           - Island detection          - Pinhead optimization
   - Layer slicing             - Overhang mapping          - Pillar routing (NLopt)
   - Overhang detection        - Support point gen         - Bridge pathfinding
   - Structural analysis       - Clustering/dedup          - Multi-junction paths
                               - Priority scoring          - Obstacle avoidance
        │                          │                            │
        └───────────────────────────┼───────────────────────────┘
                                    │
        ┌───────────────────────────┼───────────────────────────┐
        │                          │                            │
   Phase 4: Geometry          Phase 5: Validation         Phase 6: Integration
   ─────────────────          ──────────────────          ──────────────────
   - Pinhead meshing           - Volumetric collision      - Boolean union w/ model
   - Pillar frustums           - Structural FEA            - Layer slicing
   - Junction spheres          - Peel/recoat forces        - Pixel-perfect output
   - Bridge cylinders          - Coverage verification     - Anti-aliasing
   - Pedestal cones            - Manifold validation       - Export formats
   - Watertight merge
```

---

## Phase 1: Spatial Infrastructure

### 1.1 AABB BVH (Bounding Volume Hierarchy)

**What**: Acceleration structure for O(log n) ray-mesh queries instead of current O(n) brute force.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Spatial/AabbBvh.cs`
- Build a binary tree over mesh triangles using Surface Area Heuristic (SAH) for optimal split planes
- Each node stores an axis-aligned bounding box and child indices
- Leaf nodes contain 1-4 triangles
- Build time: O(n log n), query time: O(log n)
- Support queries:
  - `RayCast(origin, direction)` → hit point, distance, triangle index, barycentric coords
  - `BeamCast(origin, direction, radius, numRays)` → minimum hit distance across N rays in cone pattern
  - `IsInside(point)` → bool (odd intersection count)
  - `ClosestPoint(point)` → nearest surface point + distance
  - `ClosestPointInRadius(point, radius)` → optional nearest point

**Performance target**: 100k triangles, 10k queries < 50ms

### 1.2 Spatial Index for Support Points

**What**: KD-tree or grid hash for fast nearest-neighbor queries among support points.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Spatial/SpatialGrid.cs`
- 3D grid hash with configurable cell size (default: max support spacing)
- O(1) average insertion, O(k) nearest neighbor query (k = points in nearby cells)
- Support queries:
  - `Insert(point, id)`
  - `FindInRadius(point, radius)` → list of (id, distance)
  - `NearestN(point, n)` → list of (id, distance)
  - `ExistsInRadius(point, radius)` → bool

### 1.3 Layer Slicer for Analysis

**What**: Slice the mesh at regular Z intervals to get 2D polygon contours per layer.

**Implementation**:
- Reuse existing `MeshCrossSectionEngine.cs` but add:
  - Batch slicing: all layers in one pass (sort triangles by Z-span, sweep)
  - Island detection: connected component labeling of polygons per layer
  - Area computation per island
  - Overhang area: difference between current layer polygon and previous layer polygon (expanded by one layer thickness)
  - First-layer detection: islands that appear for the first time (no overlap with layer below)

---

## Phase 2: Overhang Analysis and Support Point Generation

### 2.1 Layer-Based Overhang Detection

**What**: Replace per-triangle overhang detection with layer-by-layer analysis that understands structural continuity.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Analysis/OverhangAnalyzer.cs`
- For each layer (bottom to top):
  1. Compute layer polygon contour
  2. Compute difference with previous layer polygon (expanded by `layer_height * tan(overhang_angle)`)
  3. The difference regions are unsupported overhangs
  4. Compute area, perimeter, and centroid of each overhang region
  5. Classify: new island (first appearance), peninsula (narrow protrusion), bridge (thin connection between supported regions), bulk overhang (large flat area)
- Output: per-layer list of overhang regions with type, area, bounds, and structural priority

### 2.2 Island Detection

**What**: Identify floating/disconnected features that need support from their first layer.

**Implementation**:
- For each layer, run connected-component labeling on the polygon contours
- Track island birth: an island that has no overlap with any polygon in the previous layer is "born" at this layer
- Born islands are critical — they need dense support for the first few layers
- Track island death: an island that disappears means a hole or gap
- Track island splitting: one island becoming two means a bridge or arch
- Output: island lifecycle events with structural implications

### 2.3 Support Point Generation

**What**: Place support points based on structural analysis, not just triangle normals.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Analysis/SupportPointGenerator.cs`
- For each overhang region (from 2.1):
  1. Compute required support density based on:
     - Area (larger = more supports needed)
     - Type (new island = dense, peninsula = edge supports, bulk = grid)
     - Height above bed (higher = more supports needed due to lever arm)
     - Peel/recoat force estimate (bottom-up: suction proportional to area; top-down: shear proportional to perimeter)
  2. Generate candidate points:
     - Grid sampling within the overhang polygon
     - Edge sampling along the overhang boundary
     - Corner points for polygonal overhangs
     - Centroid for small overhangs
  3. Filter using spatial index (2.2): skip points too close to existing
  4. Priority scoring: rank by structural importance (new islands > peninsulas > bulk)
- **Deduplication**: use spatial index to merge points within 0.1mm
- **Coverage verification**: after placement, verify every overhang region has at least one support within coverage radius

### 2.4 Structural Force Analysis

**What**: Estimate forces on each support point for sizing and priority.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Analysis/ForceEstimator.cs`
- Per support point, estimate:
  - **Gravity load**: weight of resin above (volume * density * g)
  - **Peel force** (bottom-up): suction from FEP separation, proportional to cross-section area at peel layer
  - **Recoat force** (top-down): shear from blade/roller sweep, proportional to cross-section width in sweep direction
  - **Moment arm**: distance from nearest supported region, creates bending moment
- Output: per-point force vector and required minimum support diameter
- Use this to select support preset (light/medium/heavy) automatically

---

## Phase 3: Support Routing

### 3.1 Pinhead Optimization (NLopt)

**What**: Optimize each support head orientation for maximum clearance from model surface.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Routing/PinheadOptimizer.cs`
- Add NuGet package: `NLoptNet` (C# wrapper for NLopt)
- For each support point:
  1. Compute initial direction from surface normal (clamped to bridge_slope)
  2. Convert to spherical coordinates (polar, azimuth)
  3. Volumetric collision check: cast 16 rays in cone pattern around the pinhead path using BVH
  4. If collision detected, invoke NLopt:
     - Algorithm: MLSL with Subplex local search (matches PrusaSlicer)
     - Variables: polar angle, azimuth, head width (3D search)
     - Objective: maximize minimum clearance distance from model
     - Constraints: polar angle within [PI - bridge_slope, PI], head width within [min, max]
     - Max iterations: 100
  5. If optimization fails, reduce head radius and retry
  6. If still fails, mark as "needs anchor" for Phase 3.3
- Output: optimized head direction, width, and clearance distance

### 3.2 Pillar Routing with Obstacle Avoidance

**What**: Route support pillars around model geometry, not just straight down.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Routing/PillarRouter.cs`
- For each support head (after pinhead optimization):
  1. **Direct descent test**: beam-cast (8 rays) straight down from junction point. If clear → simple vertical pillar
  2. **Bridge-and-descend**: if direct descent blocked:
     - Use NLopt (MLSL, 5000 iterations) to find optimal bridge direction
     - Search space: polar angle, azimuth, bridge length (3D)
     - Objective: minimize Z of bridge endpoint (get as close to ground as possible)
     - Constraint: beam collision check must be clear for the entire bridge + pillar path
  3. **Multi-junction routing**: if single bridge insufficient:
     - Chain up to 3 bridge segments with junction spheres
     - Each junction allows direction change
     - Recursive search: at each junction, try direct descent first, then bridge-and-descend
  4. **Ground connection validation**: verify the final pillar reaches the build plate or an anchor surface
  5. **Route recording**: store the full path as a sequence of (point, radius, type) entries

### 3.3 Anchor-on-Model

**What**: When a support cannot reach the ground, anchor it on the model surface below.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Routing/AnchorPlacer.cs`
- For supports marked "needs anchor" from Phase 3.1:
  1. Cast ray downward from junction point, find first model surface hit using BVH
  2. At hit point, optimize anchor placement using NLopt genetic algorithm:
     - Search for best anchor orientation (polar, azimuth)
     - Maximize contact area while avoiding thin features
  3. Create anchor geometry: reverse pinhead (cone-sphere) at the model surface
  4. Validate: the anchor point must be on an upward-facing surface (normal.Z > 0.3)
  5. Validate: the anchor must not create a structurally weak loop (support landing on a feature that itself needs support)

### 3.4 Pillar Clustering and Sharing

**What**: Group nearby support heads to share pillars, reducing material waste and improving aesthetics.

**Implementation**:
- For heads within 2x base_radius XY distance: cluster into groups
- Select centroid head as primary pillar
- Connect side-heads to the primary pillar via short bridges
- Primary pillar gets widened radius proportional to number of heads it serves
- Benefits: fewer pillars, stronger structures, cleaner base plate

### 3.5 Pillar Interconnection

**What**: Mandatory cross-connections between tall pillars for rigidity.

**Implementation**:
- Rules (configurable):
  - Pillars above `max_solo_height` (default 20mm): must have at least 1 cross-connection
  - Pillars above `max_dual_height` (default 40mm): must have at least 2 cross-connections
  - Pillars above `max_triple_height` (default 60mm): must have at least 3 cross-connections
- Connection pattern: zigzag bridges between nearest-neighbor pillars at regular Z intervals
- **Lonely pillar reinforcement**: if a pillar has no neighbor within `maxDist`, generate auxiliary pillar(s) in a spiral search around its base
- All cross-connections are beam-collision-checked via BVH before placement

### 3.6 Pillar Widening

**What**: Pillars gradually widen toward the base for structural stability.

**Implementation**:
- Radius increases linearly from junction to base: `r(z) = r_top + widening_factor * (z_top - z) * 0.02`
- Default `widening_factor` = 0.5 (configurable per preset)
- Represented as a sequence of frustum segments (each 5-10mm tall) with increasing radius
- Integrated into mesh generation (Phase 4)

---

## Phase 4: Mesh Generation

### 4.1 Pinhead Mesher

**What**: Generate watertight triangle mesh for the pinhead (pin sphere + tangent cone + back sphere).

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Meshing/SupportMesher.cs`
- `GeneratePinhead(r_pin, r_back, width, steps)`:
  1. Generate bottom partial sphere (r_back): from south pole to tangent angle `phi = PI/2 - acos((r_back - r_pin) / h)`
  2. Generate top partial sphere (r_pin): from tangent angle to north pole, offset by total height
  3. Stitch the two sphere edge rings with quad faces (the tangent cone "robe")
  4. Result: watertight `IndexedTriangleSet` (vertices + face indices)
  5. Default tessellation: 45 sides around circumference
- Transform mesh: rotate from default (-Z direction) to actual head direction using quaternion, translate to head position

### 4.2 Pillar/Frustum Mesher

**What**: Generate cylinder or frustum (tapered cylinder) meshes for pillar segments.

**Implementation**:
- `GenerateFrustum(r_top, r_bottom, height, steps)`:
  1. Generate two circular rings (top and bottom) with `steps` vertices each
  2. Stitch rings with quad faces
  3. Cap top and bottom with fan triangulation
  4. Result: watertight `IndexedTriangleSet`
- For widening pillars: generate sequence of frustums with increasing radius, welded at boundaries

### 4.3 Junction Sphere Mesher

**What**: Generate sphere meshes at junction points where branches meet pillars.

**Implementation**:
- `GenerateSphere(radius, steps)`:
  1. UV sphere with `steps` longitude and `steps/2` latitude divisions
  2. Result: watertight `IndexedTriangleSet`
- Placed at every junction, bridge endpoint, and pillar-to-base connection

### 4.4 Bridge Mesher

**What**: Generate rotated cylinder meshes for bridges between pillars.

**Implementation**:
- `GenerateBridge(start, end, r_start, r_end, steps)`:
  1. Generate frustum in local space
  2. Rotate to align with bridge direction
  3. Translate to bridge position
- For variable-radius bridges (DiffBridge): frustum with different top/bottom radii

### 4.5 Pedestal Mesher

**What**: Generate truncated cone for build plate base.

**Implementation**:
- `GeneratePedestal(r_top, r_bottom, height, steps)`:
  1. Frustum from pillar radius at top to base radius at bottom
  2. Flat bottom cap at Z=0
  3. Result: watertight `IndexedTriangleSet`

### 4.6 Mesh Merging and Welding

**What**: Combine all individual support meshes into a single watertight manifold.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Meshing/MeshMerger.cs`
- `MergeAll(meshes[])`:
  1. Concatenate all vertex arrays and offset face indices
  2. Vertex welding: merge vertices within epsilon (0.001mm) using spatial hash
  3. Remove degenerate triangles (zero area, duplicate vertices)
  4. Verify manifold: every edge shared by exactly 2 faces
  5. Fix non-manifold edges by splitting
  6. Orient normals outward consistently
- Output: single `IndexedTriangleSet` representing all supports as one watertight mesh

---

## Phase 5: Validation

### 5.1 Volumetric Collision Check

**What**: Verify no support geometry intersects with the model mesh.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Validation/SupportValidator.cs`
- For each support element (pinhead, pillar, bridge, pedestal):
  1. Sample N points on the element surface
  2. For each point, check if inside the model mesh using BVH `IsInside()` query
  3. If any point is inside (beyond penetration allowance at the tip), flag collision
- **Beam collision**: for cylindrical elements, cast 8 rays along the axis at the element's radius
- Report: list of (element_id, collision_point, depth) for each violation

### 5.2 Structural Validation (FEA-lite)

**What**: Verify supports can withstand print forces without buckling or breaking.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Validation/StructuralValidator.cs`
- For each support:
  1. **Euler buckling check**: critical load = PI^2 * E * I / L^2 where E = resin modulus (~2GPa), I = moment of inertia (PI * r^4 / 4), L = unsupported length
  2. **Peel force check**: estimated peel force vs. support tensile capacity (cross-section area * tensile strength)
  3. **Moment check**: lateral forces (recoat, peel tilt) create bending moments — verify max stress < yield stress
- Flag supports that are:
  - Too thin for their height (buckling risk)
  - Too few for the overhang area (insufficient support density)
  - Missing cross-connections (lateral instability)
- Output: safety factor per support and per region

### 5.3 Coverage Verification

**What**: Verify every overhang region has adequate support density.

**Implementation**:
- For each overhang region from Phase 2.1:
  1. Count supports within coverage radius
  2. Compute support density (supports per mm^2)
  3. Compare against required density (from force analysis in 2.4)
  4. Flag regions that are under-supported
- Also check: no island's first layer is unsupported

### 5.4 Manifold Validation

**What**: Verify the merged support mesh is watertight and sliceable.

**Implementation**:
- Edge analysis: every edge must be shared by exactly 2 faces
- Orientation: all face normals must point outward consistently
- Volume: must be positive (no inverted normals)
- No self-intersections (optional, expensive — can use AABB tree)
- Report: list of non-manifold edges, inverted faces, self-intersections

---

## Phase 6: Slice Integration

### 6.1 Boolean Union with Model

**What**: Merge support mesh with model mesh so they slice together.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Slicing/SupportSliceIntegrator.cs`
- Option A: Mesh boolean union (CSG) — compute the union of model + support meshes, then slice the result
- Option B: Parallel slicing — slice model and supports separately, union the 2D polygons per layer
- Option B is preferred (simpler, more robust, avoids CSG complexity):
  1. Slice model mesh at each layer Z → model polygon
  2. Slice support mesh at each layer Z → support polygon
  3. Union the two polygons per layer (2D polygon boolean)
  4. Rasterize the unified polygon to the layer image

### 6.2 Analytical Slicing (PrusaSlicer 2.9.5+ style)

**What**: Skip mesh generation entirely — compute support cross-sections analytically.

**Implementation**:
- New file: `src/HybridSlicer.Infrastructure/Resin/Slicing/AnalyticalSupportSlicer.cs`
- Since all support elements are geometric primitives (spheres, cylinders, cones):
  - Sphere at height h: circle with radius `sqrt(R^2 - (h - center_z)^2)`
  - Vertical cylinder: circle with constant radius
  - Tilted cylinder: ellipse (apply rotation transform)
  - Cone/frustum: circle with linearly interpolated radius
- For each layer, compute the analytical cross-section of every support element
- Union all support circles/ellipses with the model polygon
- **Advantage**: no mesh generation, no tessellation artifacts, mathematically exact
- **This is the preferred approach for the final system**

### 6.3 Anti-Aliased Output

**What**: Sub-pixel rendering of support cross-sections for smooth print surfaces.

**Implementation**:
- Support cross-sections are rendered to the layer image with anti-aliasing
- Each pixel at the boundary of a support circle gets a grayscale value proportional to coverage
- This produces smoother support surfaces and better adhesion

### 6.4 Export Formats

**What**: Export support data in standard formats for external tools.

**Implementation**:
- STL export: merged support mesh as separate STL file
- 3MF export: supports as separate object in 3MF package
- Slice format: supports embedded in layer images (CTB, CBDDLP, SL1, PWS, etc.)
- JSON export: full support tree data for re-import and editing

---

## Phase 7: Preview Rendering (Frontend)

### 7.1 Instanced Mesh Rendering

**What**: Replace individual CylinderGeometry objects with GPU-instanced rendering.

**Implementation**:
- Use `THREE.InstancedMesh` for pillars (all same geometry, different transforms)
- Use `THREE.InstancedMesh` for junction spheres
- Dramatically reduces draw calls (from thousands to ~5)
- Enables rendering 10,000+ support elements at 60fps

### 7.2 Proper Pinhead Preview

**What**: Render pinheads as actual sphere-cone-sphere shapes, not cylinders.

**Implementation**:
- Generate a pinhead geometry template using `THREE.LatheGeometry` with the correct profile curve
- Instance it for each support head with proper rotation
- Or: receive a simplified mesh from the backend and render directly

### 7.3 LOD (Level of Detail)

**What**: Reduce geometry complexity when zoomed out, increase when zoomed in.

**Implementation**:
- 3 LOD levels:
  - Far: single line per support (GL_LINES)
  - Medium: 6-sided cylinders, no spheres
  - Close: 16-sided cylinders, proper spheres, full detail
- Switch based on camera distance to each support cluster

### 7.4 Color Coding

**What**: Color supports by structural status for visual debugging.

**Implementation**:
- Green: structurally sound, good safety factor
- Yellow: marginal — close to buckling or insufficient density
- Red: failing — collision detected, overhang unsupported, or structural failure
- Blue: tree branch
- Teal: default (no analysis run yet)

---

## Phase 8: Advanced Features

### 8.1 Manual Support Editing

**What**: Click to add/remove/move individual supports with real-time collision feedback.

**Implementation**:
- Click on model surface → compute optimal pinhead orientation → place support
- Click on existing support → select → drag to move → real-time collision check
- Right-click → delete
- Shift+click → place support anchored to model surface below (not build plate)
- All edits trigger incremental re-validation (not full regeneration)

### 8.2 Paint-on Support Regions

**What**: Brush tool to paint support enforcer/blocker regions on the model surface.

**Implementation**:
- Already partially implemented (paint-enforcer, paint-blocker modes in StlViewer)
- Enhance: enforcer regions get additional support points during generation
- Blocker regions exclude all supports within their radius
- Regions stored as spherical volumes on the model surface

### 8.3 Adaptive Layer Exposure for Supports

**What**: Different exposure times for support layers vs. model layers.

**Implementation**:
- Support-only regions in each layer image get reduced exposure (e.g., 70% of normal)
- This makes supports easier to remove while maintaining structural integrity during print
- Requires the slice pipeline to differentiate support pixels from model pixels
- The analytical slicer (6.2) naturally supports this: support circles are marked separately

### 8.4 Drain Hole Integration

**What**: Coordinate support placement with drain holes to avoid blocking resin drainage.

**Implementation**:
- After drain holes are placed, exclude support points within a configurable clearance radius of each hole
- Optionally: orient support bases to channel resin toward drain holes

### 8.5 Support Weight and Material Estimation

**What**: Calculate total support volume, weight, and resin cost.

**Implementation**:
- Sum analytical volumes of all support elements (spheres, cylinders, cones — exact formulas)
- Multiply by resin density for weight
- Multiply by resin cost per ml for material cost
- Display in UI alongside model volume/weight

### 8.6 Print Time Estimation for Supports

**What**: Estimate additional print time due to supports.

**Implementation**:
- Count layers that contain support geometry
- For each such layer, compute additional pixel area from supports
- Estimate exposure time contribution

---

## Implementation Order

### Sprint 1: Foundation (Weeks 1-2)
1. AABB BVH (`Spatial/AabbBvh.cs`)
2. Spatial grid index (`Spatial/SpatialGrid.cs`)
3. Batch layer slicer with island detection
4. Unit tests for all spatial queries

### Sprint 2: Analysis (Weeks 3-4)
5. Layer-based overhang analyzer
6. Island lifecycle tracking
7. Support point generator with priority scoring
8. Force estimator
9. Unit tests for overhang detection and point placement

### Sprint 3: Routing (Weeks 5-7)
10. NLopt integration (NuGet package)
11. Pinhead optimizer
12. Pillar router with obstacle avoidance
13. Anchor-on-model placer
14. Pillar clustering and sharing
15. Pillar interconnection with structural rules
16. Pillar widening
17. Integration tests with real STL files

### Sprint 4: Meshing (Weeks 8-9)
18. Pinhead mesher (sphere-cone-sphere)
19. Pillar/frustum mesher
20. Junction sphere mesher
21. Bridge mesher
22. Pedestal mesher
23. Mesh merger and vertex welder
24. Manifold validation
25. Unit tests for mesh watertightness

### Sprint 5: Validation (Week 10)
26. Volumetric collision checker (BVH-based)
27. Structural validator (Euler buckling, peel force)
28. Coverage verifier
29. End-to-end validation pipeline
30. Test with 10+ real aerospace STL files

### Sprint 6: Slice Integration (Weeks 11-12)
31. Analytical support slicer
32. Per-layer polygon union (model + supports)
33. Anti-aliased support rendering
34. Support pixel marking (for adaptive exposure)
35. Integration with existing resin slice pipeline
36. Verify slice output matches preview

### Sprint 7: Frontend (Week 13)
37. Instanced mesh rendering
38. Pinhead preview geometry
39. LOD system
40. Structural color coding
41. Performance testing (10k+ supports at 60fps)

### Sprint 8: Advanced Features (Weeks 14-16)
42. Manual support editing improvements
43. Paint-on region enhancements
44. Adaptive exposure support
45. Drain hole integration
46. Weight/cost estimation
47. Print time estimation
48. Export formats (STL, 3MF)

### Sprint 9: Hardening (Weeks 17-18)
49. Stress testing with complex aerospace models
50. Edge case handling (thin walls, internal channels, overhangs-under-overhangs)
51. Performance profiling and optimization
52. Deterministic seeding for reproducibility
53. Comprehensive documentation
54. Certification test suite

---

## File Structure

```
src/HybridSlicer.Infrastructure/Resin/
├── Spatial/
│   ├── AabbBvh.cs              # BVH acceleration structure
│   ├── SpatialGrid.cs          # Grid-based spatial index
│   └── PointRing.cs            # N-point circle for beam sampling
├── Analysis/
│   ├── OverhangAnalyzer.cs     # Layer-based overhang detection
│   ├── IslandTracker.cs        # Island lifecycle detection
│   ├── SupportPointGenerator.cs # Structural point placement
│   └── ForceEstimator.cs       # Peel/recoat/gravity force calc
├── Routing/
│   ├── PinheadOptimizer.cs     # NLopt head orientation optimization
│   ├── PillarRouter.cs         # Obstacle avoidance routing
│   ├── AnchorPlacer.cs         # Support-on-model anchoring
│   ├── PillarClusterer.cs      # Shared pillar grouping
│   └── InterconnectBuilder.cs  # Cross-connection enforcement
├── Meshing/
│   ├── SupportMesher.cs        # Pinhead, pillar, junction, bridge meshes
│   ├── MeshMerger.cs           # Watertight mesh combination
│   └── IndexedTriangleSet.cs   # Vertex + face index mesh structure
├── Validation/
│   ├── SupportValidator.cs     # Volumetric collision + coverage
│   ├── StructuralValidator.cs  # FEA-lite buckling/force checks
│   └── ManifoldValidator.cs    # Watertight mesh verification
├── Slicing/
│   ├── AnalyticalSupportSlicer.cs  # Circle/ellipse cross-sections
│   └── SupportSliceIntegrator.cs   # Per-layer polygon union
├── AdvancedSupportEngine.cs    # Main orchestrator (rewritten)
├── AutoSupportEngine.cs        # Simple mode (keep for quick preview)
├── SupportOptimizer.cs         # Post-processing (rewritten)
├── StlMesh.cs                  # Existing mesh structure
├── MeshValidator.cs            # Existing validator
└── MeshCrossSectionEngine.cs   # Existing cross-section engine
```

---

## Dependencies to Add

| Package | Purpose | License |
|---------|---------|---------|
| `NLoptNet` | Nonlinear optimization (pinhead/routing) | LGPL |

No other external dependencies required. All geometry, meshing, BVH, and spatial indexing is implemented from scratch for full control and no license complications.

---

## Success Criteria

1. **Every support tip contacts the model surface** — verified by BVH ray cast, zero detached tips
2. **Every overhang region is adequately supported** — verified by coverage analysis, zero uncovered regions
3. **Zero support-model collisions** — verified by volumetric beam-cast, zero intersections
4. **Every support can withstand print forces** — verified by Euler buckling and peel force analysis
5. **Support mesh is watertight** — verified by manifold check, zero non-manifold edges
6. **Supports appear in sliced layer images** — verified by analytical slicer output
7. **10,000+ supports render at 60fps** — verified by instanced rendering benchmark
8. **Complex aerospace models** — validated against 10+ real parts with thin walls, channels, overhangs
9. **Deterministic output** — same input always produces exactly the same supports
10. **Total generation time < 5 seconds** for models up to 500k triangles
