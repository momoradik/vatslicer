# Support Generation — Complete Code & Math Deep Dive

Every support option, from contact point detection to final triangle mesh.

---

## THE UNIVERSAL PIPELINE (all support types share this)

Every support, regardless of type, passes through the same pipeline:

```
Overhang Detection → Point Sampling → Pinhead Optimization → Pillar Routing
→ Physics Sizing → Mesh Generation → Collision Validation → Structural Validation
```

### Stage 1: Overhang Detection

**File:** `SupportPointGenerator.cs:80-128`

**The math:** A triangle is an overhang if its surface normal points sufficiently downward.

```
normalZThreshold = -cos(overhangAngleDeg × π / 180)

Default: -cos(45° × π/180) = -0.70711

Test: if (normal.Z < -0.70711) → overhang

This means: any surface tilted more than 45° from horizontal, facing downward.
```

For each overhang triangle, compute:
- **Area** = `|cross(v1-v0, v2-v0)| × 0.5` (half the cross product magnitude)
- **Centroid** = `(v0 + v1 + v2) / 3`
- **Steepness** = `|normal.Z|` (0 = horizontal, 1 = vertical)

Two paths based on mesh size:
- **>50K triangles (fast path):** Uses STL file normals directly. One pass, O(n).
- **≤50K triangles:** Builds a HalfEdgeMesh for topologically consistent normals via BFS winding propagation, then global orientation via 20-sample majority vote ray casting.

### Stage 2: Contact Point Sampling (Poisson-Disk)

**File:** `SupportPointGenerator.cs:166-250`

**Base spacing formula:**
```
baseSpacing = MinSpacingMm + (MaxSpacingMm - MinSpacingMm) × (1 - DensityFactor)

Light (0.3):  2 + 6×0.7 = 6.2mm
Medium (0.5): 2 + 6×0.5 = 5.0mm
Heavy (0.8):  2 + 6×0.2 = 3.2mm
```

**Adaptive spacing per triangle** (geometric mode):
```
spacing = baseSpacing × (1.5 - steepness × 0.5)

Steep overhang (steepness=1.0): spacing = baseSpacing × 1.0 (no change)
Gentle slope (steepness=0.0):   spacing = baseSpacing × 1.5 (sparser)
```

**Force-driven mode** (replaces geometric when enabled):
```
Pre-compute peel force per triangle via Z-binned area:
  areaBins[z/2mm] = sum of overhang area in that Z band
  peelForce[tri] = (areaBins[bin] + neighbors) × 0.015 N/mm²

Map to spacing:
  t = (peelForce - min) / (max - min)     // 0=low force, 1=high force
  spacing = MaxSpacing - t × (MaxSpacing - MinSpacing)
  High force → small spacing (dense)
  Low force  → large spacing (sparse)
```

**Candidate generation per triangle:**
```
nSamples = max(1, floor(area / spacing²))

Sample 1: always the centroid
Samples 2+: jittered barycentric grid
  gridSize = ceil(sqrt(nSamples))
  u = (row + 0.35 + jitterX) / gridSize    // jitterX = deterministic from index
  v = (col + 0.35 + jitterY) / gridSize
  clamp u,v to [0.01, 0.98]
  point = v0 + u×(v1-v0) + v×(v2-v0)      // if u+v ≤ 1
```

**Poisson-disk acceptance:** Uses a `SpatialGrid` (uniform hash grid with cell size = baseSpacing).
```
For each candidate:
  if grid.ExistsInRadius(candidate, spacing) → REJECT (too close to existing point)
  else → ACCEPT, insert into grid
```

### Stage 3: Pinhead Optimization

**File:** `PinheadOptimizer.cs:74-385`

**Pinhead geometry (the contact mechanism):**
```
The pinhead is a small cone-like shape connecting the support tip to the model surface.

totalLength = pinRadius + headWidth + backRadius

Layout along direction vector 'dir' from the contact point:
  pinCenter  = contact + dir × (pinRadius - penetration)
  backCenter = contact + dir × (totalLength - backRadius - penetration)
  junction   = contact + dir × (totalLength - penetration)

Default values:
  pinRadius = 0.2mm (the tiny tip touching the model)
  backRadius = 0.5mm (the wider end connecting to the pillar)
  headWidth = 1.0mm (the cone height)
  penetration = 0.05mm (how deep the tip sinks into the model)
```

