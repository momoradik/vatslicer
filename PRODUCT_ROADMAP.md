# VATSlicer — Prototype → Sellable Industrial VPP Product: MASTER ROADMAP
Goal: commercial resin/MSLA slicer at ChiTuBox+Magics parity plus differentiators (physics-grounded
support sizing, ceramic predistortion). Each PHASE is shippable; tasks are code-grounded with
acceptance criteria (AC). Standing rules: every change builds (dotnet + npm) and passes tests; test
on SINAa.stl ROTATED to {0°, rotX45°, rotY90°, rotX30°+rotZ60°}; no feature ships without an
automated test + its AC met; Auto-mode output unchanged except for bug fixes; commit+push after each
task; write PHASE_<n>_REPORT.md after each phase.

PHASE 0 — STABILIZE THE SUPPORT CORE (do first):
- No floating geometry: a single finalValidIds (computed AFTER emission gate + collision + structural
  + escalation ladder + floater net) drives ALL geometry (mesh, slice elements, braces, rafts,
  fork/tree); add a final invariant pass that drops any orphan; brace only if BOTH endpoints survive.
- Fast island+minima detection via per-layer 1-bit rasters (Island[L]=B[L] AND NOT Dilate(B[L-1],r),
  r=ceil(tan(maxSelfSupportAngle)*layerHeight/pixelPitch)) + spatial-grid minima; remove the >100K guard.
- Reinforcement: SupportV2Controller EnableInterconnections = enableInterconnections && reinfMode!=None
  (was ||); engine guard; make Global != Triangular (Triangular=triangle edges capped to
  clamp(1.5*medianSpacing,8,25)mm; Global=full Delaunay+hull, no cap); brace only tall grounded pillars
  (height>=ReinforcementStartHeightMm) so line contact doesn't float braces.
- Forking: cluster on contact-point spacing (not junctions); multiplier max(.,2.0) if MaxTipsPerFork>=3,
  max(.,2.6) if >=4; ForkBuilder builds MAXIMAL clusters up to MaxTipsPerFork; trunkR=sqrt(sum r_i^2).
- Line contact: tag SupportPoint.LineContactGroupId; build a thin connecting rib under the edge
  (ribR=clamp(0.5*tipR,0.15,0.35)mm) in BOTH mesh and AnalyticalSupportSlicer (Type="linerib").
- Manual supports: force straight-down pinhead + anchor fallback so every click yields a support;
  per-support sizingMode auto|custom (fix hardcoded layerArea=25/supportsInLayer=1); apply
  FilletBuilder.GenerateTipCove + FilletRoute + meshSides>=12 so manual joints match auto.
- Contact tip: route starts EXACTLY at pinhead.JunctionPoint for routing AND mesh AND slicer (no
  synthesized variant); fillet the contact->...->junction->pillar chain; place the contact sphere
  center at P + n*(R_contact - d) with small contact depth d (default 0.08mm, configurable, 0<=d<=R)
  so the tip TOUCHES (penetration==d), not buries.
AC: combinatorial matrix (every supportType x reinforcement x raft x shape, auto+manual) at the 4
rotations -> 0 floaters, preview==print (feature set in mesh == in SliceElements), every toggle
visibly behaves, joints smooth, junction-gap ~0, penetration==d. Write COMBO_TEST_MATRIX.md.