**Nelder-Mead optimization:** Searches for the direction that maximizes clearance.

```
Parameter space: (θ, φ) = (polar angle from -Z, azimuthal angle)
  θ ∈ [0, maxSlope]  (typically 0 to 45°)
  φ ∈ [0, 2π)

Direction from (θ, φ):
  dir = (sin(θ)×cos(φ), sin(θ)×sin(φ), -cos(θ))

Simplex: 3 vertices in (θ, φ) space
  v0 = initial direction (surface normal clamped to max slope)
  v1 = v0 + (30% of maxSlope, +0.8 rad azimuth)
  v2 = v0 + (-20% of maxSlope, -0.8 rad azimuth)

Objective: minimize -clearance (= maximize clearance)

Nelder-Mead operations (each iteration):
  Sort vertices by objective value
  Compute centroid of best 2 vertices:
    cθ = (v0.θ + v1.θ) / 2
    cφ = (v0.φ + v1.φ) / 2

  Reflection (α=1.0):
    rθ = cθ + 1.0 × (cθ - v2.θ)
    rφ = cφ + 1.0 × (cφ - v2.φ)
    Clamp rθ to [0, maxSlope]

  If reflection better than v1:
    If better than v0: try Expansion (γ=2.0):
      eθ = cθ + 2.0 × (rθ - cθ)
      Keep better of expansion vs reflection
    Else: accept reflection

  Else: Contraction (ρ=0.5):
    kθ = cθ + 0.5 × (v2.θ - cθ)
    If better: accept contraction
    Else: Shrink (σ=0.5): move all toward best

  Converges when |worst - best| < 0.001 rad
  Max 60 iterations
```

**Clearance evaluation** (called at every Nelder-Mead step):

```
Sample 8 cross-sections along pinCenter→junction:
  For i = 0 to 7:
    t = i / 7                                    // 0 to 1 along path
    samplePoint = lerp(pinCenter, junction, t)
    sampleRadius = pinRadius + (backRadius - pinRadius) × t

    closestSurface = bvh.ClosestPoint(samplePoint)
    clearance = closestSurface.distance - sampleRadius

    if t < 0.2: clearance = max(clearance, -pinRadius)  // allow penetration near tip
    minClearance = min(minClearance, clearance)

Beam-cast validation (8 rays in cone):
  pathDir = normalize(junction - pinCenter)
  beamClearance = bvh.BeamCast(pinCenter, pathDir, backRadius, 8, pathLength)
  if beamClearance < pathLength × 0.8 → reduce minClearance

Curvature relaxation:
  cpFirst = bvh.ClosestPoint(lerp(pinCenter, junction, 0.1))
  cpLast = bvh.ClosestPoint(lerp(pinCenter, junction, 0.9))
  curvature = |lastDist - firstDist| / pathLength
  relaxation = clamp(curvature × 5, 0, 0.6)
  clearanceReq = -pinRadius × 0.3 × (1 - relaxation)

Valid if: minClearance > clearanceReq
```

**Fallback chain:**
1. Try surface normal → if valid, return
2. Nelder-Mead 60 iterations → if valid, return
3. Reduce radii to 70%, 50%, 30% and retry 1+2 at each scale
4. Accept-with-tilt: if best clearance > -pinRadius×0.5, force accept at 70% size
5. Near-bed (Z<3mm): compact pinhead with min 0.15mm pin, 0.3mm back
6. Manual supports: force straight-down pinhead regardless

### Stage 4: Pillar Routing

**File:** `PillarRouter.cs:47-395`

**Strategy 1 — Direct descent:**
```
Cast beam straight down from junction:
  clearance = bvh.BeamCast(junction, -UnitZ, pillarRadius, 8 rays, heightToBase)

  If clearance ≥ heightToBase - 0.1mm → build vertical pillar:
    segments = max(3, ceil(height / 10mm))
    For each segment i:
      t = i / segments
      z = junctionZ - height × t
      radius = junctionRadius + wideningFactor × height × t
      Add Waypoint(x, y, z, radius, "pillar")

    Add base Waypoint(x, y, 0, baseRadius, "base")
```

**Strategy 2 — Bridge-and-descend:**
```
Coarse grid search: 16 azimuths × 3 slopes × 4 lengths = 192 candidates

For each candidate:
  bridgeDir = (sin(slope)×cos(azimuth), sin(slope)×sin(azimuth), -cos(slope))
  bridgeEnd = junction + bridgeDir × length

  Collision check: bvh.BeamCast(junction, bridgeDir, radius, 8, length)
  Must be clear AND bridgeEnd must have clear descent to plate

  Score = (junction.Z - bridgeEnd.Z) × 2 - length × 0.5
  (Prefer: more descent gained, shorter bridge)

If best candidate found: build bridge + vertical descent
If not: try Nelder-Mead refinement from 6 random azimuths (30 iterations each)
If still not: chain up to 3 bridges recursively
```

**Strategy 3 — Anchor:**
```
Cast ray straight down: hit = bvh.RayCast(junction, -UnitZ)
If hit exists AND hit.normal.Z ≥ 0.3 AND distance ≥ 2mm:
  Build pillar from junction to hit.point + 0.2mm above surface
  Type = "anchor", ReachesGround = false
```

### Stage 5: Physics Sizing

**File:** `SupportSizer.cs:95-136`

```
Peel force per support (bottom-up printing):
  F = (P_adhesion × A_layer) / n_supports
  P_adhesion = 0.015 N/mm² (FEP film adhesion pressure)

Tip radius (must resist tensile failure at the bond joint):
  r_tip = √(F × SF / (π × σ_bond))
  σ_bond = 15 MPa (green resin bond strength)
  SF = 2.0 (safety factor)
  r_tip = max(r_tip, 0.25mm)       // minimum floor

Contact sphere radius:
  r_contact = r_tip × 1.6           // visible bead at touch point

Pillar radius (must resist tension + lateral sway):
  r_tensile = √(F × SF / (π × σ_resin))
  σ_resin = 40 MPa (cured resin tensile strength)

  r_stiffness = 0.004 × supportHeight   // stiffness floor (0.4% of height)

  r_pillar = max(r_tensile, r_stiffness, 0.3mm)

Base radius (flared for build plate adhesion):
  r_base = r_pillar × 2.7
  h_base = r_base × 0.8
```

### Stage 6: Mesh Generation (the actual triangles)

**File:** `SupportMesher.cs`

**Frustum (tapered cylinder) — the core primitive:**
```
Input: radiusA (bottom), radiusB (top), height, sides (6-24)

1. Generate vertex rings:
   For i = 0 to sides-1:
     angle = 2π × i / sides
     topRing[i] = (cos(angle) × rTop, height, sin(angle) × rTop)
     botRing[i] = (cos(angle) × rBottom, 0, sin(angle) × rBottom)

2. Side faces (2 triangles per quad):
   For i = 0 to sides-1:
     next = (i+1) % sides
     Triangle(topRing[i], botRing[i], botRing[next])
     Triangle(topRing[i], botRing[next], topRing[next])

3. Caps:
   topCenter = (0, height, 0)
   botCenter = (0, 0, 0)
   For i = 0 to sides-1:
     Triangle(topCenter, topRing[(i+1)%sides], topRing[i])
     Triangle(botCenter, botRing[(i+1)%sides], botRing[i])

Total triangles = sides × 2 (sides) + sides (top cap) + sides (bottom cap)
                = sides × 4
For 6 sides = 24 triangles per frustum
```