PHASE 1 — DRIVE REAL PRINTERS (the #1 sellability blocker; today only loose PNGs+JSON):
- New src/HybridSlicer.Infrastructure/Resin/Exporters/: encoders for .ctb (incl v3/v4 + encryption),
  .cbddlp, .photon/.photons, .pwmx/.pwms/.pwmb, .pm3/.pm5, .sl1/.sl1s, and generic ZIP+PNG. Use the
  open UVtools format specs as reference. Map layer PNGs + MachineProfile/ResinPrintProfile fields
  (exposure, lift dist/speed, bottom layers, light-off, AA) into each format header.
- Wire MachineProfile.ExportFormat -> exporter in ResinSliceController; add a frontend Export button +
  format picker + download.
- Per-pixel grayscale/anti-aliased exposure in LayerRasterizer (map edge AA to pixel intensity).
- Implement the top-down/recoater slicing sequence (recoater fields exist but are unused).
AC: export a real .ctb and .pwmx, open in UVtools (and print if possible) with correct layers/exposure/
lift; AA visible as grayscale edges.

PHASE 2 — SUPPORT ENGINE TO INDUSTRIAL GRADE:
- Slice-based island+minima+overhang as default detection (Phase 0 made it fast).
- Hybrid router: cheap vertical-drop fast path (occupancy/column test) for the ~85% straight-down
  supports; motion-planner only for the hard minority; keep parallel routing. Target ~1-3s interactive.
- All topologies distinct & working: single, forked, tree, line(rib), face, plus a Magics-style
  down-projection+hatch option; reinforcement None/Pairwise/Triangular/Global distinct; rafts modes.
- Calibrate P_ADH from a real part/film; presets map to it; keep the "too thin" safety warning.
- Pro manual: paint-to-support, drag-to-move, add/delete, per-support edit, save/reuse profiles,
  support-on-support.
- Enforce preview==print and no-floater invariants in code + tests.
AC: interactive-fast on SINAa at every rotation; 0 floaters; every mode visibly distinct; physics
sizing validated on a known part.

PHASE 3 — PRE-PROCESSING PARITY:
- General model hollowing (offset inner shell + wall thickness + infill) — missing today for models.
- Industrial mesh auto-repair: hole filling, non-manifold edges, self-intersections, shell orientation
  (beyond current degenerate+normal-flip in MeshValidator).
- Import 3MF + OBJ + ASCII STL (binary-only today).
- Peel-aware auto-orientation: feed ForceEstimator peel/cross-section into AutoOrientOptimizer scoring.
- Suction/trapped-volume (cup) detection + UI warnings.
- Model layout: duplication, auto-arrange/nesting (bin-pack), model-model collision, batch ops.
AC: hollow+drain+slice a model; repair a CAD STL that fails today; import a 3MF; auto-orient reduces
support area; suction pockets flagged in UI.

PHASE 4 — ANALYSIS, WORKFLOW & UX:
- Analyze overlay (red/yellow/green unsupported/at-risk) in 3D (backend heatmap exists; wire UI).
- Live slice preview; per-layer inspection.
- Dynamic support panel: live cross-section diagram + tip-angle control + save/reuse profiles.
- Resin volume + cost + accurate print time (today rough; no volume/cost).
- Undo/redo (command pattern).
- Project save/load FULL state (model + transforms + orientation + manual supports + painting +
  settings) as .vatproj (today only STL path is saved -> user work lost); session persistence.
- Professional UI polish.
AC: place supports + hollow + orient + save project + reload -> all restored; undo works; Analyze
overlay matches supports; cost/time shown and realistic.

PHASE 5 — DIFFERENTIATORS:
- Validated physics sizing (calibration + test prints).
- Ceramic predistortion / scan-driven compensation pipeline: morph nominal mesh by inverse deviation
  field, iterative fixed-point with relaxation alpha, volumetric (RBF/FFD) warping, repeatability gate,
  frozen-process constraint (modules designed, not yet built).
- Top-down VPP support physics (compression + recoater shear).
AC: a physics-sized print meets spec; a ceramic core moves toward the 50um tolerance through the loop.

PHASE 6 — PRODUCTIZATION & RELIABILITY:
- CI test harness: unit + full combinatorial support matrix + no-floater & preview==print invariants +
  golden-fingerprint regression gates, on every commit.
- Performance benchmark harness (fail CI on regressions).
- Robust error handling + telemetry + crash reporting; no silent catches.
- Packaging: signed Windows installer bundling deps + auto-update.
- Licensing/activation + tiers.
- Docs + first-run onboarding wizard.
AC: clean-machine install -> activate -> slice -> export -> print -> auto-update; CI green and gating.

PHASE 7 — VALIDATION & CERTIFICATION (human-verified):
- Real-print validation matrix across printers/resins (support removal, surface, dimensional accuracy).
- Documented calibration (P_ADH per resin/film; exposure; compensation).
- Accuracy verification vs targets incl. 50um ceramic tolerance; metrology 4-10x tighter.
- Verification framework + release sign-off gate.
AC: a signed validation report; reproducible accuracy on real hardware.

SEQUENCING: 0 -> 1 (MVP-sellable) -> 2 -> 3 -> 4 (ChiTuBox parity) -> 5 (beyond) -> 6 -> 7.