**Oriented frustum (rotated to connect two 3D points):**
```
1. Build frustum along Y axis (default)
2. Compute rotation quaternion from Y-axis to (pointB - pointA):
   dir = normalize(pointB - pointA)

   if dot(dir, UnitY) < -0.999:  // nearly opposite
     rotation = 180° around X axis
   elif dot(dir, UnitY) > 0.999:  // nearly aligned
     rotation = identity
   else:
     rotation = shortest-arc quaternion from UnitY to dir
     axis = normalize(cross(UnitY, dir))
     angle = acos(dot(UnitY, dir))
     rotation = Quaternion(axis, angle)

3. Transform every vertex:
   v' = rotation × v + pointA
```

**Sphere (UV sphere):**
```
1. Top pole at (0, radius, 0)
2. Ring vertices:
   For ring r = 1 to rings-1:
     φ = π × r / rings
     y = cos(φ) × radius
     ringRadius = sin(φ) × radius
     For side s = 0 to sides-1:
       θ = 2π × s / sides
       vertex = (cos(θ) × ringRadius, y, sin(θ) × ringRadius)

3. Bottom pole at (0, -radius, 0)
4. Connect: pole→first ring, ring strips, last ring→bottom pole
```

**Per-support mesh assembly:**
```
For each support:
  1. Contact sphere: OrientedSphere(contactPoint, contactSphereRadius, 4 rings, N sides)
  2. Taper: OrientedFrustum(contactPoint, routeStart, tipRadius, pillarRadius, N sides)
  3. For each route segment (wp1 → wp2):
     OrientedFrustum(wp1.pos, wp2.pos, wp1.radius, wp2.radius, N sides)
  4. Junction sphere at each intermediate waypoint:
     OrientedSphere(wp.pos, wp.radius, 4 rings, N sides)
  5. Mini raft (if enabled):
     MiniRaft.Generate(basePos, baseRadius, margin, thickness, 12 sides)
```

### Stage 7: Validation

**Collision (`CollisionValidator.cs`):**
```
For each support:
  Pinhead check:
    if bvh.IsInside(backCenter) → collision
    if bvh.IsInside(junction) → collision
    beamCast(backCenter → junction, 50% backRadius, 8 rays) must be 80% clear

  Pillar check:
    For each segment (wp1, wp2):
      beamCast(wp1 → wp2, max(r1,r2), 8 rays) must be 95% clear
      if bvh.IsInside(wp2) → collision
```

**Structural (`StructuralValidator.cs`):**
```
Euler buckling:
  I = π × r⁴ / 4                    (moment of inertia for circle)
  P_critical = π² × E × I / L²      (Euler formula)
  E = 2000 MPa (cured resin elastic modulus)

  SF_buckling = P_critical / F_total
  Must be ≥ 2.0

Combined tension + bending:
  σ_axial = F / (π × r²)
  M = F_peel × bendingArm           (eccentric peel load)
  Z = π × r³ / 4                    (section modulus)
  σ_bending = M / Z
  σ_combined = σ_axial + σ_bending
  SF_combined = σ_yield / σ_combined
  Must be ≥ 2.0
```

---

## OPTION-SPECIFIC DETAILS

### FORKED SUPPORTS (ForkBuilder.cs)

**Clustering:**
```
For each unassigned valid pinhead i:
  Find all unassigned pinheads j within ForkClusterRadiusMm (4mm) in XY
  Take up to MaxTipsPerFork (4) nearest
  If cluster.size < 2 → skip
```

**Fork node computation:**
```
centroid.X = average(junction.X for all tips)
centroid.Y = average(junction.Y for all tips)

For each tip:
  xyDist = √((junction.X - centroid.X)² + (junction.Y - centroid.Y)²)
  requiredDrop = xyDist / tan(criticalAngle)    // 45° → tan=1.0 → drop = xyDist

fork.Z = min(junction.Z across tips) - max(requiredDrop across tips)
Must be > 1.0mm (above the plate)
```

**Route assembly per forked tip:**
```
[junction waypoint at tip]
→ [bridge waypoint at fork node]  (strut from tip to shared point)
→ [shared trunk from fork node to plate via PillarRouter.Route()]
```

**Trunk radius:**
```
r_trunk = √(Σ r_branch²)    // area-equivalent: πr² = Σπr_i²
```

### LINE CONTACT (SupportPointGenerator.cs)

**Edge detection:**
```
1. Build edge→face map from overhang triangles
   EdgeKey = symmetric hash of two endpoint positions (quantized to 0.001mm)

2. For each edge shared by 2 overhang triangles:
   avgNormal = normalize(face1.normal + face2.normal)
   if avgNormal.Z ≥ threshold → skip (not downward-facing crease)
   if |ΔZ along edge| / edgeLength > 0.5 → skip (too vertical)
   if edgeLength < 0.5mm → skip (too tiny)

3. Sample along accepted edges:
   nSamples = max(2, ceil(edgeLength / LineContactSpacingMm))
   For i = 0 to nSamples-1:
     t = i / (nSamples-1)
     position = lerp(edgeStart, edgeEnd, t)
     if existing point within spacing×0.6 → skip
     else → add support point with normal = avgNormal
```

### FACE CONTACT (SupportPointGenerator.cs)

**Grid sampling on large flat overhangs:**
```
For each overhang triangle with:
  area ≥ FaceContactAreaThresholdMm2 (50mm²)
  normal.Z < -0.7 (near-horizontal)

Grid over the triangle's XY bounding box:
  For gx from minX to maxX, step FaceGridSpacingMm (3mm):
    For gy from minY to maxY, step FaceGridSpacingMm:
      if PointInTriangleXY(gx, gy, v0, v1, v2):
        gz = InterpolateZ(gx, gy, v0, v1, v2)
        if no existing point within spacing×0.5:
          add support point at (gx, gy, gz)

PointInTriangleXY (barycentric sign test):
  d1 = (px-b.X)×(a.Y-b.Y) - (a.X-b.X)×(py-b.Y)
  d2, d3 similarly for other edges
  Inside = all same sign

InterpolateZ (plane equation):
  n = cross(b-a, c-a)
  gz = a.Z - (n.X×(px-a.X) + n.Y×(py-a.Y)) / n.Z
```

### TRIANGULATED TRUSS (InterconnectBuilder.cs)

**Delaunay triangulation (Bowyer-Watson):**
```
1. Create super-triangle enclosing all pillar XY positions (×10 margin)

2. For each pillar point p:
   Find all "bad" triangles whose circumcircle contains p:
     InCircumcircle test (3×3 determinant):
       ax=a.X-p.X, ay=a.Y-p.Y, bx=b.X-p.X, by=b.Y-p.Y, cx=c.X-p.X, cy=c.Y-p.Y
       det = ax×(by×(cx²+cy²) - cy×(bx²+by²))
           - ay×(bx×(cx²+cy²) - cx×(bx²+by²))
           + (ax²+ay²)×(bx×cy - by×cx)
       Inside if: (det>0 for CCW triangle) or (det<0 for CW)

   Extract boundary polygon of the hole (non-shared edges of bad triangles)
   Remove bad triangles
   Re-triangulate: for each boundary edge, add triangle (edge.a, edge.b, p)

3. Remove triangles sharing vertices with super-triangle

Result: unique edges of the Delaunay triangulation
```

**Brace placement along Delaunay edges:**
```
For each edge (pillar_i, pillar_j):
  Triangular mode: skip if XY distance > MaxConnectionDistMm
  Global mode: keep all edges

  Z overlap range:
    minZ = max(base_i.Z, base_j.Z) + 0.5mm
    maxZ = min(top_i, top_j) - 0.5mm

  If no overlap: try single brace at midpoint of tops

  Else: place at intervals:
    pairMax = min(6, max(1, floor(overlap / intervalMm)))
    For z from minZ+interval to maxZ, step interval:
      ptA = (base_i.X, base_i.Y, z)
      ptB = (base_j.X, base_j.Y, z)
      Collision check: bvh.BeamCast(ptA, dir, strutRadius, 8 rays, length)
      Accept if clearance ≥ length - 0.1mm
```

### FULL-PLATE RAFT (SupportEngineV2.cs + LatticeBase.cs)

**Footprint computation:**
```
For each plate-routed support:
  fpMinX = min(base.X - baseRadius) - raftMarginMm
  fpMinY = min(base.Y - baseRadius) - raftMarginMm
  fpMaxX = max(base.X + baseRadius) + raftMarginMm
  fpMaxY = max(base.Y + baseRadius) + raftMarginMm
```

**Lattice generation (Grid pattern):**
```
Compute raft radius from footprint
Generate at RaftThicknessMm height:

1. Top/base connecting rings (frustum at top, base at Z=0)
2. Mid-height horizontal struts (X-aligned and Y-aligned):
   For each Y from -midRadius+spacing to +midRadius:
     chordHalf = √(midRadius² - Y²)    // chord intersection with circle
     strut from (-chordHalf, Y, midZ) to (+chordHalf, Y, midZ)
   Same for X-aligned struts

3. Vertical struts at grid intersections
4. Central vertical strut through origin

Each strut = OrientedFrustum(ptA, ptB, strutRadius, strutRadius, 8 sides)
```

### DRAINAGE-AWARE (SupportEngineV2.cs + DrainHolePlacer.cs)

**Trap detection:**
```
Slice mesh top-to-bottom at 1mm layers
For each layer, compute total contour area

Scan for area decreases (pocket closing):
  For each layer i:
    Find local area peak in ±5mm window
    bottomArea = area at peak - 5mm
    decreaseRatio = (peakArea - bottomArea) / peakArea

    If decreaseRatio ≥ 0.50 AND peakArea > 5mm²:
      trappedVolume = Σ(peakArea - layerArea) × layerHeight
      → Mark as resin trap

Drain hole placement:
  For each trap with volume ≥ 50mm³:
    interiorPoint = (centroid.X, centroid.Y, bottomZ)
    surfacePoint = bvh.ClosestPoint(interiorPoint)
    Ensure normal points outward (flip if IsInside test fails)
    Prefer lower Z positions (gravity drainage)

Exclusion zones:
  For each drain hole: exclude support points within max(holeDiameter, MinDrainGapMm)
```

### MINI RAFT (MiniRaft.cs)

**Geometry:**
```
raftRadius = baseRadius + 1.5mm
chamferHeight = thickness × 0.4 = 0.12mm
bodyHeight = thickness × 0.6 = 0.18mm

topZ = baseCenter.Z (= plate level)
chamferZ = topZ - bodyHeight
botZ = topZ - thickness

chamferRadius = raftRadius - chamferHeight × 0.5

Three vertex rings (24 sides each):
  topRing:    (cx + cos(θ)×raftRadius, cy + sin(θ)×raftRadius, topZ)
  chamferRing:(cx + cos(θ)×raftRadius, cy + sin(θ)×raftRadius, chamferZ)
  botRing:    (cx + cos(θ)×chamferRadius, cy + sin(θ)×chamferRadius, botZ)

Faces:
  Top cap: 24 triangles (fan from center)
  Upper wall: 48 triangles (topRing → chamferRing)
  Chamfer wall: 48 triangles (chamferRing → botRing, angled)
  Bottom cap: 24 triangles (fan, reversed winding)
Total: 144 triangles per mini raft
```

### HOLLOW SUPPORT (HollowedSupport.cs)

**For tall pillar segments (>20mm):**
```
innerRadiusTop = max(outerRadiusTop - wallThickness, outerRadiusTop × 0.1)
innerRadiusBot = max(outerRadiusBot - wallThickness, outerRadiusBot × 0.1)

If inner < 0.1mm: fall back to solid frustum

4 vertex rings: outerTop, outerBot, innerTop, innerBot

Outer wall (normal faces out):
  Triangle(outerTop[i], outerBot[i], outerBot[next])
  Triangle(outerTop[i], outerBot[next], outerTop[next])

Inner wall (normal faces in — reversed winding):
  Triangle(innerTop[i], innerBot[next], innerBot[i])
  Triangle(innerTop[i], innerTop[next], innerBot[next])

Top annular cap (outer→inner):
  Triangle(outerTop[i], outerTop[next], innerTop[next])
  Triangle(outerTop[i], innerTop[next], innerTop[i])

Bottom annular cap (outer→inner, reversed):
  Similar with reversed winding
```
