import { useState, useCallback, useRef, useMemo, useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import StlViewer, {
  type BuildVolume,
  type ModelTransform,
  type ModelEntry,
  type StlViewerHandle,
  DEFAULT_TRANSFORM,
} from '../components/viewer/StlViewer'
import PrintProfilePanel from '../components/PrintProfilePanel'
import MaterialProfilePanel from '../components/MaterialProfilePanel'
import { SupportOptionsPicker, DEFAULT_SUPPORT_OPTIONS, type SupportOptionsConfig } from '../components/support-picker'
import { machineProfilesApi, resinPrintProfilesApi, resinSliceApi, meshApi, supportV2Api, prepToolsApi, type AdvancedSupportData, type CrossBraceData } from '../api/client'

// ── Per-object settings override ──────────────────────────────────────────────

interface ObjectSettings {
  // Exposure overrides (null = use global from material/printer)
  exposureMs: number | null
  bottomExposureMs: number | null
  liftDistanceMm: number | null
  liftSpeedMmPerMin: number | null
  // Support overrides (null = use global from print profile)
  supportEnabled: boolean | null
  supportType: string | null            // 'normal' | 'tree'
  supportPlacement: string | null       // 'buildplate' | 'everywhere'
  supportDensity: number | null         // 0..1
  supportPattern: string | null
  supportOverhangAngleDeg: number | null
  supportXYDistanceMm: number | null
  supportZDistanceMm: number | null
  supportInterfaceEnabled: boolean | null
  // Hollowing overrides
  hollowingEnabled: boolean | null
  hollowWallThicknessMm: number | null
}

const DEFAULT_SETTINGS: ObjectSettings = {
  exposureMs: null, bottomExposureMs: null,
  liftDistanceMm: null, liftSpeedMmPerMin: null,
  supportEnabled: null, supportType: null, supportPlacement: null,
  supportDensity: null, supportPattern: null, supportOverhangAngleDeg: null,
  supportXYDistanceMm: null, supportZDistanceMm: null, supportInterfaceEnabled: null,
  hollowingEnabled: null, hollowWallThicknessMm: null,
}

// ── Support presets (single source of truth for ALL placement methods) ────────
// All values are RADII in mm. Both manual and auto support paths read from here.
// The backend expects radii for pinRadius, backRadius, pillarRadius, baseRadius.

const SUPPORT_PRESETS: Record<string, { pin: number; back: number; pillar: number; base: number }> = {
  'light':          { pin: 0.1,  back: 0.3,  pillar: 0.3,  base: 1.2 },
  'medium':         { pin: 0.2,  back: 0.5,  pillar: 0.5,  base: 2.0 },
  'heavy':          { pin: 0.4,  back: 0.75, pillar: 0.75, base: 3.0 },
  'point-tip':      { pin: 0.08, back: 0.3,  pillar: 0.35, base: 1.0 },
  'needle-tip':     { pin: 0.05, back: 0.2,  pillar: 0.3,  base: 1.0 },
  'mushroom-tip':   { pin: 0.3,  back: 0.5,  pillar: 0.5,  base: 2.0 },
  'cross-tip':      { pin: 0.3,  back: 0.5,  pillar: 0.5,  base: 2.2 },
}

// ── Independent mesh-based ground truth (for reconciliation completeness test) ─
// This code shares NOTHING with SupportFingerprint or computeGeometryDelta.
// It reads raw mesh geometry (preview STL triangles / final segment endpoints)
// and computes bbox + surface area + tri count. Used to verify that the fingerprint
// catches every divergence the meshes exhibit.

interface MeshEnvelope {
  bboxW: number; bboxD: number; bboxH: number  // axis-aligned bounding box dimensions
  surfaceArea: number                           // total surface area
  triCount: number                              // number of triangles
}

/** Parse a binary STL from base64 and compute its MeshEnvelope. */
function envelopeFromStlBase64(b64: string): MeshEnvelope {
  const bin = atob(b64)
  const buf = new Uint8Array(bin.length)
  for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i)
  const dv = new DataView(buf.buffer)
  const triCount = dv.getUint32(80, true)
  let minX = Infinity, minY = Infinity, minZ = Infinity
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity
  let surfaceArea = 0
  for (let t = 0; t < triCount; t++) {
    const off = 84 + t * 50
    // skip normal (12 bytes), read 3 vertices (36 bytes)
    const verts: [number, number, number][] = []
    for (let v = 0; v < 3; v++) {
      const vOff = off + 12 + v * 12
      const x = dv.getFloat32(vOff, true), y = dv.getFloat32(vOff + 4, true), z = dv.getFloat32(vOff + 8, true)
      verts.push([x, y, z])
      minX = Math.min(minX, x); minY = Math.min(minY, y); minZ = Math.min(minZ, z)
      maxX = Math.max(maxX, x); maxY = Math.max(maxY, y); maxZ = Math.max(maxZ, z)
    }
    // triangle area = 0.5 * |cross(AB, AC)|
    const [ax, ay, az] = [verts[1][0] - verts[0][0], verts[1][1] - verts[0][1], verts[1][2] - verts[0][2]]
    const [bx, by, bz] = [verts[2][0] - verts[0][0], verts[2][1] - verts[0][1], verts[2][2] - verts[0][2]]
    const cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx
    surfaceArea += 0.5 * Math.sqrt(cx * cx + cy * cy + cz * cz)
  }
  return {
    bboxW: triCount > 0 ? maxX - minX : 0,
    bboxD: triCount > 0 ? maxY - minY : 0,
    bboxH: triCount > 0 ? maxZ - minZ : 0,
    surfaceArea, triCount,
  }
}

/** Reconstruct a MeshEnvelope from a segment list (independent of SupportFingerprint).
 *  Computes bbox from all segment endpoints, surface area from frustum lateral areas,
 *  and triCount from frustum face estimates (2 * sides per segment). */
function envelopeFromSegments(segments: { part: string; x1: number; y1: number; z1: number; r1: number; x2: number; y2: number; z2: number; r2: number }[]): MeshEnvelope {
  let minX = Infinity, minY = Infinity, minZ = Infinity
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity
  let surfaceArea = 0
  const sides = 8 // match engine's default tessellation
  for (const s of segments) {
    // Expand bbox by segment endpoints + radius (cylinder envelope)
    minX = Math.min(minX, s.x1 - s.r1, s.x2 - s.r2); maxX = Math.max(maxX, s.x1 + s.r1, s.x2 + s.r2)
    minY = Math.min(minY, s.y1 - s.r1, s.y2 - s.r2); maxY = Math.max(maxY, s.y1 + s.r1, s.y2 + s.r2)
    minZ = Math.min(minZ, s.z1 - s.r1, s.z2 - s.r2); maxZ = Math.max(maxZ, s.z1 + s.r1, s.z2 + s.r2)
    // Frustum lateral surface area = π(r1+r2) * slant_height
    const dx = s.x2 - s.x1, dy = s.y2 - s.y1, dz = s.z2 - s.z1
    const h = Math.sqrt(dx * dx + dy * dy + dz * dz)
    const slant = Math.sqrt(h * h + (s.r1 - s.r2) * (s.r1 - s.r2))
    surfaceArea += Math.PI * (s.r1 + s.r2) * slant
    // Caps: π*r²
    surfaceArea += Math.PI * s.r1 * s.r1 + Math.PI * s.r2 * s.r2
  }
  return {
    bboxW: segments.length > 0 ? maxX - minX : 0,
    bboxD: segments.length > 0 ? maxY - minY : 0,
    bboxH: segments.length > 0 ? maxZ - minZ : 0,
    surfaceArea,
    triCount: segments.length * sides * 2, // approximate: 2 triangles per quad face per segment
  }
}

/** Compute ground-truth diff between two MeshEnvelopes. Independent of SupportFingerprint. */
function computeGroundTruthDiff(a: MeshEnvelope, b: MeshEnvelope): { diff: number; fields: Record<string, { a: number; b: number; rel: number }> } {
  const rel = (x: number, y: number) => {
    const d = Math.max(Math.abs(x), Math.abs(y), 0.01)
    return Math.abs(x - y) / d
  }
  const fields: Record<string, { a: number; b: number; rel: number }> = {
    bboxW: { a: a.bboxW, b: b.bboxW, rel: rel(a.bboxW, b.bboxW) },
    bboxD: { a: a.bboxD, b: b.bboxD, rel: rel(a.bboxD, b.bboxD) },
    bboxH: { a: a.bboxH, b: b.bboxH, rel: rel(a.bboxH, b.bboxH) },
    surfaceArea: { a: a.surfaceArea, b: b.surfaceArea, rel: rel(a.surfaceArea, b.surfaceArea) },
    triCount: { a: a.triCount, b: b.triCount, rel: rel(a.triCount, b.triCount) },
  }
  const diff = Math.max(...Object.values(fields).map(f => f.rel))
  return { diff, fields }
}

// ── Mesh-measured equality test (T1/T2/T3 from prompt 2.1) ───────────────────
// Extracts geometric properties directly from real STL triangle vertices.
// Used to prove manual==auto equality and tier-switching correctness by measurement.

interface SupportMeshMeasurement {
  triCount: number
  surfaceArea: number
  bboxW: number; bboxD: number; bboxH: number
  // Measured radii: max radial distance from the vertical axis (centroid XY)
  // at the top 10% of Z range (tip region) and bottom 10% (base region)
  tipRadius: number   // max radial extent near the contact point
  baseRadius: number  // max radial extent near the build plate
  maxRadius: number   // overall max radial extent from axis
}

/** Measure a support mesh from real STL bytes. Extracts radii at tip and base by
 *  finding the centroid XY axis and measuring max radial distance at Z extremes. */
function measureSupportMesh(b64: string): SupportMeshMeasurement {
  const bin = atob(b64)
  const buf = new Uint8Array(bin.length)
  for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i)
  const dv = new DataView(buf.buffer)
  const triCount = dv.getUint32(80, true)

  // First pass: collect all vertices, compute bbox and centroid
  const verts: { x: number; y: number; z: number }[] = []
  let minX = Infinity, minY = Infinity, minZ = Infinity
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity
  let surfaceArea = 0
  let sumX = 0, sumY = 0
  for (let t = 0; t < triCount; t++) {
    const off = 84 + t * 50
    const tv: [number, number, number][] = []
    for (let v = 0; v < 3; v++) {
      const vOff = off + 12 + v * 12
      const x = dv.getFloat32(vOff, true), y = dv.getFloat32(vOff + 4, true), z = dv.getFloat32(vOff + 8, true)
      tv.push([x, y, z])
      verts.push({ x, y, z })
      minX = Math.min(minX, x); minY = Math.min(minY, y); minZ = Math.min(minZ, z)
      maxX = Math.max(maxX, x); maxY = Math.max(maxY, y); maxZ = Math.max(maxZ, z)
      sumX += x; sumY += y
    }
    const [ax, ay, az] = [tv[1][0] - tv[0][0], tv[1][1] - tv[0][1], tv[1][2] - tv[0][2]]
    const [bx, by, bz] = [tv[2][0] - tv[0][0], tv[2][1] - tv[0][1], tv[2][2] - tv[0][2]]
    const cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx
    surfaceArea += 0.5 * Math.sqrt(cx * cx + cy * cy + cz * cz)
  }

  if (triCount === 0) return { triCount: 0, surfaceArea: 0, bboxW: 0, bboxD: 0, bboxH: 0, tipRadius: 0, baseRadius: 0, maxRadius: 0 }

  // Centroid XY = axis of the support pillar
  const n = verts.length
  const cX = sumX / n, cY = sumY / n
  const zRange = maxZ - minZ
  const tipZThreshold = maxZ - zRange * 0.1  // top 10%
  const baseZThreshold = minZ + zRange * 0.1 // bottom 10%

  // Second pass: measure max radial distance from axis at tip, base, and overall
  let tipRadius = 0, baseRadius = 0, maxRadius = 0
  for (const v of verts) {
    const r = Math.sqrt((v.x - cX) * (v.x - cX) + (v.y - cY) * (v.y - cY))
    if (r > maxRadius) maxRadius = r
    if (v.z >= tipZThreshold && r > tipRadius) tipRadius = r
    if (v.z <= baseZThreshold && r > baseRadius) baseRadius = r
  }

  return {
    triCount, surfaceArea,
    bboxW: maxX - minX, bboxD: maxY - minY, bboxH: maxZ - minZ,
    tipRadius, baseRadius, maxRadius,
  }
}

// ── Manual support data (per-object) ──────────────────────────────────────────

interface SupportPoint {
  id: string
  x: number; y: number; z: number       // contact point on mesh surface (Y-up world space)
  nx: number; ny: number; nz: number    // surface normal at contact (Y-up world space)
  faceIndex?: number                    // face-anchored: triangle index in baked geometry
  baryU?: number; baryV?: number        // barycentric coords within triangle (u,v; w=1-u-v)
  tipDiameterMm: number
  shaftDiameterMm: number
  baseDiameterMm: number
  type: 'light' | 'medium' | 'heavy'
  // Per-support shape overrides (B6)
  touchShape?: string
  connectionShape?: string
  pillarShape?: string
  // Live engine result (from computeSingleSupport)
  engineStatus?: string        // 'routed' | 'bundled' | 'collision' | 'uncoverable' | 'error'
  engineMeshBase64?: string    // real mesh STL from engine
  engineMeshOffset?: { x: number; y: number; z: number }
  // Preview is provisional until full Generate confirms it.
  // After Generate, finalStatus holds the authoritative result.
  provisional: boolean
  finalStatus?: string         // set by Generate reconciliation; matches engineStatus values
  // Geometry fingerprint — captured from preview, compared against final
  previewFingerprint?: SupportFingerprint
  finalFingerprint?: SupportFingerprint
  geometryDelta?: number       // scalar divergence measure; >0.1 = flagged
  changeReasons?: string[]     // human-readable reasons for geometry divergence
  // Independent mesh-based ground truth (for completeness verification)
  previewEnvelope?: MeshEnvelope    // from actual preview STL triangles
  finalEnvelope?: MeshEnvelope      // reconstructed from final segment list
  groundTruthDiff?: number          // mesh-based diff, independent of fingerprint
}

/**
 * Ground-truth geometry fingerprint derived from ALL segment/mesh data.
 * Used as a quantitative divergence backstop — not a hand-picked field list.
 */
interface SupportFingerprint {
  meshFaceCount: number        // triangle count from actual mesh (preview) or 0 (final; merged mesh)
  totalSegmentCount: number    // ALL segments including pinhead, route, branch
  routeSegmentCount: number    // route-only segments (shaft, lowerTaper, base, branch)
  maxRadius: number            // widest radius across ALL segments (fixes G1: includes branch)
  minShaftRadius: number       // narrowest shaft/lowerTaper/branch radius (fixes G3: detects shrinkage)
  baseRadius: number           // base pedestal radius
  hasBranch: boolean           // any segment with part === 'branch'
  crossBraceCount: number      // number of cross-braces touching this support (fixes G2)
  baseZ: number                // Z of the lowest waypoint
  totalVolume: number          // summed frustum volume across all segments (ground-truth scalar)
  // Bounding box extents — catches lateral repositioning (volume-neutral changes)
  bboxW: number; bboxD: number; bboxH: number
}

/** Compute frustum volume for a segment: π/3 * h * (r1² + r1*r2 + r2²) */
function segmentVolume(s: { r1: number; r2: number; x1: number; y1: number; z1: number; x2: number; y2: number; z2: number }): number {
  const dx = s.x2 - s.x1, dy = s.y2 - s.y1, dz = s.z2 - s.z1
  const h = Math.sqrt(dx * dx + dy * dy + dz * dz)
  return Math.PI / 3 * h * (s.r1 * s.r1 + s.r1 * s.r2 + s.r2 * s.r2)
}

/**
 * Compute a scalar geometryDelta between two fingerprints.
 * Normalizes each field's relative change to [0,1] and takes the max.
 * >0.1 = geometry meaningfully differs from preview.
 */
function computeGeometryDelta(prev: SupportFingerprint, final: SupportFingerprint): number {
  const rel = (a: number, b: number) => {
    const denom = Math.max(Math.abs(a), Math.abs(b), 0.01)
    return Math.abs(a - b) / denom
  }
  return Math.max(
    rel(prev.maxRadius, final.maxRadius),
    rel(prev.minShaftRadius, final.minShaftRadius),
    rel(prev.baseRadius, final.baseRadius),
    rel(prev.baseZ, final.baseZ),
    rel(prev.totalVolume, final.totalVolume),
    rel(prev.routeSegmentCount, final.routeSegmentCount),
    rel(prev.crossBraceCount, final.crossBraceCount),
    rel(prev.bboxW, final.bboxW),
    rel(prev.bboxD, final.bboxD),
    rel(prev.bboxH, final.bboxH),
    prev.hasBranch !== final.hasBranch ? 1.0 : 0.0,
  )
}

/** Single boundary conversion: Three.js Y-up → backend Z-up. X stays, Y↔Z swap. */
function yUpToZUp(x: number, y: number, z: number): [number, number, number] {
  return [x, z, y]
}

interface PaintedRegion {
  id: string
  mode: 'enforcer' | 'blocker'
  // Triangles affected (face indices in the mesh)
  faceIndices: number[]
  // Approximate center for display
  cx: number; cy: number; cz: number
  radiusMm: number
}

interface ManualSupportData {
  points: SupportPoint[]
  paintedRegions: PaintedRegion[]
}

const EMPTY_SUPPORT_DATA: ManualSupportData = { points: [], paintedRegions: [] }

// ── Per-model state ───────────────────────────────────────────────────────────

interface HollowState {
  enabled: boolean
  wallThicknessMm: number
  appliedAt: number | null        // timestamp when last applied, null = never
  stale: boolean                  // true if model changed after hollowing was applied
}

const DEFAULT_HOLLOW: HollowState = { enabled: false, wallThicknessMm: 1.5, appliedAt: null, stale: false }

interface MeshValidation {
  status: 'pending' | 'valid' | 'warning' | 'error'
  triangleCount: number
  volumeMm3: number
  degenerateTriangles: number
  openEdges: number
  nonManifoldEdges: number
  flippedNormals: number
  warnings: string[]
  errors: string[]
  repaired: boolean
}

const PENDING_VALIDATION: MeshValidation = {
  status: 'pending', triangleCount: 0, volumeMm3: 0,
  degenerateTriangles: 0, openEdges: 0, nonManifoldEdges: 0, flippedNormals: 0,
  warnings: [], errors: [], repaired: false,
}

// ── Auto-generated preparation data ───────────────────────────────────────────

interface AutoSupportPoint {
  x: number; y: number; contactZ: number; baseZ: number
  tipDiameter: number; columnDiameter: number; baseDiameter: number
}

interface V2Stats {
  engine: string
  validSupports: number
  totalSupports: number
  volumeMl: number
  weightG: number
  costUsd: number
  elapsedMs: number
  meshFaces: number
  safetyFactor: number
  bucklingPass: number
  bucklingFail: number
  coverageOk: number
  coverageTotal: number
  collisions: number
}

interface PrepState {
  autoSupports: AutoSupportPoint[]
  advancedSupports: AdvancedSupportData[]
  crossBraces: CrossBraceData[]
  raft: { type: string; minX: number; minY: number; maxX: number; maxY: number; thicknessMm: number } | null
  skirt: { minX: number; minY: number; maxX: number; maxY: number; layers: number; distanceMm: number; widthMm: number } | null
  locked: boolean
  stale: boolean
  generatedAt: number | null
  v2Stats: V2Stats | null
  v2MeshBuffer: ArrayBuffer | null
  v2MeshOffset: { x: number; y: number; z: number } | null
  uncoverableManualIds: string[]
}

const EMPTY_PREP: PrepState = {
  autoSupports: [], advancedSupports: [], crossBraces: [],
  raft: null, skirt: null,
  locked: false, stale: false, generatedAt: null,
  v2Stats: null, v2MeshBuffer: null, v2MeshOffset: null, uncoverableManualIds: [],
}

interface ModelState extends ModelEntry {
  fileName: string
  size: { x: number; y: number; z: number } | null
  isOutOfBounds: boolean
  overrideSettings: ObjectSettings
  manualSupports: ManualSupportData
  hollow: HollowState
  meshValidation: MeshValidation
  prep: PrepState
}

let idCounter = 0
const mkId = () => `model-${++idCounter}`

// Module-level persistence
let _savedModels: ModelState[] = []
let _savedSelectedId: string | null = null
let _savedSpacing = 5
let _savedSupportEnabled = false
let _savedSupportType: 'normal' | 'tree' = 'normal'
let _savedSupportPlacement: 'buildplate' | 'everywhere' = 'buildplate'

// ── Undo stack ────────────────────────────────────────────────────────────────

interface UndoEntry { modelId: string; transform: ModelTransform }
let _undoStack: UndoEntry[] = []

const pushUndo = (modelId: string, transform: ModelTransform) => {
  _undoStack.push({ modelId, transform: { ...transform } })
  if (_undoStack.length > 50) _undoStack.shift()
}

// ── Component ─────────────────────────────────────────────────────────────────

export default function StlImport() {
  const viewerRef = useRef<StlViewerHandle>(null)

  const [models, setModels]           = useState<ModelState[]>(() => _savedModels)
  const [selectedId, setSelectedId]   = useState<string | null>(() => _savedSelectedId)
  const [isDragOver, setIsDragOver]   = useState(false)
  const [uniformScale, setUniformScale] = useState(true)
  const [spacing, setSpacing]         = useState(_savedSpacing)
  const [showSettings, setShowSettings] = useState(false)
  const [analyzeMode, setAnalyzeMode] = useState(false)

  // ── Keyboard shortcuts (Ctrl+Z undo, Ctrl+S save project) ──
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'z' && !e.shiftKey) {
        e.preventDefault()
        const entry = _undoStack.pop()
        if (entry) {
          setModels(prev => prev.map(m => m.id === entry.modelId ? { ...m, transform: entry.transform } : m))
        }
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault()
        // Trigger save handled by saveProject
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [])

  // ── Job-level support state (driven by active print profile, overridable) ──
  const [jobSupportEnabled, setJobSupportEnabled] = useState(() => _savedSupportEnabled)
  const [jobSupportType, setJobSupportType] = useState<'normal' | 'tree'>(() => _savedSupportType)
  const [jobSupportPlacement, setJobSupportPlacement] = useState<'buildplate' | 'everywhere'>(() => _savedSupportPlacement)

  // Support editing mode
  type SupportEditMode = 'none' | 'add' | 'delete' | 'paint-enforcer' | 'paint-blocker'
  const [supportEditMode, setSupportEditMode] = useState<SupportEditMode>('none')
  const [orientationCommitted, setOrientationCommitted] = useState(true) // false while bake is in progress
  const [supportBrushSize, setSupportBrushSize] = useState(3) // mm
  const [supportTipType, setSupportTipType] = useState<'light' | 'medium' | 'heavy'>('medium')
  const [selectedManualSupportId, setSelectedManualSupportId] = useState<string | null>(null)

  // Persist support state
  const setSupportEnabled = (v: boolean) => { setJobSupportEnabled(v); _savedSupportEnabled = v; setSliceStale(true) }
  const setSupportType = (v: 'normal' | 'tree') => { setJobSupportType(v); _savedSupportType = v; setSliceStale(true) }
  const setSupportPlacement = (v: 'buildplate' | 'everywhere') => { setJobSupportPlacement(v); _savedSupportPlacement = v; setSliceStale(true) }
  const [profilePanelOpen, setProfilePanelOpen] = useState(true)
  type RightTab = 'model' | 'supports' | 'print' | 'material'
  const [activeRightTab, setActiveRightTab] = useState<RightTab>('model')

  const [buildVolume, setBuildVolume] = useState<BuildVolume>({ width: 220, depth: 220, height: 250 })

  // Slice state
  const [selectedPrinterId, setSelectedPrinterId] = useState<string>('')
  const [selectedProfileId, setSelectedProfileId] = useState<string>('')
  const [slicing, setSlicing] = useState(false)
  interface LayerInfo {
    index: number; zHeightMm: number; layerThicknessMm: number
    type: string; exposureMs: number; liftDistanceMm: number
    liftSpeedMmPerMin: number; lightOffDelayMs: number
    contourCount: number; imageSizeBytes: number; isEmpty: boolean
  }
  const [sliceResult, setSliceResult] = useState<{
    jobId: string; layerCount: number; bottomLayerCount: number
    layerHeightMm: number; resolutionX: number; resolutionY: number
    totalHeightMm: number; estimatedPrintTimeMin: number; elapsedMs: number
  } | null>(null)
  const [layerData, setLayerData] = useState<LayerInfo[]>([])
  const [sliceError, setSliceError] = useState<string | null>(null)
  const [previewLayer, setPreviewLayer] = useState(0)
  const [sliceStale, setSliceStale] = useState(false)

  // Queries for printer and profile lists
  const { data: printers = [] } = useQuery({ queryKey: ['machine-profiles'], queryFn: machineProfilesApi.getAll })
  const { data: printProfiles = [] } = useQuery({ queryKey: ['resin-print-profiles'], queryFn: resinPrintProfilesApi.getAll })

  const resinPrinters = printers.filter(p => p.type === 'MSLA' || p.type === 'DLP')
  const activePrinter = resinPrinters.find(p => p.id === selectedPrinterId) ?? null

  // Update build volume when printer changes
  const prevPrinterIdRef = useRef(selectedPrinterId)
  if (selectedPrinterId !== prevPrinterIdRef.current) {
    prevPrinterIdRef.current = selectedPrinterId
    if (activePrinter) {
      setBuildVolume({ width: activePrinter.bedWidthMm, depth: activePrinter.bedDepthMm, height: activePrinter.bedHeightMm })
    }
  }

  const updateModels = (fn: (prev: ModelState[]) => ModelState[]) => {
    setModels(prev => { const next = fn(prev); _savedModels = next; return next })
    setSliceStale(true) // any model change invalidates slice
  }

  const selected = models.find(m => m.id === selectedId) ?? null

  // ── Overlap detection ───────────────────────────────────────────────────────

  const overlaps = useMemo(() => {
    const result = new Set<string>()
    for (let i = 0; i < models.length; i++) {
      const a = models[i]
      if (!a.size) continue
      const ax1 = a.transform.x - (a.size.x * a.transform.scaleX) / 2
      const ax2 = a.transform.x + (a.size.x * a.transform.scaleX) / 2
      const ay1 = a.transform.y - (a.size.y * a.transform.scaleY) / 2
      const ay2 = a.transform.y + (a.size.y * a.transform.scaleY) / 2
      for (let j = i + 1; j < models.length; j++) {
        const b = models[j]
        if (!b.size) continue
        const bx1 = b.transform.x - (b.size.x * b.transform.scaleX) / 2
        const bx2 = b.transform.x + (b.size.x * b.transform.scaleX) / 2
        const by1 = b.transform.y - (b.size.y * b.transform.scaleY) / 2
        const by2 = b.transform.y + (b.size.y * b.transform.scaleY) / 2
        if (ax1 < bx2 && ax2 > bx1 && ay1 < by2 && ay2 > by1) {
          result.add(a.id); result.add(b.id)
        }
      }
    }
    return result
  }, [models])

  const outOfBoundsIds = useMemo(() => {
    const result = new Set<string>()
    const hw = buildVolume.width / 2, hd = buildVolume.depth / 2
    for (const m of models) {
      if (!m.size) continue
      const sx = (m.size.x * m.transform.scaleX) / 2
      const sy = (m.size.y * m.transform.scaleY) / 2
      const sz = m.size.z * m.transform.scaleZ
      if (m.transform.x - sx < -hw || m.transform.x + sx > hw ||
          m.transform.y - sy < -hd || m.transform.y + sy > hd ||
          m.transform.z + sz > buildVolume.height)
        result.add(m.id)
    }
    return result
  }, [models, buildVolume])

  // ── Add file ────────────────────────────────────────────────────────────────

  const addFile = useCallback((file: File) => {
    const ext = file.name.toLowerCase()
    if (!ext.endsWith('.stl') && !ext.endsWith('.obj') && !ext.endsWith('.3mf')) return
    const url = URL.createObjectURL(file)
    const id = mkId()
    const entry: ModelState = {
      id, name: file.name, url, fileName: file.name,
      transform: { ...DEFAULT_TRANSFORM }, size: null,
      isOutOfBounds: false, overrideSettings: { ...DEFAULT_SETTINGS },
      manualSupports: { ...EMPTY_SUPPORT_DATA, points: [], paintedRegions: [] },
      hollow: { ...DEFAULT_HOLLOW },
      meshValidation: { ...PENDING_VALIDATION },
      prep: { ...EMPTY_PREP },
    }
    updateModels(prev => [...prev, entry])
    setSelectedId(id); _savedSelectedId = id

    // Async mesh validation
    const fd = new FormData()
    fd.append('stlFile', file, file.name)
    meshApi.validate(fd).then(v => {
      const status = v.errors.length > 0 ? 'error' : v.warnings.length > 0 ? 'warning' : 'valid'
      setModels(prev => {
        const next = prev.map(m => m.id === id ? { ...m, meshValidation: {
          status, triangleCount: v.triangleCount, volumeMm3: v.volumeMm3,
          degenerateTriangles: v.degenerateTriangles, openEdges: v.openEdges,
          nonManifoldEdges: v.nonManifoldEdges, flippedNormals: v.flippedNormals,
          warnings: v.warnings, errors: v.errors, repaired: false,
        } as MeshValidation } : m)
        _savedModels = next; return next
      })
    }).catch(() => {
      setModels(prev => {
        const next = prev.map(m => m.id === id ? { ...m, meshValidation: { ...PENDING_VALIDATION, status: 'error' as const, errors: ['Validation failed'] } } : m)
        _savedModels = next; return next
      })
    })
  }, [])

  // ── Duplicate selected ──────────────────────────────────────────────────────

  const duplicateModel = (id: string) => {
    const src = models.find(m => m.id === id)
    if (!src) return
    const newId = mkId()
    const copy: ModelState = {
      ...src, id: newId,
      name: src.fileName + ' (copy)',
      transform: { ...src.transform, x: src.transform.x + (src.size?.x ?? 20) + spacing },
      overrideSettings: { ...src.overrideSettings },
      manualSupports: { points: [...src.manualSupports.points], paintedRegions: [...src.manualSupports.paintedRegions] },
      hollow: { ...src.hollow },
      meshValidation: { ...src.meshValidation },
      prep: { ...EMPTY_PREP },
    }
    updateModels(prev => [...prev, copy])
    setSelectedId(newId); _savedSelectedId = newId
  }

  // ── Drop / input ────────────────────────────────────────────────────────────

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault(); setIsDragOver(false)
    Array.from(e.dataTransfer.files).forEach(f => addFile(f))
  }, [addFile])

  const handleFileInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    Array.from(e.target.files ?? []).forEach(f => addFile(f))
    e.target.value = ''
  }

  // ── Transform handling ──────────────────────────────────────────────────────

  const handleTransformChange = (modelId: string, t: ModelTransform) => {
    updateModels(prev => prev.map(m => {
      if (m.id !== modelId) return m
      // If locked and has prep, block the move (or mark stale if forced)
      const prepStale = m.prep.generatedAt ? true : m.prep.stale
      return {
        ...m, transform: t,
        hollow: m.hollow.appliedAt ? { ...m.hollow, stale: true } : m.hollow,
        prep: m.prep.generatedAt ? { ...m.prep, stale: prepStale } : m.prep,
        // Manual supports move with the part (relative)
      }
    }))
  }

  // ── Delete / undo ───────────────────────────────────────────────────────────

  const deleteModel = (id: string) => {
    updateModels(prev => prev.filter(m => m.id !== id))
    if (selectedId === id) {
      const remaining = models.filter(m => m.id !== id)
      const next = remaining.length > 0 ? remaining[0].id : null
      setSelectedId(next); _savedSelectedId = next
    }
  }

  const clearAll = () => {
    updateModels(() => [])
    setSelectedId(null); _savedSelectedId = null
  }

  // ── Project save/load ──
  const saveProject = () => {
    const project = {
      version: 1,
      name: 'VATSlicer Project',
      createdAt: new Date().toISOString(),
      models: models.map(m => ({
        fileName: m.name,
        positionX: m.transform.x, positionY: m.transform.y, positionZ: m.transform.z,
        rotationX: m.transform.rotX, rotationY: m.transform.rotY, rotationZ: m.transform.rotZ,
        scale: m.transform.scaleX,
        manualSupports: (m.manualSupports?.points ?? []).map((p: any) => ({
          id: p.id, x: p.x, y: p.y, z: p.z, nx: p.nx, ny: p.ny, nz: p.nz,
          shaftDiameterMm: p.shaftDiameterMm ?? 1.0, type: p.type ?? 'medium',
        })),
      })),
      printerId: selectedPrinterId,
      profileId: selectedProfileId,
      supportOptions: supportOptions,
    }
    const json = JSON.stringify(project, null, 2)
    const blob = new Blob([json], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `project_${Date.now()}.vatproj`
    a.click()
    URL.revokeObjectURL(url)
  }

  const loadProject = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return
    const reader = new FileReader()
    reader.onload = () => {
      try {
        const proj = JSON.parse(reader.result as string)
        if (proj.printerId) setSelectedPrinterId(proj.printerId)
        if (proj.profileId) setSelectedProfileId(proj.profileId)
        if (proj.supportOptions) setSupportOptions(proj.supportOptions)
        console.log('[Project] Loaded:', proj.name, proj.models?.length, 'models')
      } catch (err) { console.error('Failed to load project:', err) }
    }
    reader.readAsText(file)
    e.target.value = '' // allow re-selecting same file
  }

  const undo = () => {
    const entry = _undoStack.pop()
    if (!entry) return
    updateModels(prev => prev.map(m => m.id === entry.modelId ? { ...m, transform: entry.transform } : m))
  }

  // ── Slice action ───────────────────────────────────────────────────────────

  const handleSlice = async () => {
    if (models.length === 0) return
    if (!selectedPrinterId) { setSliceError('Select a printer first.'); return }
    if (!selectedProfileId) { setSliceError('Select a print profile first.'); return }

    // Get the first model's original file for upload
    // For multi-model: we'd merge, but for now slice the first model
    const firstModel = models[0]
    setSlicing(true); setSliceError(null); setSliceResult(null)

    try {
      // Fetch the STL blob from the object URL
      const resp = await fetch(firstModel.url)
      const blob = await resp.blob()

      const fd = new FormData()
      fd.append('stlFile', blob, firstModel.fileName)
      fd.append('printerId', selectedPrinterId)
      fd.append('printProfileId', selectedProfileId)
      fd.append('translateX', String(firstModel.transform.x))
      fd.append('translateY', String(firstModel.transform.y))
      fd.append('translateZ', String(firstModel.transform.z))
      fd.append('scale', String(firstModel.transform.scaleX))
      // Support settings (resolved: per-object override → job-level → print profile)
      const objSupport = firstModel.overrideSettings.supportEnabled
      fd.append('supportEnabled', String(objSupport ?? jobSupportEnabled))
      fd.append('supportType', firstModel.overrideSettings.supportType ?? jobSupportType)
      fd.append('supportPlacement', firstModel.overrideSettings.supportPlacement ?? jobSupportPlacement)
      // Hollowing
      const objHollow = firstModel.overrideSettings.hollowingEnabled
      const hollowEnabled = objHollow ?? firstModel.hollow.enabled
      fd.append('hollowEnabled', String(hollowEnabled))
      if (hollowEnabled) {
        fd.append('hollowWallThicknessMm', String(
          firstModel.overrideSettings.hollowWallThicknessMm ?? firstModel.hollow.wallThicknessMm
        ))
      }
      // Manual support data (points + painted regions)
      if (firstModel.manualSupports.points.length > 0 || firstModel.manualSupports.paintedRegions.length > 0) {
        fd.append('manualSupportData', JSON.stringify(firstModel.manualSupports))
      }

      const result = await resinSliceApi.slice(fd)
      setSliceResult(result)
      setPreviewLayer(0)
      setSliceStale(false)
      // Fetch structured layer data
      try {
        const ld = await resinSliceApi.getLayerData(result.jobId)
        setLayerData(ld.layers)
      } catch { setLayerData([]) }
    } catch (err: any) {
      const msg = err?.response?.data?.error || err?.response?.data || err?.message || 'Slicing failed'
      setSliceError(typeof msg === 'string' ? msg : JSON.stringify(msg))
    } finally {
      setSlicing(false)
    }
  }

  // ── Manual support editing ──────────────────────────────────────────────────

  const addSupportPoint = async (x: number, y: number, z: number, nx: number, ny: number, nz: number, faceIndex?: number, baryU?: number, baryV?: number) => {
    if (!selectedId || !selected) return
    const pr = SUPPORT_PRESETS[supportTipType] ?? SUPPORT_PRESETS['medium']
    const pointId = mkId()
    const point: SupportPoint = { id: pointId, x, y, z, nx, ny, nz, faceIndex, baryU, baryV, tipDiameterMm: pr.pin * 2, shaftDiameterMm: pr.pillar * 2, baseDiameterMm: pr.base * 2, type: supportTipType, provisional: true }

    // Add immediately with placeholder (marker sphere shows right away)
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, points: [...m.manualSupports.points, point] } } : m
    ))

    // Call backend for real engine geometry (same pipeline as auto)
    try {
      const meshData = (window as any).__stlViewerMeshMap?.get(selectedId)
      if (!meshData?.mesh) return
      const { STLExporter } = await import('three/examples/jsm/exporters/STLExporter.js')
      const THREE_mod = await import('three')

      // Export current baked geometry for the backend
      const backendGeo = meshData.mesh.geometry.clone()
      const pos = backendGeo.getAttribute('position')
      for (let i = 0; i < pos.count; i++) {
        const [bx, by, bz] = yUpToZUp(pos.getX(i), pos.getY(i), pos.getZ(i))
        pos.setXYZ(i, bx, by, bz)
      }
      for (let i = 0; i < pos.count; i += 3) {
        const x1=pos.getX(i+1),y1=pos.getY(i+1),z1=pos.getZ(i+1)
        const x2=pos.getX(i+2),y2=pos.getY(i+2),z2=pos.getZ(i+2)
        pos.setXYZ(i+1, x2, y2, z2)
        pos.setXYZ(i+2, x1, y1, z1)
      }
      pos.needsUpdate = true
      const tmpMesh = new THREE_mod.Mesh(backendGeo)
      const tmpScene = new THREE_mod.Scene()
      tmpScene.add(tmpMesh)
      const exporter = new STLExporter()
      const stlBinary = exporter.parse(tmpScene, { binary: true })
      const stlBlob = new Blob([stlBinary as any], { type: 'application/octet-stream' })
      tmpScene.remove(tmpMesh)
      backendGeo.dispose()

      // Convert tip to Z-up for backend
      const [tx, ty, tz] = yUpToZUp(x, y, z)
      const [tnx, tny, tnz] = yUpToZUp(nx, ny, nz)

      const fd = new FormData()
      fd.append('stlFile', stlBlob, selected.fileName)
      fd.append('tipX', String(tx))
      fd.append('tipY', String(ty))
      fd.append('tipZ', String(tz))
      fd.append('normalX', String(tnx))
      fd.append('normalY', String(tny))
      fd.append('normalZ', String(tnz))
      fd.append('pinRadius', String(pr.pin))
      fd.append('pillarRadius', String(pr.pillar))
      fd.append('baseRadius', String(pr.base))

      const result = await supportV2Api.computeSingle(fd)
      console.log('[SingleSupport]', result.status, `${result.computeMs}ms`, result.mesh.faces, 'faces')

      // ── T3 tier-switching log: record what tier was sent and measure the result ──
      if (result.mesh.stlBase64) {
        const m = measureSupportMesh(result.mesh.stlBase64)
        console.log(`[TierSwitch] placed ${supportTipType} support ${pointId}:`,
          `sent pin=${pr.pin} pillar=${pr.pillar} base=${pr.base}`,
          `| measured tipR=${m.tipRadius.toFixed(4)} maxR=${m.maxRadius.toFixed(3)} baseR=${m.baseRadius.toFixed(3)}`,
          `| tri=${m.triCount} area=${m.surfaceArea.toFixed(1)}`)
      }

      // Capture preview fingerprint from single-support result.
      // Single-support always produces: a straight pillar (no branches, no cross-braces),
      // solid geometry (no hollow/lattice). Estimate volume from a simple frustum.
      const pillarH = Math.abs(z - result.baseZ) // approximate height in Y-up
      const previewVolume = Math.PI / 3 * pillarH * (pr.pillar * pr.pillar + pr.pillar * pr.base + pr.base * pr.base)
      // Estimate preview bbox from a vertical pillar with base radius
      const prevBboxLateral = pr.base * 2 // base is the widest part
      const previewFingerprint: SupportFingerprint = {
        meshFaceCount: result.mesh.faces,
        totalSegmentCount: 5, // tip + neck + upperTaper + shaft/lowerTaper + base
        routeSegmentCount: 2, // shaft/lowerTaper + base
        maxRadius: Math.max(pr.pillar, pr.base),
        minShaftRadius: pr.pillar,
        baseRadius: pr.base,
        hasBranch: false,
        crossBraceCount: 0,
        baseZ: result.baseZ,
        totalVolume: previewVolume,
        bboxW: prevBboxLateral, bboxD: prevBboxLateral, bboxH: pillarH,
      }

      // Compute independent preview envelope from actual STL mesh (if available)
      const previewEnvelope = result.mesh.stlBase64
        ? envelopeFromStlBase64(result.mesh.stlBase64)
        : undefined

      // Update the point with engine result + preview fingerprint + preview envelope
      updateModels(prev => prev.map(m =>
        m.id === selectedId ? {
          ...m, manualSupports: {
            ...m.manualSupports,
            points: m.manualSupports.points.map(p => p.id === pointId ? {
              ...p,
              engineStatus: result.status,
              engineMeshBase64: result.mesh.stlBase64 ?? undefined,
              engineMeshOffset: result.meshOffset,
              previewFingerprint,
              previewEnvelope,
            } : p),
          }
        } : m
      ))
    } catch (err: any) {
      console.error('[SingleSupport] engine call failed:', err)
      // Mark the point as 'error' so it never stays silently pending
      updateModels(prev => prev.map(m =>
        m.id === selectedId ? {
          ...m, manualSupports: {
            ...m.manualSupports,
            points: m.manualSupports.points.map(p => p.id === pointId ? {
              ...p,
              engineStatus: 'error',
            } : p),
          }
        } : m
      ))
    }
  }

  const deleteSupportPoint = (pointId: string) => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, points: m.manualSupports.points.filter(p => p.id !== pointId) } } : m
    ))
  }

  const updateSupportPoint = (pointId: string, updates: Partial<SupportPoint>) => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, points: m.manualSupports.points.map(p => p.id === pointId ? { ...p, ...updates } : p) } } : m
    ))
  }

  // B4: Delete selected support via Delete/Backspace key
  const selectedSupportRef = useRef(selectedManualSupportId)
  selectedSupportRef.current = selectedManualSupportId
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.key === 'Delete' || e.key === 'Backspace') && selectedSupportRef.current) {
        if ((e.target as HTMLElement)?.tagName === 'INPUT' || (e.target as HTMLElement)?.tagName === 'SELECT') return
        e.preventDefault()
        deleteSupportPoint(selectedSupportRef.current)
        setSelectedManualSupportId(null)
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [])

  const retrySupportPoint = async (pointId: string) => {
    if (!selectedId || !selected) return
    const point = selected.manualSupports.points.find(p => p.id === pointId)
    if (!point) return
    // Reset to pending
    updateSupportPoint(pointId, { engineStatus: undefined, engineMeshBase64: undefined, engineMeshOffset: undefined })

    try {
      const meshData = (window as any).__stlViewerMeshMap?.get(selectedId)
      if (!meshData?.mesh) { updateSupportPoint(pointId, { engineStatus: 'error' }); return }
      const { STLExporter } = await import('three/examples/jsm/exporters/STLExporter.js')
      const THREE_mod = await import('three')
      const backendGeo = meshData.mesh.geometry.clone()
      const pos = backendGeo.getAttribute('position')
      for (let i = 0; i < pos.count; i++) {
        const [bx, by, bz] = yUpToZUp(pos.getX(i), pos.getY(i), pos.getZ(i))
        pos.setXYZ(i, bx, by, bz)
      }
      for (let i = 0; i < pos.count; i += 3) {
        const x1=pos.getX(i+1),y1=pos.getY(i+1),z1=pos.getZ(i+1)
        const x2=pos.getX(i+2),y2=pos.getY(i+2),z2=pos.getZ(i+2)
        pos.setXYZ(i+1, x2, y2, z2)
        pos.setXYZ(i+2, x1, y1, z1)
      }
      pos.needsUpdate = true
      const tmpMesh = new THREE_mod.Mesh(backendGeo)
      const tmpScene = new THREE_mod.Scene()
      tmpScene.add(tmpMesh)
      const exporter = new STLExporter()
      const stlBinary = exporter.parse(tmpScene, { binary: true })
      const stlBlob = new Blob([stlBinary as any], { type: 'application/octet-stream' })
      tmpScene.remove(tmpMesh)
      backendGeo.dispose()

      const [tx, ty, tz] = yUpToZUp(point.x, point.y, point.z)
      const [tnx, tny, tnz] = yUpToZUp(point.nx, point.ny, point.nz)
      const retryPreset = SUPPORT_PRESETS[point.type] ?? SUPPORT_PRESETS['medium']
      const fd = new FormData()
      fd.append('stlFile', stlBlob, selected.fileName)
      fd.append('tipX', String(tx)); fd.append('tipY', String(ty)); fd.append('tipZ', String(tz))
      fd.append('normalX', String(tnx)); fd.append('normalY', String(tny)); fd.append('normalZ', String(tnz))
      fd.append('pinRadius', String(retryPreset.pin))
      fd.append('pillarRadius', String(retryPreset.pillar))
      fd.append('baseRadius', String(retryPreset.base))

      const result = await supportV2Api.computeSingle(fd)
      updateSupportPoint(pointId, {
        engineStatus: result.status,
        engineMeshBase64: result.mesh.stlBase64 ?? undefined,
        engineMeshOffset: result.meshOffset,
      })
    } catch (err: any) {
      console.error('[SingleSupport retry] failed:', err)
      updateSupportPoint(pointId, { engineStatus: 'error' })
    }
  }

  const addPaintedRegion = (mode: 'enforcer' | 'blocker', cx: number, cy: number, cz: number) => {
    if (!selectedId) return
    const region: PaintedRegion = { id: mkId(), mode, faceIndices: [], cx, cy, cz, radiusMm: supportBrushSize }
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, paintedRegions: [...m.manualSupports.paintedRegions, region] } } : m
    ))
  }

  const deletePaintedRegion = (regionId: string) => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, paintedRegions: m.manualSupports.paintedRegions.filter(r => r.id !== regionId) } } : m
    ))
  }

  const clearPaintedRegions = (mode?: 'enforcer' | 'blocker') => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, paintedRegions: mode ? m.manualSupports.paintedRegions.filter(r => r.mode !== mode) : [] } } : m
    ))
  }

  const clearAllManualSupports = () => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { points: [], paintedRegions: [] } } : m
    ))
  }

  const selectedSupportData = selected?.manualSupports ?? EMPTY_SUPPORT_DATA
  const hasSupportEdits = selectedSupportData.points.length > 0 || selectedSupportData.paintedRegions.length > 0

  // ── Orientation commit — freeze pose into geometry, group becomes identity ──
  // Called synchronously from enterSupportEditMode BEFORE the mode changes,
  // so by the time React renders and StlViewer registers the click handler,
  // the geometry is already baked and the group is at identity. No race.

  const commitOrientation = useCallback((modelId: string) => {
    const meshData = (window as any).__stlViewerMeshMap?.get(modelId)
    if (!meshData?.mesh || !meshData?.group) return

    // Full 16-element identity check via Three.js Matrix4.equals (epsilon-exact)
    const identityMatrix = new meshData.group.matrix.constructor() // THREE.Matrix4
    const alreadyIdentity = meshData.group.matrixWorld.equals(identityMatrix)

    if (!alreadyIdentity) {
      setOrientationCommitted(false)

      const displayGeo = meshData.mesh.geometry.clone()
      displayGeo.applyMatrix4(meshData.group.matrixWorld)
      // Drop to bed (Y-up: lowest Y = 0)
      displayGeo.computeBoundingBox()
      const minY = displayGeo.boundingBox!.min.y
      const p = displayGeo.getAttribute('position')
      for (let i = 0; i < p.count; i++) p.setY(i, p.getY(i) - minY)
      p.needsUpdate = true

      // ── Rebuild ALL bounds on baked geometry ──
      displayGeo.computeVertexNormals()
      displayGeo.computeBoundingBox()
      displayGeo.computeBoundingSphere()
      // Build BVH for accelerated raycasting (three-mesh-bvh is a required dependency).
      // Without this, every raycast is O(n) brute-force over all triangles.
      ;(displayGeo as any).computeBoundsTree()

      // Assign baked geometry to the SAME mesh object the raycaster collects
      const oldGeo = meshData.mesh.geometry
      ;(oldGeo as any).disposeBoundsTree?.()
      meshData.mesh.geometry = displayGeo
      oldGeo.dispose()

      meshData.group.position.set(0, 0, 0)
      meshData.group.rotation.set(0, 0, 0)
      meshData.group.scale.set(1, 1, 1)
      meshData.group.updateMatrixWorld(true)
      const newSize = displayGeo.boundingBox!.getSize(meshData.naturalSize.clone())
      meshData.naturalSize.copy(newSize)
      meshData.currentTransform = { ...DEFAULT_TRANSFORM }
      updateModels(prev => prev.map(m => m.id === modelId ? { ...m, transform: { ...DEFAULT_TRANSFORM } } : m))
    }

    // ── Ensure bounds are fresh on the CURRENT geometry (both paths) ──
    const geo = meshData.mesh.geometry
    if (!geo.boundingBox) geo.computeBoundingBox()
    if (!geo.boundingSphere) geo.computeBoundingSphere()

    setOrientationCommitted(true)

    // ── T1: Reference identity ──
    console.log('[Commit] T1 ref:', meshData.mesh.geometry === geo ? 'PASS' : 'FAIL',
      '| uuid:', geo.uuid)

    // ── T2: Bounds freshness ──
    const pos = geo.getAttribute('position')
    const bt = (geo as any).boundsTree
    console.log('[Commit] T2 bounds:',
      'bSphere:', geo.boundingSphere ? `r=${geo.boundingSphere.radius.toFixed(2)}` : 'NULL',
      '| bTree:', bt ? 'present' : 'none',
      '| vtx:', pos?.count,
      '| baked:', !alreadyIdentity)

    // ── T3: Headless raycast hit test ──
    // Pick triangle 0, build ray from 2mm along outward normal, cast back into mesh
    const T = (window as any).__THREE
    if (pos && pos.count >= 3 && T) {
      const v0 = new T.Vector3(pos.getX(0), pos.getY(0), pos.getZ(0))
      const v1 = new T.Vector3(pos.getX(1), pos.getY(1), pos.getZ(1))
      const v2 = new T.Vector3(pos.getX(2), pos.getY(2), pos.getZ(2))
      const center = v0.clone().add(v1).add(v2).multiplyScalar(1/3)
      const edge1 = v1.clone().sub(v0)
      const edge2 = v2.clone().sub(v0)
      const normal = new T.Vector3().crossVectors(edge1, edge2).normalize()
      const origin = center.clone().add(normal.clone().multiplyScalar(2))
      const dir = normal.clone().negate()
      const rc = new T.Raycaster(origin, dir, 0, 5)
      const hits = rc.intersectObject(meshData.mesh, false)
      const passT3 = hits.length >= 1 && hits[0].point.distanceTo(center) < 0.1
      console.log('[Commit] T3 headless:', passT3 ? 'PASS' : 'FAIL',
        '| hits:', hits.length,
        '| dist:', hits.length > 0 ? hits[0].point.distanceTo(center).toFixed(4) : 'N/A',
        '| face:', hits.length > 0 ? hits[0].faceIndex : 'N/A')
    }
  }, [updateModels])

  const enterSupportEditMode = useCallback((mode: SupportEditMode) => {
    if (mode !== 'none' && selectedId) {
      commitOrientation(selectedId) // synchronous — runs NOW, before mode changes
    }
    setSupportEditMode(mode)
  }, [selectedId, commitOrientation])

  // ── Hollowing controls ────────────────────────────────────────────────────

  const setHollowEnabled = (id: string, enabled: boolean) => {
    updateModels(prev => prev.map(m =>
      m.id === id ? { ...m, hollow: { ...m.hollow, enabled, appliedAt: enabled ? Date.now() : null, stale: false } } : m
    ))
  }

  const setHollowWallThickness = (id: string, wallThicknessMm: number) => {
    updateModels(prev => prev.map(m =>
      m.id === id ? { ...m, hollow: { ...m.hollow, wallThicknessMm, stale: m.hollow.appliedAt !== null } } : m
    ))
  }

  const applyHollow = (id: string) => {
    updateModels(prev => prev.map(m =>
      m.id === id ? { ...m, hollow: { ...m.hollow, appliedAt: Date.now(), stale: false } } : m
    ))
    setSliceStale(true)
  }

  const selectedHollow = selected?.hollow ?? DEFAULT_HOLLOW
  const selectedPrep = selected?.prep ?? EMPTY_PREP

  // Memoize arrays passed to StlViewer to prevent new references every render
  const memoSupportPoints = useMemo(() => [
    ...selectedSupportData.points,
    ...(selectedPrep.advancedSupports.length > 0
      ? selectedPrep.advancedSupports.map(s => ({
          id: s.id, x: s.contactX, y: s.contactY, z: s.contactZ,
          type: (s.preset.name === 'Light' ? 'light' : s.preset.name === 'Heavy' ? 'heavy' : 'medium') as 'light' | 'medium' | 'heavy',
          segments: s.segments,
        }))
      : selectedPrep.autoSupports.map((s, i) => ({
          id: `auto-${i}`, x: s.x, y: s.y, z: s.contactZ,
          type: 'medium' as const,
        }))
    ),
  ], [selectedSupportData.points, selectedPrep.advancedSupports, selectedPrep.autoSupports])

  const memoAllSupportMeshes = useMemo(() =>
    models.filter(m => m.prep.v2MeshBuffer).map(m => ({
      modelId: m.id,
      buffer: m.prep.v2MeshBuffer!,
      offset: m.prep.v2MeshOffset,
    })),
  [models])

  const memoManualMarkers = useMemo(() =>
    selectedSupportData.points.map(p => ({
      id: p.id, x: p.x, y: p.y, z: p.z, shaftDiameter: p.shaftDiameterMm,
      uncoverable: (selectedPrep.uncoverableManualIds ?? []).includes(p.id),
      engineStatus: p.finalStatus ?? p.engineStatus,
      engineMeshBase64: p.engineMeshBase64,
      engineMeshOffset: p.engineMeshOffset,
      provisional: p.provisional,
    })),
  [selectedSupportData.points, selectedPrep.uncoverableManualIds])

  // ── Auto-support generation ───────────────────────────────────────────────

  const [supportOptions, setSupportOptions] = useState<SupportOptionsConfig>(DEFAULT_SUPPORT_OPTIONS)

  // Derive the legacy autoSupportConfig from the visual picker state
  // This ensures the FormData payload stays identical to what the backend expects.
  const autoSupportConfig = useMemo(() => {
    const densityMap = { light: 0.3, medium: 0.5, heavy: 0.8 } as const
    const presetMap = { light: 'light', medium: 'medium', heavy: 'heavy' } as const
    return {
      overhangAngle: 45,
      density: densityMap[supportOptions.density],
      tipDiameter: supportOptions.density === 'light' ? 0.2 : supportOptions.density === 'heavy' ? 0.8 : 0.4,
      supportType: presetMap[supportOptions.density],
      crossBracing: supportOptions.reinforcementMode !== 'none',
      crossBraceDistMm: 50,
      raftEnabled: supportOptions.raftMode !== 'none',
      raftType: supportOptions.raftMode === 'fullHex' ? 'hex' : supportOptions.raftMode === 'fullGrid' ? 'grid' : 'grid',
      skirtEnabled: false,
      skirtLayers: 3,
      skirtDistance: 2.0,
      supportExposurePct: 100,
      treeSupports: supportOptions.supportType === 'tree',
      hollowSupports: true,
      hollowMinHeight: 20,
      hollowWallThickness: 0.6,
      latticePattern: 'grid' as string,
      miniRafts: supportOptions.raftMode === 'mini',
      raftMargin: 1.5,
      raftThickness: 0.3,
      materialPreset: 'standard' as string,
    }
  }, [supportOptions])

  // Legacy setter for any code that still calls setAutoSupportConfig directly
  const setAutoSupportConfig = useCallback((fn: (prev: typeof autoSupportConfig) => typeof autoSupportConfig) => {
    // Map back to supportOptions where possible; for fields only in legacy config, just ignore
    const updated = fn(autoSupportConfig)
    setSupportOptions(prev => ({
      ...prev,
      density: updated.density <= 0.35 ? 'light' : updated.density >= 0.65 ? 'heavy' : 'medium',
    }))
  }, [autoSupportConfig])
  const [generating, setGenerating] = useState(false)
  const [generatingProgress, setGeneratingProgress] = useState('')

  /** Generate supports for a SPECIFIC model by ID. Core logic — used by both single and batch. */
  const generateSupportsForModel = async (modelId: string) => {
    const targetModel = models.find(m => m.id === modelId)
    if (!targetModel) throw new Error(`Model ${modelId} not found`)
    commitOrientation(modelId)
    const meshData = (window as any).__stlViewerMeshMap?.get(modelId)
    let stlBlob: Blob

    try {
      if (meshData?.mesh && meshData?.group) {
        const THREE_mod = await import('three')
        const { STLExporter } = await import('three/examples/jsm/exporters/STLExporter.js')

        // Geometry is already baked (Y-up world, dropped to bed, group at identity).
        // Clone for backend — apply the ONE boundary conversion via yUpToZUp.
        const backendGeo = meshData.mesh.geometry.clone()
        const pos = backendGeo.getAttribute('position')
        for (let i = 0; i < pos.count; i++) {
          const [bx, by, bz] = yUpToZUp(pos.getX(i), pos.getY(i), pos.getZ(i))
          pos.setXYZ(i, bx, by, bz)
        }
        // Y↔Z swap is a reflection → always reverse winding once
        for (let i = 0; i < pos.count; i += 3) {
          const x1=pos.getX(i+1),y1=pos.getY(i+1),z1=pos.getZ(i+1)
          const x2=pos.getX(i+2),y2=pos.getY(i+2),z2=pos.getZ(i+2)
          pos.setXYZ(i+1, x2, y2, z2)
          pos.setXYZ(i+2, x1, y1, z1)
        }
        pos.needsUpdate = true

        // Export to binary STL
        const tempMesh = new THREE_mod.Mesh(backendGeo)
        const tempScene = new THREE_mod.Scene()
        tempScene.add(tempMesh)
        const exporter = new STLExporter()
        const stlBinary = exporter.parse(tempScene, { binary: true })
        stlBlob = new Blob([stlBinary as any], { type: 'application/octet-stream' })
        tempScene.remove(tempMesh)
        backendGeo.dispose()
      } else {
        // Fallback: raw file (no transforms)
        const resp = await fetch(targetModel.url)
        stlBlob = await resp.blob()
      }

      const fd = new FormData()
      fd.append('stlFile', stlBlob, targetModel.fileName)
      // NO rotation/scale params — the bake handles everything
      fd.append('orientation', activePrinter?.orientation ?? 'BottomUp')
      if (selectedPrinterId) fd.append('printerId', selectedPrinterId)
      fd.append('overhangAngleDeg', String(autoSupportConfig.overhangAngle))
      fd.append('density', String(autoSupportConfig.density))
      fd.append('tipDiameterMm', String(autoSupportConfig.tipDiameter))
      fd.append('supportType', autoSupportConfig.supportType)
      fd.append('placement', jobSupportPlacement)
      fd.append('enableInterconnections', String(autoSupportConfig.crossBracing))
      fd.append('interconnectDistMm', String(autoSupportConfig.crossBraceDistMm))
      fd.append('raftEnabled', String(autoSupportConfig.raftEnabled))
      fd.append('raftType', autoSupportConfig.raftType)
      fd.append('skirtEnabled', String(autoSupportConfig.skirtEnabled))
      fd.append('skirtLayers', String(autoSupportConfig.skirtLayers))
      fd.append('skirtDistanceMm', String(autoSupportConfig.skirtDistance))

      // Use unified preset table for V2 engine parameters
      const preset = SUPPORT_PRESETS[autoSupportConfig.supportType] ?? SUPPORT_PRESETS['medium']
      fd.append('pinRadius', String(preset.pin))
      fd.append('backRadius', String(preset.back))
      fd.append('pillarRadius', String(preset.pillar))
      fd.append('baseRadius', String(preset.base))

      // V2 advanced features
      fd.append('enableTreeSupports', String(autoSupportConfig.treeSupports))
      fd.append('enableHollowSupports', String(autoSupportConfig.hollowSupports))
      fd.append('hollowMinHeightMm', String(autoSupportConfig.hollowMinHeight))
      fd.append('hollowWallThicknessMm', String(autoSupportConfig.hollowWallThickness))
      fd.append('baseLatticePattern', autoSupportConfig.latticePattern)
      fd.append('enableMiniRafts', String(autoSupportConfig.miniRafts))
      fd.append('raftMarginMm', String(autoSupportConfig.raftMargin))
      fd.append('raftThicknessMm', String(autoSupportConfig.raftThickness))
      fd.append('materialPreset', autoSupportConfig.materialPreset)

      // New V2 features from visual picker
      fd.append('enableForking', String(supportOptions.supportType === 'forked'))
      fd.append('maxTipsPerFork', String(supportOptions.forkTips))
      fd.append('enableLineContact', String(supportOptions.contactStyle === 'line'))
      fd.append('enableFaceContact', String(supportOptions.contactStyle === 'face'))
      fd.append('reinforcementMode', supportOptions.reinforcementMode === 'none' ? 'None'
        : supportOptions.reinforcementMode === 'pairwise' ? 'Pairwise'
        : supportOptions.reinforcementMode === 'triangular' ? 'Triangular' : 'Global')
      const raftModeMap: Record<string, string> = {
        'none': 'None', 'mini': 'MiniRafts', 'skate': 'Skate',
        'fullGrid': 'CrossGrid', 'fullHex': 'Hex',
      }
      fd.append('raftMode', raftModeMap[supportOptions.raftMode] ?? 'MiniRafts')
      fd.append('fullPlateRaftPattern', supportOptions.raftMode === 'fullHex' ? 'Honeycomb' : 'Grid')
      // Raft advanced params
      fd.append('raftAreaRatioPct', String(supportOptions.raftAreaRatioPct))
      fd.append('raftThicknessMm', String(supportOptions.raftThicknessMm))
      fd.append('fullPlateRaftHeightMm', String(supportOptions.raftHeightMm))
      fd.append('raftSlopeDeg', String(supportOptions.raftSlopeDeg))
      fd.append('gridCellMm', String(supportOptions.gridCellMm))
      fd.append('gridStrutMm', String(supportOptions.gridStrutMm))
      fd.append('enableDrainageAwareSupports', String(supportOptions.drainageAware))
      fd.append('enableForceDrivenPlacement', String(supportOptions.forceDriven))

      // Advanced Settings (ChiTuBox-style manual sizing)
      const adv = supportOptions.advanced
      fd.append('sizingMode', adv.sizingMode)
      fd.append('supportPreset', adv.preset)
      fd.append('topTouchShape', adv.topTouchShape)
      fd.append('topConnectionShape', adv.topConnectionShape)
      fd.append('middlePillarShape', adv.middlePillarShape)
      if (adv.sizingMode === 'manual') {
        fd.append('topContactDepthMm', String(adv.topContactDepthMm))
        fd.append('topTipUpperDiaMm', String(adv.topTipUpperDiaMm))
        fd.append('topTipLowerDiaMm', String(adv.topTipLowerDiaMm))
        fd.append('topTipAngleDeg', String(adv.topTipAngleDeg))
        fd.append('topConnectionLengthMm', String(adv.topConnectionLengthMm))
        fd.append('middlePillarDiaMm', String(adv.middlePillarDiaMm))
        fd.append('bottomBaseDiaMm', String(adv.bottomBaseDiaMm))
        fd.append('bottomBaseThicknessMm', String(adv.bottomBaseThicknessMm))
        fd.append('raftCustomThicknessMm', String(adv.raftThicknessMm))
      }

      // Send manual support contacts — same yUpToZUp conversion as the mesh
      const manualPts = targetModel.manualSupports?.points ?? []
      if (manualPts.length > 0) {
        fd.append('manualContacts', JSON.stringify(manualPts.map(p => {
          const [px, py, pz] = yUpToZUp(p.x, p.y, p.z)
          const [nx, ny, nz] = yUpToZUp(p.nx, p.ny, p.nz)
          return { id: p.id, x: px, y: py, z: pz, nx, ny, nz,
            tipDiameterMm: p.tipDiameterMm, shaftDiameterMm: p.shaftDiameterMm, baseDiameterMm: p.baseDiameterMm,
            touchShape: p.touchShape, connectionShape: p.connectionShape, pillarShape: p.pillarShape }
        })))
      }

      // V2 engine only — no legacy fallback
      const v2Result = await supportV2Api.generate(fd)
      const v2Stats: V2Stats = {
        engine: v2Result.engine,
        validSupports: v2Result.validSupports,
        totalSupports: v2Result.totalSupports,
        volumeMl: v2Result.totalVolumeMl,
        weightG: v2Result.totalWeightG,
        costUsd: v2Result.estimatedCostUsd,
        elapsedMs: v2Result.elapsedMs,
        meshFaces: v2Result.mesh.faces,
        safetyFactor: v2Result.validation.structural.minSafetyFactor,
        bucklingPass: v2Result.validation.structural.passedBuckling,
        bucklingFail: v2Result.validation.structural.failedBuckling,
        coverageOk: v2Result.validation.structural.overhangRegionsCovered,
        coverageTotal: v2Result.validation.structural.overhangRegionsCovered + v2Result.validation.structural.overhangRegionsUncovered,
        collisions: v2Result.validation.collision.collidingSupports,
      }
      console.log(`[V2] ${v2Result.validSupports} supports, SF=${v2Result.validation.structural.minSafetyFactor.toFixed(1)}, ${v2Result.elapsedMs}ms`)

      // Log stats to console (routine — not user-actionable)
      if (v2Result.droppedByCapCount > 0)
        console.log(`[V2] ${v2Result.droppedByCapCount} overhang points dropped (500-point cap)`)
      if (v2Result.rejectedCollisions > 0)
        console.log(`[V2] ${v2Result.rejectedCollisions} auto-support(s) removed due to collision (normal)`)
      // Only alert for manual support failures — those are user-actionable
      const manualFailed = (v2Result.uncoverableManualIds ?? []).length
      if (manualFailed > 0)
        alert(`${manualFailed} manual support(s) could not be routed to the build plate. Try placing them on downward-facing surfaces with a clear path to the bed.`)

      // Decode inline STL mesh from V2 response (no second HTTP request)
      // Get V2 mesh: inline base64 for small meshes, separate fetch for large
      let meshBuffer: ArrayBuffer | null = null
      if (v2Result.mesh?.stlBase64) {
        const binary = atob(v2Result.mesh.stlBase64)
        const bytes = new Uint8Array(binary.length)
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
        meshBuffer = bytes.buffer
      } else if (v2Result.mesh?.faces > 0) {
        // Large mesh — fetch via separate endpoint
        try {
          meshBuffer = await supportV2Api.getMeshBuffer(fd)
        } catch { /* mesh fetch optional */ }
      }

      // ── Reconcile manual support previews against full pipeline result ──
      // Ground-truth comparison: build per-support fingerprint from ALL segment
      // data + cross-braces, compute a scalar geometryDelta, then derive named
      // reasons. The delta is the completeness backstop — any unmeasured divergence
      // that exceeds tolerance triggers a catch-all flag.
      const uncovSet = new Set(v2Result.uncoverableManualIds ?? [])
      const DELTA_TOLERANCE = 0.10

      // Build lookups
      const finalSupportMap = new Map<string, typeof v2Result.supports[0]>()
      for (const s of (v2Result.supports ?? [])) finalSupportMap.set(s.id, s)

      // Count cross-braces per support ID (fixes G2)
      const crossBraceCounts = new Map<string, number>()
      for (const b of (v2Result.crossBraces ?? [])) {
        crossBraceCounts.set(b.supportA, (crossBraceCounts.get(b.supportA) ?? 0) + 1)
        crossBraceCounts.set(b.supportB, (crossBraceCounts.get(b.supportB) ?? 0) + 1)
      }

      const totalFinalSupports = v2Result.supports?.length ?? 0

      const reconciledPoints = (targetModel.manualSupports?.points ?? []).map(p => {
        const finalStatus = uncovSet.has(p.id) ? 'uncoverable' : 'routed'
        const changeReasons: string[] = []

        // Status divergence
        if (p.engineStatus && p.engineStatus !== finalStatus
            && p.engineStatus !== 'pending' && p.engineStatus !== 'error')
          changeReasons.push(`status: ${p.engineStatus} → ${finalStatus}`)

        // Build final fingerprint from ALL segment data + cross-braces
        const finalSupport = finalSupportMap.get(p.id)
        let finalFingerprint: SupportFingerprint | undefined
        let delta = 0

        if (finalSupport) {
          const segs = finalSupport.segments ?? []
          const routeSegs = segs.filter((s: any) => s.part !== 'tip' && s.part !== 'neck' && s.part !== 'upperTaper')
          // maxRadius across ALL segments — not just shaft (fixes G1: includes branch)
          const allRadii = segs.flatMap((s: any) => [s.r1 as number, s.r2 as number])
          const maxR = allRadii.length > 0 ? Math.max(...allRadii) : 0
          // minShaftRadius across shaft/lowerTaper/branch — detects shrinkage (fixes G3)
          const loadSegs = segs.filter((s: any) => s.part === 'shaft' || s.part === 'lowerTaper' || s.part === 'branch')
          const loadRadii = loadSegs.flatMap((s: any) => [s.r1 as number, s.r2 as number]).filter((r: number) => r > 0.01)
          const minShaftR = loadRadii.length > 0 ? Math.min(...loadRadii) : 0
          const baseSeg = segs.find((s: any) => s.part === 'base')
          const hasBranch = segs.some((s: any) => s.part === 'branch')
          const braceCount = crossBraceCounts.get(p.id) ?? 0
          const totalVol = segs.reduce((sum: number, s: any) => sum + segmentVolume(s), 0)

          // Compute bbox from segment endpoints + radii
          let fMinX = Infinity, fMinY = Infinity, fMinZ = Infinity
          let fMaxX = -Infinity, fMaxY = -Infinity, fMaxZ = -Infinity
          for (const s of segs) {
            fMinX = Math.min(fMinX, s.x1 - s.r1, s.x2 - s.r2); fMaxX = Math.max(fMaxX, s.x1 + s.r1, s.x2 + s.r2)
            fMinY = Math.min(fMinY, s.y1 - s.r1, s.y2 - s.r2); fMaxY = Math.max(fMaxY, s.y1 + s.r1, s.y2 + s.r2)
            fMinZ = Math.min(fMinZ, s.z1 - s.r1, s.z2 - s.r2); fMaxZ = Math.max(fMaxZ, s.z1 + s.r1, s.z2 + s.r2)
          }
          finalFingerprint = {
            meshFaceCount: 0,
            totalSegmentCount: segs.length,
            routeSegmentCount: routeSegs.length,
            maxRadius: maxR,
            minShaftRadius: minShaftR,
            baseRadius: baseSeg ? Math.max(baseSeg.r1, baseSeg.r2) : 0,
            hasBranch,
            crossBraceCount: braceCount,
            baseZ: finalSupport.baseZ,
            totalVolume: totalVol,
            bboxW: segs.length > 0 ? fMaxX - fMinX : 0,
            bboxD: segs.length > 0 ? fMaxY - fMinY : 0,
            bboxH: segs.length > 0 ? fMaxZ - fMinZ : 0,
          }

          // ── Ground-truth geometryDelta ──
          const prev = p.previewFingerprint
          if (prev) {
            delta = computeGeometryDelta(prev, finalFingerprint)

            // ── Named reasons (human-readable labels for known divergence types) ──
            if (finalFingerprint.hasBranch && !prev.hasBranch)
              changeReasons.push('merged into trunk')
            // Bidirectional radius checks (fixes G3) — both maxRadius and minShaftRadius
            const maxRatio = prev.maxRadius > 0.01 ? finalFingerprint.maxRadius / prev.maxRadius : 1
            if (maxRatio > 1.15)
              changeReasons.push(`widened (${prev.maxRadius.toFixed(2)} → ${finalFingerprint.maxRadius.toFixed(2)}mm)`)
            else if (maxRatio < 0.87)
              changeReasons.push(`narrowed (${prev.maxRadius.toFixed(2)} → ${finalFingerprint.maxRadius.toFixed(2)}mm)`)
            // Shaft-specific shrinkage (load-bearing radius decreased)
            const shaftMinRatio = prev.minShaftRadius > 0.01 ? finalFingerprint.minShaftRadius / prev.minShaftRadius : 1
            if (shaftMinRatio < 0.87 && maxRatio >= 0.87) // shaft shrank but max didn't flag it
              changeReasons.push(`shaft narrowed (${prev.minShaftRadius.toFixed(2)} → ${finalFingerprint.minShaftRadius.toFixed(2)}mm)`)
            else if (shaftMinRatio > 1.15 && maxRatio <= 1.15) // shaft grew but max didn't flag it
              changeReasons.push(`shaft widened (${prev.minShaftRadius.toFixed(2)} → ${finalFingerprint.minShaftRadius.toFixed(2)}mm)`)
            const baseRatio = prev.baseRadius > 0.01 ? finalFingerprint.baseRadius / prev.baseRadius : 1
            if (baseRatio > 1.15)
              changeReasons.push(`base enlarged (${prev.baseRadius.toFixed(1)} → ${finalFingerprint.baseRadius.toFixed(1)}mm)`)
            else if (baseRatio < 0.87)
              changeReasons.push(`base reduced (${prev.baseRadius.toFixed(1)} → ${finalFingerprint.baseRadius.toFixed(1)}mm)`)
            if (Math.abs(finalFingerprint.baseZ - prev.baseZ) > 0.5)
              changeReasons.push('rerouted')
            else if (finalFingerprint.routeSegmentCount > prev.routeSegmentCount + 2)
              changeReasons.push('rerouted')
            // Bbox shift: catches lateral repositioning (base nudge, volume-neutral reroute)
            const bboxWRatio = prev.bboxW > 0.01 ? finalFingerprint.bboxW / prev.bboxW : 1
            const bboxDRatio = prev.bboxD > 0.01 ? finalFingerprint.bboxD / prev.bboxD : 1
            if ((Math.abs(bboxWRatio - 1) > 0.15 || Math.abs(bboxDRatio - 1) > 0.15)
                && !changeReasons.includes('rerouted'))
              changeReasons.push('repositioned')
            // Cross-braces added (fixes G2)
            if (finalFingerprint.crossBraceCount > prev.crossBraceCount)
              changeReasons.push(`cross-braced (${finalFingerprint.crossBraceCount} brace${finalFingerprint.crossBraceCount > 1 ? 's' : ''})`)
            // Hollow/lattice (mesh-gen decisions not in segment data)
            const pillarHeight = Math.abs(finalSupport.contactZ - finalSupport.baseZ)
            if (autoSupportConfig.hollowSupports && totalFinalSupports < 150
                && pillarHeight > autoSupportConfig.hollowMinHeight)
              changeReasons.push('hollowed')
            if (autoSupportConfig.latticePattern !== 'solid' && totalFinalSupports < 100)
              changeReasons.push('lattice base')

            // ── Completeness backstop: if delta exceeds tolerance but no named reason
            // fired, flag with honest catch-all + log the raw diff for future labeling ──
            if (delta > DELTA_TOLERANCE && changeReasons.length === 0) {
              changeReasons.push('changed (geometry differs)')
              console.warn(`[Reconcile] support ${p.id}: geometryDelta=${delta.toFixed(3)} but no named reason.`,
                'prev:', JSON.stringify(prev), 'final:', JSON.stringify(finalFingerprint))
            }
          }
        } else if (finalStatus === 'routed') {
          changeReasons.push('geometry unavailable')
        }

        // ── Independent ground-truth diff (REAL MESH, not segments) ──
        // Preview side: actual STL triangles from single-support result.
        // Final side: actual generated mesh from ManualSupportMeshes (includes hollow, lattice, tessellation).
        // These share NO data source with SupportFingerprint (which reads segment fields).
        let gtDiff = 0
        let finalEnvelope: MeshEnvelope | undefined
        let gtSource: 'real-mesh' | 'segment-fallback' | 'none' = 'none'
        const finalMeshB64 = (v2Result.manualSupportMeshes ?? {})[p.id]

        if (p.previewEnvelope && finalMeshB64) {
          // PRIMARY PATH: real generated mesh (independent of fingerprint)
          gtSource = 'real-mesh'
          finalEnvelope = envelopeFromStlBase64(finalMeshB64)
          const gt = computeGroundTruthDiff(p.previewEnvelope, finalEnvelope)
          gtDiff = gt.diff

          // ── Fault-injection measurement log ──
          console.log(`[FaultTest] support ${p.id}: source=real-mesh`,
            `| preview: tri=${p.previewEnvelope.triCount} area=${p.previewEnvelope.surfaceArea.toFixed(1)}`,
            `| final: tri=${finalEnvelope.triCount} area=${finalEnvelope.surfaceArea.toFixed(1)}`,
            `| gtDiff=${gtDiff.toFixed(4)} delta=${delta.toFixed(4)}`,
            `| ${gtDiff > DELTA_TOLERANCE && delta <= DELTA_TOLERANCE ? 'FAULT DETECTED (gt sees, fp blind)' :
                gtDiff <= DELTA_TOLERANCE && delta <= DELTA_TOLERANCE ? 'MATCH (both agree: same)' :
                gtDiff > DELTA_TOLERANCE && delta > DELTA_TOLERANCE ? 'BOTH SEE CHANGE' :
                'fp sees, gt blind (unexpected)'}`)

          // ── T1/T2/T3: Mesh-measured equality test (prompt 2.1) ──
          // Measures actual radii off STL triangles to prove manual==auto equality.
          if (p.engineMeshBase64 && finalMeshB64) {
            const previewMeas = measureSupportMesh(p.engineMeshBase64)
            const finalMeas = measureSupportMesh(finalMeshB64)
            const eqTol = 0.05 // 5% relative tolerance for mesh equality
            const relDiff = (a: number, b: number) => { const d = Math.max(Math.abs(a), Math.abs(b), 0.01); return Math.abs(a - b) / d }
            const checks = {
              triCount: { prev: previewMeas.triCount, final: finalMeas.triCount, rel: relDiff(previewMeas.triCount, finalMeas.triCount) },
              surfaceArea: { prev: +previewMeas.surfaceArea.toFixed(2), final: +finalMeas.surfaceArea.toFixed(2), rel: relDiff(previewMeas.surfaceArea, finalMeas.surfaceArea) },
              bboxW: { prev: +previewMeas.bboxW.toFixed(3), final: +finalMeas.bboxW.toFixed(3), rel: relDiff(previewMeas.bboxW, finalMeas.bboxW) },
              bboxH: { prev: +previewMeas.bboxH.toFixed(3), final: +finalMeas.bboxH.toFixed(3), rel: relDiff(previewMeas.bboxH, finalMeas.bboxH) },
              tipRadius: { prev: +previewMeas.tipRadius.toFixed(4), final: +finalMeas.tipRadius.toFixed(4), rel: relDiff(previewMeas.tipRadius, finalMeas.tipRadius) },
              baseRadius: { prev: +previewMeas.baseRadius.toFixed(3), final: +finalMeas.baseRadius.toFixed(3), rel: relDiff(previewMeas.baseRadius, finalMeas.baseRadius) },
              maxRadius: { prev: +previewMeas.maxRadius.toFixed(3), final: +finalMeas.maxRadius.toFixed(3), rel: relDiff(previewMeas.maxRadius, finalMeas.maxRadius) },
            }
            const failures = Object.entries(checks).filter(([, v]) => v.rel > eqTol)
            const status = failures.length === 0 ? 'EQUAL' : `DIVERGED (${failures.map(([k, v]) => `${k}: ${v.prev}→${v.final} (${(v.rel * 100).toFixed(1)}%)`).join(', ')})`
            console.log(`[MeshEquality] support ${p.id} tier=${p.type}: ${status}`,
              `\n  triCount: ${previewMeas.triCount} vs ${finalMeas.triCount}`,
              `| area: ${previewMeas.surfaceArea.toFixed(1)} vs ${finalMeas.surfaceArea.toFixed(1)}`,
              `| tipR: ${previewMeas.tipRadius.toFixed(4)} vs ${finalMeas.tipRadius.toFixed(4)}`,
              `| baseR: ${previewMeas.baseRadius.toFixed(3)} vs ${finalMeas.baseRadius.toFixed(3)}`,
              `| maxR: ${previewMeas.maxRadius.toFixed(3)} vs ${finalMeas.maxRadius.toFixed(3)}`)
          }

          if (gtDiff > DELTA_TOLERANCE && changeReasons.length === 0 && delta <= DELTA_TOLERANCE) {
            const diffFields = Object.entries(gt.fields).filter(([, v]) => v.rel > DELTA_TOLERANCE)
              .map(([k, v]) => `${k}: ${v.a.toFixed(2)}→${v.b.toFixed(2)} (${(v.rel * 100).toFixed(0)}%)`)
            changeReasons.push('changed (geometry differs)')
            console.error(`[Reconcile] COMPLETENESS GAP: support ${p.id}: groundTruthDiff=${gtDiff.toFixed(3)} but fingerprint delta=${delta.toFixed(3)}.`,
              'Mesh fields that differ:', diffFields.join(', '),
              '| Add these to SupportFingerprint to close the gap.')
          }
        } else if (p.previewEnvelope && !finalMeshB64 && finalSupport && finalStatus !== 'uncoverable') {
          // FALLBACK PATH: no per-support mesh from backend.
          // This shares the segment list's blindness with the fingerprint (Reading B).
          // It CANNOT see hollow/lattice/tessellation differences.
          // The support is VISIBLY marked as reduced-confidence.
          gtSource = 'segment-fallback'
          finalEnvelope = envelopeFromSegments(finalSupport.segments ?? [])
          const gt = computeGroundTruthDiff(p.previewEnvelope, finalEnvelope)
          gtDiff = gt.diff
          // Always flag the reduced confidence — never silently use the blind path
          changeReasons.push('reconciliation: segment-estimate (mesh unavailable, blind to hollow/lattice/tessellation)')
          console.warn(`[Reconcile] support ${p.id}: FALLBACK to segment-estimate — per-support mesh missing.`,
            `gtDiff=${gtDiff.toFixed(3)} (REDUCED CONFIDENCE). This support's matrix entry is NOT independently verified.`)
          if (gtDiff > DELTA_TOLERANCE && changeReasons.length === 1 && delta <= DELTA_TOLERANCE) {
            changeReasons.push('changed (geometry differs)')
          }
        } else if (p.previewEnvelope && !finalMeshB64 && !finalSupport && finalStatus !== 'uncoverable') {
          gtSource = 'none'
          gtDiff = 1.0
          changeReasons.push('geometry unavailable')
        }

        const uniqueReasons = [...new Set(changeReasons)]
        if (uniqueReasons.length > 0) {
          console.warn(`[Reconcile] support ${p.id}: delta=${delta.toFixed(3)} gtDiff=${gtDiff.toFixed(3)} src=${gtSource}, ${uniqueReasons.join(', ')}`)
        } else if (p.previewFingerprint && finalFingerprint) {
          console.log(`[Reconcile] support ${p.id}: MATCH delta=${delta.toFixed(4)} gtDiff=${gtDiff.toFixed(4)} src=${gtSource}`)
        }

        return { ...p, finalStatus, finalFingerprint, geometryDelta: delta, provisional: false,
          changeReasons: uniqueReasons.length > 0 ? uniqueReasons : undefined,
          finalEnvelope, groundTruthDiff: gtDiff }
      })
      const divergenceCount = reconciledPoints.filter(p => (p.changeReasons?.length ?? 0) > 0).length

      // ── Confusion matrix (logged for empirical completeness verification) ──
      // Rows: flagged (delta-based + named checks). Columns: actuallyDifferent (mesh-based gtDiff).
      // SEPARATELY track real-mesh vs segment-fallback supports.
      let realMeshCount = 0, fallbackCount = 0
      const matrix = { flaggedAndDiff: 0, flaggedAndSame: 0, unflaggedAndDiff: 0, unflaggedAndSame: 0 }
      for (const p of reconciledPoints) {
        const src = (v2Result.manualSupportMeshes ?? {})[p.id] ? 'real-mesh' : 'fallback'
        if (src === 'real-mesh') realMeshCount++; else fallbackCount++
        const flagged = (p.changeReasons?.length ?? 0) > 0
        const actuallyDiff = (p.groundTruthDiff ?? 0) > DELTA_TOLERANCE
        if (flagged && actuallyDiff) matrix.flaggedAndDiff++
        else if (flagged && !actuallyDiff) matrix.flaggedAndSame++
        else if (!flagged && actuallyDiff) matrix.unflaggedAndDiff++
        else matrix.unflaggedAndSame++
      }
      const qualifiedClaim = fallbackCount > 0
        ? `${realMeshCount} verified by real mesh, ${fallbackCount} by segment-estimate (blind to hollow/lattice/tessellation)`
        : `all ${realMeshCount} verified by real mesh`
      console.log('[Reconcile] CONFUSION MATRIX (rows=flagged[fingerprint], cols=actuallyDifferent[mesh]):',
        `\n  flagged+different=${matrix.flaggedAndDiff}`,
        `flagged+same=${matrix.flaggedAndSame}`,
        `\n  UNFLAGGED+different=${matrix.unflaggedAndDiff}`,
        `unflagged+same=${matrix.unflaggedAndSame}`,
        `\n  Coverage: ${qualifiedClaim}`,
        matrix.unflaggedAndDiff > 0 ? '\n  ⚠ COMPLETENESS GAP: unflagged-and-different > 0' : '\n  ✓ No completeness gaps (for real-mesh subset)')

      updateModels(prev => prev.map(m => m.id === modelId ? {
        ...m,
        transform: { ...DEFAULT_TRANSFORM },
        manualSupports: { ...m.manualSupports, points: reconciledPoints },
        prep: {
          autoSupports: v2Result.supports.map((s: any) => ({
            x: s.contactX, y: s.contactY, contactZ: s.contactZ, baseZ: s.baseZ,
            tipDiameter: s.preset?.tipDiameterMm ?? 0.4, columnDiameter: s.preset?.shaftDiameterMm ?? 0.8, baseDiameter: s.preset?.baseDiameterMm ?? 2.0,
          })),
          advancedSupports: v2Result.supports,
          crossBraces: v2Result.crossBraces,
          raft: autoSupportConfig.raftEnabled && v2Result.supports.length > 0 ? (() => {
            const xs = v2Result.supports.map((s: any) => s.baseX)
            const ys = v2Result.supports.map((s: any) => s.baseY)
            const margin = autoSupportConfig.raftMargin ?? 1.5
            return {
              type: autoSupportConfig.raftType ?? 'grid',
              minX: Math.min(...xs) - margin, minY: Math.min(...ys) - margin,
              maxX: Math.max(...xs) + margin, maxY: Math.max(...ys) + margin,
              thicknessMm: autoSupportConfig.raftThickness ?? 0.3,
            }
          })() : null,
          skirt: autoSupportConfig.skirtEnabled && v2Result.supports.length > 0 ? (() => {
            const xs = v2Result.supports.map((s: any) => s.baseX)
            const ys = v2Result.supports.map((s: any) => s.baseY)
            const dist = autoSupportConfig.skirtDistance ?? 2.0
            return {
              minX: Math.min(...xs) - dist, minY: Math.min(...ys) - dist,
              maxX: Math.max(...xs) + dist, maxY: Math.max(...ys) + dist,
              layers: autoSupportConfig.skirtLayers ?? 3,
              distanceMm: dist, widthMm: 0.4,
            }
          })() : null,
          locked: true,
          stale: false,
          generatedAt: Date.now(),
          v2Stats,
          v2MeshBuffer: meshBuffer,
          v2MeshOffset: v2Result.meshOffset ?? null,
          uncoverableManualIds: v2Result.uncoverableManualIds ?? [],
        }
      } : m))

      // Surface preview→final divergences as a visible warning
      if (divergenceCount > 0) {
        const divergedList = reconciledPoints
          .filter(p => (p.changeReasons?.length ?? 0) > 0)
          .map(p => `  ${p.id}: ${p.changeReasons!.join(', ')}`)
        alert(`${divergenceCount} manual support(s) changed after full pipeline:\n${divergedList.join('\n')}\n\nThe V2 mesh now shows the final geometry.`)
      }
    } catch (err: any) {
      console.error(`Auto-support failed for ${modelId}:`, err)
      throw err // re-throw so callers (batch or single) can handle
    }
  }

  /** Generate supports for the currently selected model. */
  const generateAutoSupports = async () => {
    if (!selectedId || !selected) return
    setGenerating(true)
    setGeneratingProgress('')
    try {
      await generateSupportsForModel(selectedId)
    } catch (err: any) {
      alert('Support generation failed: ' + (err?.message || 'Unknown error'))
    } finally {
      setGenerating(false)
      setGeneratingProgress('')
    }
  }

  /** Generate supports for ALL loaded models sequentially. */
  const generateAllSupports = async () => {
    if (models.length === 0) return
    setGenerating(true)
    const results: { id: string; name: string; ok: boolean; error?: string }[] = []
    for (let i = 0; i < models.length; i++) {
      const m = models[i]
      setGeneratingProgress(`Generating ${i + 1} of ${models.length}: ${m.fileName}...`)
      try {
        commitOrientation(m.id)
        await generateSupportsForModel(m.id)
        results.push({ id: m.id, name: m.fileName, ok: true })
      } catch (err: any) {
        results.push({ id: m.id, name: m.fileName, ok: false, error: err?.message || 'Unknown error' })
      }
    }
    setGenerating(false)
    setGeneratingProgress('')
    const succeeded = results.filter(r => r.ok).length
    const failed = results.filter(r => !r.ok)
    if (failed.length > 0) {
      alert(`Support generation: ${succeeded} succeeded, ${failed.length} failed:\n${failed.map(f => `  ${f.name}: ${f.error}`).join('\n')}`)
    }
  }

  const clearPrep = () => {
    if (!selectedId) return
    updateModels(prev => prev.map(m => m.id === selectedId ? { ...m, prep: { ...EMPTY_PREP } } : m))
  }

  const toggleLock = () => {
    if (!selectedId) return
    updateModels(prev => prev.map(m => m.id === selectedId ? {
      ...m, prep: { ...m.prep, locked: !m.prep.locked }
    } : m))
  }

  // ── Per-object settings ─────────────────────────────────────────────────────

  const patchSettings = (field: keyof ObjectSettings, value: number | string | boolean | null) => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, overrideSettings: { ...m.overrideSettings, [field]: value } } : m
    ))
  }

  const resetSettings = () => {
    if (!selectedId) return
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, overrideSettings: { ...DEFAULT_SETTINGS } } : m
    ))
  }

  const hasOverrides = selected?.overrideSettings &&
    Object.values(selected.overrideSettings).some(v => v !== null)

  // ── Warnings ────────────────────────────────────────────────────────────────

  const warningCount = overlaps.size + outOfBoundsIds.size

  // ── Render ──────────────────────────────────────────────────────────────────

  return (
    <div className="flex flex-col gap-3 h-[calc(100vh-4rem)]" tabIndex={0}
         onKeyDown={e => {
           if (e.key === 'Delete' && selectedId) deleteModel(selectedId)
           if (e.ctrlKey && e.key === 'z') undo()
         }}>

      {/* ── Toolbar ────────────────────────────────────────────────────── */}
      <div className="flex items-center gap-2 flex-shrink-0 px-1">
        <h1 className="text-xl font-bold mr-auto">Import STL</h1>

        {models.length > 0 && (
          <>
            {/* Placement actions */}
            <button onClick={() => viewerRef.current?.centerOnBed()}
              title="Center on bed"
              className="text-xs px-3 py-1.5 rounded-lg bg-gray-800 hover:bg-gray-700 text-gray-300 transition">
              Center
            </button>
            <button onClick={() => viewerRef.current?.placeOnBed()}
              title="Place on bed (Z=0)"
              className="text-xs px-3 py-1.5 rounded-lg bg-gray-800 hover:bg-gray-700 text-gray-300 transition">
              Place on Bed
            </button>
            <button onClick={() => viewerRef.current?.resetTransform()}
              title="Reset transform"
              className="text-xs px-3 py-1.5 rounded-lg bg-gray-800 hover:bg-gray-700 text-gray-300 transition">
              Reset
            </button>

            <div className="w-px h-5 bg-gray-700" />

            {/* Auto-arrange */}
            <button onClick={() => viewerRef.current?.autoArrange()}
              title="Auto-arrange all parts with spacing"
              className="text-xs px-3 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white font-medium transition">
              Auto-Arrange
            </button>
            <label className="flex items-center gap-1 text-[10px] text-gray-500" title="Spacing between parts (mm)">
              <input type="number" value={spacing} min={0} max={50} step={1}
                onChange={e => { const v = +e.target.value; setSpacing(v); _savedSpacing = v }}
                className="w-10 bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-xs text-gray-300 text-center" />
              mm
            </label>

            <div className="w-px h-5 bg-gray-700" />

            {/* Model count + warnings */}
            <span className="text-xs text-gray-500">{models.length} model{models.length > 1 ? 's' : ''}</span>

            {warningCount > 0 && (
              <span className="text-xs text-amber-400 flex items-center gap-1" title={
                `${overlaps.size ? overlaps.size / 2 + ' overlapping pair(s)' : ''}${overlaps.size && outOfBoundsIds.size ? ', ' : ''}${outOfBoundsIds.size ? outOfBoundsIds.size + ' out of bounds' : ''}`
              }>
                <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 20 20">
                  <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
                </svg>
                {warningCount}
              </span>
            )}

            <button onClick={() => setProfilePanelOpen(!profilePanelOpen)}
              className={`text-xs px-3 py-1.5 rounded-lg transition ${
                profilePanelOpen ? 'bg-indigo-600/20 text-indigo-300 border border-indigo-500/30' : 'bg-gray-800 hover:bg-gray-700 text-gray-400'
              }`}>
              Profiles
            </button>

            <div className="w-px h-5 bg-gray-700" />

            {/* Support controls */}
            <button onClick={() => setSupportEnabled(!jobSupportEnabled)}
              className={`text-xs px-3 py-1.5 rounded-lg font-medium transition ${
                jobSupportEnabled
                  ? 'bg-green-600/20 text-green-400 border border-green-600/30'
                  : 'bg-gray-800 text-gray-500 hover:bg-gray-700'
              }`}>
              {jobSupportEnabled ? 'Supports ON' : 'Supports'}
            </button>
            {jobSupportEnabled && (
              <>
                <select value={jobSupportType} onChange={e => setSupportType(e.target.value as 'normal' | 'tree')}
                  className="text-xs bg-gray-800 border border-gray-700 rounded-lg px-1.5 py-1.5 text-gray-300">
                  <option value="normal">Normal</option>
                  <option value="tree">Tree</option>
                </select>
                <select value={jobSupportPlacement} onChange={e => setSupportPlacement(e.target.value as 'buildplate' | 'everywhere')}
                  className="text-xs bg-gray-800 border border-gray-700 rounded-lg px-1.5 py-1.5 text-gray-300">
                  <option value="buildplate">Plate Only</option>
                  <option value="everywhere">Everywhere</option>
                </select>
              </>
            )}

            <div className="w-px h-5 bg-gray-700" />

            {/* Printer + Profile selectors */}
            <select value={selectedPrinterId} onChange={e => { setSelectedPrinterId(e.target.value); setSliceStale(true) }}
              className="text-xs bg-gray-800 border border-gray-700 rounded-lg px-2 py-1.5 text-gray-300 max-w-[140px] truncate">
              <option value="">Printer...</option>
              {resinPrinters.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
            <select value={selectedProfileId} onChange={e => { setSelectedProfileId(e.target.value); setSliceStale(true) }}
              className="text-xs bg-gray-800 border border-gray-700 rounded-lg px-2 py-1.5 text-gray-300 max-w-[140px] truncate">
              <option value="">Profile...</option>
              {printProfiles.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>

            {/* Slice button */}
            <button onClick={handleSlice} disabled={slicing || models.length === 0 || !selectedPrinterId || !selectedProfileId}
              className={`text-xs px-4 py-1.5 rounded-lg font-medium transition ${
                slicing ? 'bg-amber-600 text-white animate-pulse' :
                sliceStale && sliceResult ? 'bg-amber-600 hover:bg-amber-500 text-white' :
                'bg-green-600 hover:bg-green-500 text-white'
              } disabled:opacity-40 disabled:cursor-not-allowed`}>
              {slicing ? 'Slicing...' : sliceStale && sliceResult ? 'Re-Slice' : 'Slice'}
            </button>

            <button onClick={async () => {
              if (models.length < 2) return
              try {
                const parts = models.map(m => ({
                  id: m.id,
                  widthMm: m.size?.x ?? 20,
                  depthMm: m.size?.y ?? 20,
                }))
                const bv = buildVolume
                const result = await prepToolsApi.nest(parts, bv.width, bv.depth)
                updateModels(prev => prev.map(m => {
                  const p = result.placements.find(pl => pl.id === m.id)
                  if (!p) return m
                  return { ...m, transform: { ...m.transform, x: p.centerX - bv.width / 2, y: p.centerY - bv.depth / 2 } }
                }))
                if (result.overflow.length > 0)
                  console.warn('[Nest] Overflow:', result.overflow)
              } catch (err) { console.error('Auto-arrange failed:', err) }
            }} title="Auto-arrange models on build plate"
              disabled={models.length < 2}
              className="text-xs px-2 py-1.5 rounded-lg bg-gray-800 border border-gray-700 text-gray-400 hover:text-teal-300 hover:border-teal-500/40 transition disabled:opacity-40 disabled:cursor-not-allowed">
              Arrange
            </button>
            <button onClick={saveProject} title="Save project (.vatproj)"
              className="text-xs px-2 py-1.5 rounded-lg bg-gray-800 border border-gray-700 text-gray-400 hover:text-teal-300 hover:border-teal-500/40 transition">
              Save
            </button>
            <label title="Load project (.vatproj)"
              className="text-xs px-2 py-1.5 rounded-lg bg-gray-800 border border-gray-700 text-gray-400 hover:text-teal-300 hover:border-teal-500/40 transition cursor-pointer">
              Load
              <input type="file" accept=".vatproj,.json" className="hidden" onChange={loadProject} />
            </label>
            <button onClick={clearAll}
              className="text-xs px-2 py-1 rounded text-red-400/70 hover:text-red-300 hover:bg-red-900/20 transition">
              Clear All
            </button>
          </>
        )}
      </div>

      {/* Slice error/status */}
      {sliceError && (
        <div className="bg-red-900/30 border border-red-800 rounded-lg px-4 py-2 text-xs text-red-300 flex-shrink-0">
          {sliceError}
          <button onClick={() => setSliceError(null)} className="ml-3 text-red-400 hover:text-red-200">dismiss</button>
        </div>
      )}

      {/* Slice result summary */}
      {sliceResult && !sliceStale && (
        <div className="bg-green-900/20 border border-green-800/40 rounded-lg px-4 py-2 text-xs text-green-300 flex-shrink-0 flex items-center gap-4">
          <span className="font-medium">Sliced</span>
          <span>{sliceResult.layerCount} layers</span>
          <span>{sliceResult.totalHeightMm.toFixed(1)} mm</span>
          <span>{sliceResult.resolutionX}x{sliceResult.resolutionY} px</span>
          <span>{sliceResult.estimatedPrintTimeMin.toFixed(1)} min est.</span>
          <span className="text-green-500">{sliceResult.elapsedMs}ms</span>
          {/* Export button */}
          <select
            onChange={async (e) => {
              const fmt = e.target.value
              if (!fmt || !sliceResult?.jobId) return
              try {
                const blob = await resinSliceApi.exportJob(sliceResult.jobId, fmt)
                const url = URL.createObjectURL(blob)
                const a = document.createElement('a')
                a.href = url
                a.download = `print.${fmt === 'sl1' ? 'sl1' : fmt}`
                a.click()
                URL.revokeObjectURL(url)
              } catch (err) { console.error('Export failed:', err) }
              e.target.value = ''
            }}
            className="bg-green-800/50 border border-green-700 rounded px-2 py-0.5 text-[10px] text-green-300"
          >
            <option value="">Export...</option>
            <option value="ctb">.ctb (ChiTuBox)</option>
            <option value="cbddlp">.cbddlp (Anycubic)</option>
            <option value="photon">.photon (Anycubic)</option>
            <option value="sl1">.sl1 (Prusa)</option>
            <option value="zip">.zip (Generic)</option>
          </select>
        </div>
      )}

      <div className="flex gap-4 flex-1 min-h-0">
        {/* ── 3D Viewer / Drop zone / Layer preview ──────────────────── */}
        <div className="flex-1 flex flex-col min-h-0 gap-2">
          <div
            className={`flex-1 relative bg-gray-950 rounded-xl border-2 transition-colors min-h-0 ${
              isDragOver ? 'border-indigo-500 bg-indigo-950/20' : 'border-gray-800'
            }`}
            onDragOver={e => { e.preventDefault(); setIsDragOver(true) }}
            onDragLeave={() => setIsDragOver(false)}
            onDrop={handleDrop}
          >
            {models.length > 0 ? (
              <>
                <StlViewer
                  ref={viewerRef}
                  models={models}
                  selectedId={selectedId}
                  onModelSelect={(id: string | null) => { setSelectedId(id); _savedSelectedId = id }}
                  onTransformChange={handleTransformChange}
                  onSizeChange={(id, size) => {
                    updateModels(prev => {
                      const m = prev.find(x => x.id === id)
                      if (m?.size && m.size.x === size.x && m.size.y === size.y && m.size.z === size.z) return prev
                      return prev.map(x => x.id === id ? { ...x, size } : x)
                    })
                  }}
                  onBoundsChange={(id, out) => {
                    updateModels(prev => {
                      const m = prev.find(x => x.id === id)
                      if (!m || m.isOutOfBounds === out) return prev // no change → same reference → no re-render
                      return prev.map(x => x.id === id ? { ...x, isOutOfBounds: out } : x)
                    })
                  }}
                  buildVolume={buildVolume}
                  supportEditMode={supportEditMode}
                  supportPoints={memoSupportPoints}
                  crossBraces={selectedPrep.crossBraces}
                  supportMeshBuffer={selectedPrep.v2MeshBuffer}
                  supportMeshOffset={selectedPrep.v2MeshOffset}
                  allSupportMeshes={memoAllSupportMeshes}
                  manualMarkers={memoManualMarkers}
                  orientationCommitted={orientationCommitted}
                  paintedRegions={selectedSupportData.paintedRegions}
                  supportTipType={supportTipType}
                  supportBrushSize={supportBrushSize}
                  onSupportPointAdd={(x, y, z, nx, ny, nz, fi, bu, bv) => addSupportPoint(x, y, z, nx, ny, nz, fi, bu, bv)}
                  onSupportPointDelete={(id) => deleteSupportPoint(id)}
                  onSupportPointSelect={(id) => setSelectedManualSupportId(id)}
                  selectedManualSupportId={selectedManualSupportId}
                  onPaintRegionAdd={(mode, cx, cy, cz) => addPaintedRegion(mode, cx, cy, cz)}
                  raftData={selectedPrep.raft}
                  skirtData={selectedPrep.skirt}
                  analyzeMode={analyzeMode}
                />
                {/* Viewer toolbar */}
                <div className="absolute bottom-3 right-3 flex items-center gap-2">
                  <button
                    onClick={() => setAnalyzeMode(p => !p)}
                    className={`text-xs px-3 py-1.5 rounded-lg transition backdrop-blur-sm border
                      ${analyzeMode
                        ? 'bg-teal-500/20 text-teal-300 border-teal-500/40'
                        : 'bg-gray-800/80 hover:bg-gray-700/90 text-gray-400 hover:text-gray-200 border-gray-700/50'}`}
                  >
                    {analyzeMode ? 'Analyze ON' : 'Analyze'}
                  </button>
                  <label className="cursor-pointer text-xs px-3 py-1.5 rounded-lg
                                    bg-gray-800/80 hover:bg-gray-700/90 text-gray-400 hover:text-gray-200
                                    transition backdrop-blur-sm border border-gray-700/50">
                    + Add Model
                    <input type="file" accept=".stl,.obj,.3mf" multiple className="hidden" onChange={handleFileInput} />
                  </label>
                </div>
              </>
            ) : (
              <div className="absolute inset-0 flex flex-col items-center justify-center text-gray-500 gap-3 select-none">
                <svg className="w-14 h-14 opacity-40" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1}
                    d="M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10" />
                </svg>
                <p className="text-sm font-medium">Drag & drop STL files here</p>
                <p className="text-xs text-gray-600">Multiple files supported</p>
                <label className="cursor-pointer text-xs px-4 py-2 rounded-lg bg-gray-800 hover:bg-gray-700 transition text-gray-300">
                  or Browse files
                  <input type="file" accept=".stl,.obj,.3mf" multiple className="hidden" onChange={handleFileInput} />
                </label>
              </div>
            )}
          </div>

          {/* Layer preview scrubber + per-layer data */}
          {sliceResult && !sliceStale && (() => {
            const li = layerData[previewLayer] // structured layer info
            return (
            <div className="bg-gray-900 rounded-xl border border-gray-800 p-3 flex-shrink-0">
              <div className="flex items-center gap-3">
                <span className="text-[10px] text-gray-500 w-16 text-right">
                  Layer {previewLayer + 1}/{sliceResult.layerCount}
                </span>
                <input type="range" min={0} max={sliceResult.layerCount - 1} value={previewLayer}
                  onChange={e => setPreviewLayer(+e.target.value)}
                  className="flex-1 h-1 accent-indigo-500" />
                <span className="text-[10px] text-gray-500 w-16">
                  Z={li ? li.zHeightMm.toFixed(2) : ((previewLayer + 0.5) * sliceResult.layerHeightMm).toFixed(2)}mm
                </span>
                <span className={`text-[10px] px-1.5 py-0.5 rounded ${
                  (li?.type ?? '') === 'Bottom' ? 'bg-amber-900/40 text-amber-400' : 'bg-gray-800 text-gray-500'
                }`}>
                  {li?.type ?? (previewLayer < sliceResult.bottomLayerCount ? 'Bottom' : 'Normal')}
                </span>
              </div>

              {/* Per-layer metadata */}
              {li && (
                <div className="flex gap-4 mt-1.5 text-[9px] text-gray-500">
                  <span>Exposure: <span className="text-gray-300">{li.exposureMs}ms</span></span>
                  <span>Lift: <span className="text-gray-300">{li.liftDistanceMm}mm @ {li.liftSpeedMmPerMin}mm/m</span></span>
                  <span>Contours: <span className="text-gray-300">{li.contourCount}</span></span>
                  {li.isEmpty && <span className="text-amber-400">Empty</span>}
                  <span className="ml-auto">{(li.imageSizeBytes / 1024).toFixed(1)}KB</span>
                </div>
              )}

              {/* Layer image preview */}
              <div className="mt-2 flex justify-center">
                <img
                  key={`${sliceResult.jobId}-${previewLayer}`}
                  src={resinSliceApi.getLayerImageUrl(sliceResult.jobId, previewLayer)}
                  alt={`Layer ${previewLayer}`}
                  className="max-h-48 rounded border border-gray-700 bg-black"
                  style={{ imageRendering: 'pixelated' }}
                />
              </div>
            </div>
            )
          })()}
        </div>

        {/* ── Unified Right Panel (collapsible, tabbed) ────────────── */}
        <div className={`flex-shrink-0 bg-gray-900 border-l border-gray-800 transition-all duration-200 flex flex-col ${
          profilePanelOpen ? 'w-72' : 'w-8'
        }`}>
          {/* Tab bar + collapse */}
          <div className="flex items-center border-b border-gray-800 flex-shrink-0">
            {profilePanelOpen ? (
              <>
                {(['model', 'supports', 'print', 'material'] as const).map(tab => (
                  <button key={tab} onClick={() => setActiveRightTab(tab)}
                    className={`flex-1 text-[10px] font-medium py-2 transition capitalize ${
                      activeRightTab === tab
                        ? (tab === 'supports' ? 'text-green-400 border-b-2 border-green-500' :
                           tab === 'material' ? 'text-violet-400 border-b-2 border-violet-500' :
                           'text-indigo-400 border-b-2 border-indigo-500')
                        : 'text-gray-500 hover:text-gray-300'
                    }`}>{tab}</button>
                ))}
                <button onClick={() => setProfilePanelOpen(false)}
                  className="px-2 py-2 text-gray-600 hover:text-gray-400 transition">
                  <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                  </svg>
                </button>
              </>
            ) : (
              <button onClick={() => setProfilePanelOpen(true)}
                className="w-full py-2 text-gray-600 hover:text-gray-400 transition flex justify-center">
                <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
                </svg>
              </button>
            )}
          </div>

          {profilePanelOpen ? (
            /* ── TAB CONTENT ─── */
            activeRightTab === 'print' ? (
              <PrintProfilePanel onProfileChange={profile => {
                if (profile) {
                  setSupportEnabled(profile.supportEnabled)
                  setSupportType(profile.supportType as 'normal' | 'tree')
                  setSupportPlacement(profile.supportPlacement as 'buildplate' | 'everywhere')
                }
              }} />
            ) : activeRightTab === 'material' ? (
              <MaterialProfilePanel />
            ) : activeRightTab === 'supports' ? (
              <div className="flex-1 overflow-y-auto p-3 space-y-3">
                {selected ? (<>
                  {/* Printer orientation info */}
                  {activePrinter && (
                    <div className={`text-[10px] px-2 py-1.5 rounded ${
                      activePrinter.orientation === 'TopDown' ? 'bg-cyan-900/20 text-cyan-400' : 'bg-indigo-900/20 text-indigo-400'
                    }`}>
                      {activePrinter.orientation === 'TopDown' ? 'Top-Down' : 'Bottom-Up'} printer — supports {activePrinter.orientation === 'TopDown' ? 'build upward from plate' : 'connect inverted model to plate above'}
                    </div>
                  )}

                  {/* Auto Support Generation — Visual Picker */}
                  <div className="bg-gray-800/50 rounded-xl">
                    <div className="px-3 pt-3 pb-1">
                      <h3 className="text-xs font-semibold text-green-400 uppercase tracking-wider mb-1">Support Options</h3>
                    </div>
                    <SupportOptionsPicker value={supportOptions} onChange={setSupportOptions} />

                    {/* Advanced numeric controls (collapsed) */}
                    <details className="px-3 pb-3">
                      <summary className="text-[9px] text-gray-500 cursor-pointer hover:text-gray-400 py-1">Advanced Parameters</summary>
                      <div className="space-y-1.5 mt-1">
                        <label className="flex items-center justify-between text-[10px]">
                          <span className="text-gray-500">Overhang Angle</span>
                          <div className="flex items-center gap-1">
                            <input type="number" value={autoSupportConfig.overhangAngle} min={10} max={80} step={5}
                              onChange={() => {
                                setSupportOptions(p => p) // force re-derive
                              }}
                              className="w-12 bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-right" />
                            <span className="text-gray-600 text-[9px]">deg</span>
                          </div>
                        </label>
                        <label className="flex items-center justify-between text-[10px]">
                          <span className="text-gray-500">Material</span>
                          <select value={autoSupportConfig.materialPreset}
                            onChange={() => {}}
                            className="bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200">
                            <option value="standard">Standard Resin</option>
                            <option value="tough">Tough Resin</option>
                            <option value="flexible">Flexible Resin</option>
                            <option value="castable">Castable Resin</option>
                            <option value="dental">Dental Model</option>
                          </select>
                        </label>
                      </div>
                    </details>

                    {/* HIDDEN: preserve old select for backward compat if needed */}
                    <select value={autoSupportConfig.supportType} onChange={() => {}} className="hidden">
                      <optgroup label="Weight">
                        <option value="light">Light (thin tips, easy removal)</option>
                        <option value="medium">Medium (balanced strength/marks)</option>
                        <option value="heavy">Heavy (1.0mm tip, maximum hold)</option>
                      </optgroup>
                      <optgroup label="Tip Shape">
                        <option value="point-tip">Point Tip (0.15mm, cosmetic)</option>
                        <option value="pyramid-tip">Pyramid Tip</option>
                        <option value="skate-tip">Skate Tip (elongated)</option>
                        <option value="chisel-tip">Chisel Tip (blade/edge)</option>
                        <option value="mushroom-tip">Mushroom Tip (snap-off)</option>
                        <option value="cross-tip">Cross Tip (plus-shape)</option>
                        <option value="ring-tip">Ring Tip (hollow circle)</option>
                        <option value="needle-tip">Needle Tip (ultra-thin, e-Stage)</option>
                      </optgroup>
                      <optgroup label="Shaft Style">
                        <option value="tapered">Tapered Column (wider base)</option>
                        <option value="hollow-tube">Hollow Tube</option>
                        <option value="square">Square Column</option>
                        <option value="x-profile">X-Profile (material saving)</option>
                        <option value="i-beam">I-Beam (lateral strength)</option>
                        <option value="lattice-column">Lattice Column</option>
                        <option value="spiral">Spiral (torsion resistant)</option>
                        <option value="ribbed">Ribbed (extra rigidity)</option>
                        <option value="diamond-open">Diamond/Open (drain friendly)</option>
                      </optgroup>
                      <optgroup label="Base Style">
                        <option value="cone-base">Cone Base</option>
                        <option value="pyramid-base">Pyramid Base</option>
                        <option value="raft-base">Raft Base (shared platform)</option>
                        <option value="miniraft-base">Mini Raft Base</option>
                        <option value="pin-base">Pin Base (minimal)</option>
                        <option value="skirted-base">Skirted Base</option>
                        <option value="webbed-base">Webbed Base (star pattern)</option>
                        <option value="anchor-base">Anchor Base (max adhesion)</option>
                      </optgroup>
                      <optgroup label="Structure">
                        <option value="tree">Tree/Branching (material saving)</option>
                        <option value="crossbraced">Cross-Braced (rigid)</option>
                        <option value="wall-blade">Wall/Blade/Fin</option>
                        <option value="small-pillar">Small Pillar (micro)</option>
                        <option value="scaffold">Scaffold (grid lattice)</option>
                        <option value="truss">Truss (triangulated)</option>
                        <option value="gusset">Gusset (part-to-part)</option>
                        <option value="cage">Cage (enclosing)</option>
                        <option value="organic">Organic (freeform curves)</option>
                      </optgroup>
                      <optgroup label="Neck Style">
                        <option value="waist-neck">Waist Neck (hourglass break)</option>
                        <option value="perforated-neck">Perforated Neck (clean break)</option>
                        <option value="double-neck">Double Neck (predictable)</option>
                      </optgroup>
                    </select>

                    {/* ── Legacy controls (hidden, replaced by picker above) ── */}
                    <div className="hidden">
                    <label className="flex items-center justify-between text-[10px] mt-1">
                      <span className="text-gray-500">Support Exposure</span>
                      <div className="flex items-center gap-1">
                        <input type="range" min={50} max={100} step={5} value={autoSupportConfig.supportExposurePct}
                          onChange={e => setAutoSupportConfig(p => ({ ...p, supportExposurePct: +e.target.value }))}
                          className="w-16 h-1 accent-green-500" />
                        <span className="text-gray-400 w-8 text-right text-[9px]">{autoSupportConfig.supportExposurePct}%</span>
                      </div>
                    </label>
                    {autoSupportConfig.supportExposurePct < 100 && (
                      <p className="text-[8px] text-amber-500/70 mt-0.5">Reduced exposure makes supports easier to remove</p>
                    )}

                    {/* V2 Advanced Features */}
                    <div className="mt-3 pt-2 border-t border-gray-700/50">
                      <h4 className="text-[9px] font-semibold text-teal-400 uppercase tracking-wider mb-1.5">Advanced</h4>
                      <div className="space-y-1">
                        <label className="flex items-center gap-2 text-[10px] cursor-pointer">
                          <input type="checkbox" checked={autoSupportConfig.treeSupports}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, treeSupports: e.target.checked }))}
                            className="rounded border-gray-600 bg-gray-800 w-3 h-3 text-teal-500" />
                          <span className="text-gray-400">Tree Supports</span>
                          <span className="text-gray-600 text-[8px] ml-auto">merge nearby pillars</span>
                        </label>
                        <label className="flex items-center gap-2 text-[10px] cursor-pointer">
                          <input type="checkbox" checked={autoSupportConfig.crossBracing}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, crossBracing: e.target.checked }))}
                            className="rounded border-gray-600 bg-gray-800 w-3 h-3 text-teal-500" />
                          <span className="text-gray-400">Cross-Braces</span>
                          <span className="text-gray-600 text-[8px] ml-auto">connect pillars</span>
                        </label>
                        {autoSupportConfig.crossBracing && (
                          <label className="flex items-center justify-between text-[10px] pl-5">
                            <span className="text-gray-500">Max Distance</span>
                            <div className="flex items-center gap-1">
                              <input type="range" min={10} max={100} step={5} value={autoSupportConfig.crossBraceDistMm}
                                onChange={e => setAutoSupportConfig(p => ({ ...p, crossBraceDistMm: +e.target.value }))}
                                className="w-14 h-1 accent-teal-500" />
                              <span className="text-gray-400 w-10 text-right text-[9px]">{autoSupportConfig.crossBraceDistMm}mm</span>
                            </div>
                          </label>
                        )}
                        <label className="flex items-center gap-2 text-[10px] cursor-pointer">
                          <input type="checkbox" checked={autoSupportConfig.hollowSupports}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, hollowSupports: e.target.checked }))}
                            className="rounded border-gray-600 bg-gray-800 w-3 h-3 text-teal-500" />
                          <span className="text-gray-400">Hollow Supports</span>
                          <span className="text-gray-600 text-[8px] ml-auto">&gt;{autoSupportConfig.hollowMinHeight}mm</span>
                        </label>
                        <label className="flex items-center gap-2 text-[10px] cursor-pointer">
                          <input type="checkbox" checked={autoSupportConfig.miniRafts}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, miniRafts: e.target.checked }))}
                            className="rounded border-gray-600 bg-gray-800 w-3 h-3 text-teal-500" />
                          <span className="text-gray-400">Mini Rafts</span>
                          <span className="text-gray-600 text-[8px] ml-auto">per-support pads</span>
                        </label>
                        <label className="flex items-center justify-between text-[10px]">
                          <span className="text-gray-500">Base Pattern</span>
                          <select value={autoSupportConfig.latticePattern}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, latticePattern: e.target.value }))}
                            className="bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200">
                            <option value="grid">Grid</option>
                            <option value="honeycomb">Honeycomb</option>
                            <option value="cross">Cross</option>
                            <option value="solid">Solid</option>
                          </select>
                        </label>
                        <label className="flex items-center justify-between text-[10px]">
                          <span className="text-gray-500">Material</span>
                          <select value={autoSupportConfig.materialPreset}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, materialPreset: e.target.value }))}
                            className="bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200">
                            <option value="standard">Standard Resin</option>
                            <option value="tough">Tough Resin</option>
                            <option value="flexible">Flexible Resin</option>
                            <option value="castable">Castable Resin</option>
                            <option value="dental">Dental Model</option>
                          </select>
                        </label>
                      </div>
                    </div>

                    {/* Raft */}
                    <div className="mt-3 pt-2 border-t border-gray-700/50">
                      <label className="flex items-center gap-2 text-[10px] cursor-pointer mb-1">
                        <input type="checkbox" checked={autoSupportConfig.raftEnabled}
                          onChange={e => setAutoSupportConfig(p => ({ ...p, raftEnabled: e.target.checked }))}
                          className="rounded border-gray-600 bg-gray-800 w-3 h-3 text-green-500" />
                        <span className="text-gray-400">Raft</span>
                      </label>
                      {autoSupportConfig.raftEnabled && (
                        <select value={autoSupportConfig.raftType} onChange={e => setAutoSupportConfig(p => ({ ...p, raftType: e.target.value }))}
                          className="w-full bg-gray-800 border border-gray-700 rounded px-1.5 py-0.5 text-[10px] text-gray-200 mt-1">
                          <option value="solid">Solid Raft</option>
                          <option value="grid">Grid Raft</option>
                          <option value="pad">Pad Raft</option>
                        </select>
                      )}
                    </div>

                    {/* Skirt */}
                    <div className="mt-2 pt-2 border-t border-gray-700/50">
                      <label className="flex items-center gap-2 text-[10px] cursor-pointer mb-1">
                        <input type="checkbox" checked={autoSupportConfig.skirtEnabled}
                          onChange={e => setAutoSupportConfig(p => ({ ...p, skirtEnabled: e.target.checked }))}
                          className="rounded border-gray-600 bg-gray-800 w-3 h-3 text-green-500" />
                        <span className="text-gray-400">Skirt</span>
                      </label>
                      {autoSupportConfig.skirtEnabled && (
                        <div className="space-y-1 mt-1">
                          <label className="flex items-center justify-between text-[10px]">
                            <span className="text-gray-500">Layers</span>
                            <input type="number" value={autoSupportConfig.skirtLayers} min={1} max={20} step={1}
                              onChange={e => setAutoSupportConfig(p => ({ ...p, skirtLayers: +e.target.value }))}
                              className="w-10 bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-right" />
                          </label>
                          <label className="flex items-center justify-between text-[10px]">
                            <span className="text-gray-500">Distance</span>
                            <div className="flex items-center gap-1">
                              <input type="number" value={autoSupportConfig.skirtDistance} min={0.5} max={10} step={0.5}
                                onChange={e => setAutoSupportConfig(p => ({ ...p, skirtDistance: +e.target.value }))}
                                className="w-10 bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-right" />
                              <span className="text-gray-600 text-[9px]">mm</span>
                            </div>
                          </label>
                        </div>
                      )}
                    </div>

                    </div>{/* end hidden legacy controls */}

                    {/* Prep Tools */}
                    <div className="mt-3 pt-2 border-t border-gray-700/50 flex gap-1">
                      <button onClick={async () => {
                        if (!selected) return
                        try {
                          const resp = await fetch(selected.url)
                          const blob = await resp.blob()
                          const fd = new FormData()
                          fd.append('stlFile', blob, selected.fileName)
                          const result = await supportV2Api.autoOrient(fd)
                          if (result.orientations.length > 0) {
                            const best = result.orientations[0]
                            // Convert quaternion to Euler angles (ZYX order)
                            const { rotationX: qx, rotationY: qy, rotationZ: qz, rotationW: qw } = best
                            const sinrCosp = 2 * (qw * qx + qy * qz)
                            const cosrCosp = 1 - 2 * (qx * qx + qy * qy)
                            const eulerX = Math.atan2(sinrCosp, cosrCosp) * 180 / Math.PI
                            const sinp = 2 * (qw * qy - qz * qx)
                            const eulerY = (Math.abs(sinp) >= 1 ? Math.sign(sinp) * 90 : Math.asin(sinp) * 180 / Math.PI)
                            const sinyCosp = 2 * (qw * qz + qx * qy)
                            const cosyCosp = 1 - 2 * (qy * qy + qz * qz)
                            const eulerZ = Math.atan2(sinyCosp, cosyCosp) * 180 / Math.PI

                            if (selected.id) {
                              pushUndo(selected.id, selected.transform)
                              handleTransformChange(selected.id, {
                                ...selected.transform,
                                rotX: eulerX, rotY: eulerY, rotZ: eulerZ,
                              })
                            }
                            console.log(`[AutoOrient] ${best.description}: overhang=${best.overhangAreaMm2.toFixed(0)}mm², supports≈${best.estimatedSupports}`)
                          }
                        } catch (err) { console.error('Auto-orient failed:', err) }
                      }}
                        className="flex-1 text-[9px] py-1.5 rounded bg-violet-900/30 text-violet-400 hover:bg-violet-900/50 transition">
                        Auto Orient
                      </button>
                      <button onClick={async () => {
                        if (!selected) return
                        try {
                          const resp = await fetch(selected.url)
                          const blob = await resp.blob()
                          const fd = new FormData()
                          fd.append('stlFile', blob, selected.fileName)
                          const result = await supportV2Api.suggestDrainHoles(fd)
                          if (result.holes.length > 0) {
                            alert(`Found ${result.holes.length} resin trap(s):\n${result.holes.map(h => `• ${h.reason} (${h.trapVolumeMm3.toFixed(0)}mm³)`).join('\n')}`)
                          } else {
                            alert('No resin traps detected — no drain holes needed')
                          }
                        } catch (err) { console.error('Drain hole analysis failed:', err) }
                      }}
                        className="flex-1 text-[9px] py-1.5 rounded bg-cyan-900/30 text-cyan-400 hover:bg-cyan-900/50 transition">
                        Drain Holes
                      </button>
                    </div>

                    {/* Generate button */}
                    <button onClick={generateAutoSupports} disabled={generating}
                      className={`w-full mt-2 text-xs py-2 rounded-lg font-medium transition ${
                        generating ? 'bg-green-800 text-green-200 animate-pulse' : 'bg-green-600 hover:bg-green-500 text-white'
                      }`}>
                      {generating && generatingProgress ? generatingProgress : generating ? 'Generating...' : selectedPrep.generatedAt ? 'Regenerate Selected' : 'Generate Supports'}
                    </button>
                    {models.length > 1 && (
                      <button onClick={generateAllSupports} disabled={generating}
                        className={`w-full mt-1 text-xs py-1.5 rounded-lg font-medium transition ${
                          generating ? 'bg-teal-900 text-teal-300 animate-pulse' : 'bg-teal-700 hover:bg-teal-600 text-white'
                        }`}>
                        {generating && generatingProgress ? generatingProgress : 'Generate All Parts'}
                      </button>
                    )}
                    <p className="text-[8px] text-gray-600 mt-0.5 text-center">BVH-accelerated | Tree supports | Structural validation | Watertight mesh</p>

                    {/* Generated status */}
                    {selectedPrep.generatedAt && (
                      <div className="mt-2 space-y-1">
                        <div className="flex items-center justify-between text-[10px]">
                          <span className="text-green-400">{selectedPrep.advancedSupports.length > 0
                            ? selectedPrep.advancedSupports.filter(s => s.type !== 'tree-trunk' && s.type !== 'tree-subtruck').length
                            : selectedPrep.autoSupports.length} supports</span>
                          {selectedPrep.raft && <span className="text-blue-400">Raft ({selectedPrep.raft.type})</span>}
                          {selectedPrep.skirt && <span className="text-cyan-400">Skirt ({selectedPrep.skirt.layers}L)</span>}
                        </div>
                        {/* V2 Engine Stats */}
                        {selectedPrep.v2Stats && (
                          <div className="mt-1 space-y-0.5 text-[9px]">
                            <div className="flex justify-between">
                              <span className="text-gray-500">Volume</span>
                              <span className="text-gray-300">{selectedPrep.v2Stats.volumeMl.toFixed(2)} ml ({selectedPrep.v2Stats.weightG.toFixed(1)}g, ~${selectedPrep.v2Stats.costUsd.toFixed(2)})</span>
                            </div>
                            <div className="flex justify-between">
                              <span className="text-gray-500">Mesh</span>
                              <span className="text-gray-300">{(selectedPrep.v2Stats.meshFaces/1000).toFixed(0)}k faces</span>
                            </div>
                            <div className="flex justify-between">
                              <span className="text-gray-500">Buckling</span>
                              <span className={selectedPrep.v2Stats.bucklingFail === 0 ? 'text-green-400' : 'text-amber-400'}>
                                {selectedPrep.v2Stats.bucklingPass} pass / {selectedPrep.v2Stats.bucklingFail} fail
                              </span>
                            </div>
                            <div className="flex justify-between">
                              <span className="text-gray-500">Safety Factor</span>
                              <span className={selectedPrep.v2Stats.safetyFactor >= 2 ? 'text-green-400' : selectedPrep.v2Stats.safetyFactor >= 1 ? 'text-amber-400' : 'text-red-400'}>
                                {selectedPrep.v2Stats.safetyFactor.toFixed(1)}x min
                              </span>
                            </div>
                            <div className="flex justify-between">
                              <span className="text-gray-500">Time</span>
                              <span className="text-gray-400">{selectedPrep.v2Stats.elapsedMs}ms (V2)</span>
                            </div>
                            {/* D5: Coverage analyze */}
                            <div className="flex justify-between">
                              <span className="text-gray-500">Coverage</span>
                              <span className={selectedPrep.v2Stats.coverageOk >= selectedPrep.v2Stats.coverageTotal
                                ? 'text-green-400' : selectedPrep.v2Stats.coverageOk > 0 ? 'text-amber-400' : 'text-red-400'}>
                                {selectedPrep.v2Stats.coverageOk}/{selectedPrep.v2Stats.coverageTotal} regions
                              </span>
                            </div>
                            {selectedPrep.v2Stats.coverageOk < selectedPrep.v2Stats.coverageTotal && (
                              <div className="mt-0.5 px-1 py-0.5 rounded bg-red-900/20 border border-red-800/30">
                                <span className="text-[8px] text-red-400">
                                  {selectedPrep.v2Stats.coverageTotal - selectedPrep.v2Stats.coverageOk} uncovered region(s) — add manual supports or increase density
                                </span>
                              </div>
                            )}
                            {selectedPrep.v2Stats.collisions > 0 && (
                              <div className="mt-0.5 px-1 py-0.5 rounded bg-amber-900/20 border border-amber-800/30">
                                <span className="text-[8px] text-amber-400">
                                  {selectedPrep.v2Stats.collisions} collision(s) detected
                                </span>
                              </div>
                            )}
                          </div>
                        )}
                        {selectedPrep.stale && (
                          <p className="text-[9px] text-amber-400">Stale — model moved, regenerate needed</p>
                        )}
                        <div className="flex gap-1">
                          <button onClick={toggleLock}
                            className={`flex-1 text-[9px] py-1 rounded transition ${
                              selectedPrep.locked ? 'bg-amber-900/30 text-amber-400' : 'bg-gray-800 text-gray-400'
                            }`}>
                            {selectedPrep.locked ? 'Unlock Movement' : 'Lock Movement'}
                          </button>
                          <button onClick={async () => {
                            if (!selected) return
                            try {
                              const resp = await fetch(selected.url)
                              const blob = await resp.blob()
                              const fd = new FormData()
                              fd.append('stlFile', blob, selected.fileName)
                              fd.append('density', String(autoSupportConfig.density))
                              const stlBlob = await supportV2Api.downloadMesh(fd)
                              const url = URL.createObjectURL(stlBlob)
                              const a = document.createElement('a')
                              a.href = url; a.download = 'supports.stl'; a.click()
                              URL.revokeObjectURL(url)
                            } catch (err) { console.error('STL export failed:', err) }
                          }}
                            className="flex-1 text-[9px] py-1 rounded bg-teal-900/20 text-teal-400 hover:bg-teal-900/30 transition">
                            Export STL
                          </button>
                          <button onClick={async () => {
                            if (!selected) return
                            try {
                              const resp = await fetch(selected.url)
                              const blob = await resp.blob()
                              const fd = new FormData()
                              fd.append('stlFile', blob, selected.fileName)
                              fd.append('density', String(autoSupportConfig.density))
                              const zipBlob = await supportV2Api.exportCombined(fd)
                              const url = URL.createObjectURL(zipBlob)
                              const a = document.createElement('a')
                              a.href = url; a.download = 'model_with_supports.zip'; a.click()
                              URL.revokeObjectURL(url)
                            } catch (err) { console.error('ZIP export failed:', err) }
                          }}
                            className="flex-1 text-[9px] py-1 rounded bg-indigo-900/20 text-indigo-400 hover:bg-indigo-900/30 transition">
                            Export ZIP
                          </button>
                          <button onClick={clearPrep}
                            className="flex-1 text-[9px] py-1 rounded bg-red-900/20 text-red-400 hover:bg-red-900/30 transition">
                            Clear All
                          </button>
                        </div>
                      </div>
                    )}
                  </div>

                  {/* Manual Support Editing */}
                  <div className="bg-gray-800/50 rounded-xl p-3">
                    <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-2">Manual Edit</h3>
                    <div className="grid grid-cols-3 gap-1 mb-2">
                      {([['none', 'View'], ['add', '+ Add'], ['delete', '- Del']] as const).map(([mode, label]) => (
                        <button key={mode} onClick={() => enterSupportEditMode(mode as SupportEditMode)}
                          className={`text-[10px] py-1 rounded border transition ${
                            supportEditMode === mode ? 'font-medium bg-indigo-600/20 text-indigo-300 border-indigo-600/30' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                          }`}>{label}</button>
                      ))}
                    </div>
                    <div className="grid grid-cols-2 gap-1 mb-2">
                      <button onClick={() => enterSupportEditMode(supportEditMode === 'paint-enforcer' ? 'none' : 'paint-enforcer')}
                        className={`text-[10px] py-1 rounded border transition ${supportEditMode === 'paint-enforcer' ? 'text-blue-400 bg-blue-900/30 border-blue-700/30 font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'}`}>Enforcer</button>
                      <button onClick={() => enterSupportEditMode(supportEditMode === 'paint-blocker' ? 'none' : 'paint-blocker')}
                        className={`text-[10px] py-1 rounded border transition ${supportEditMode === 'paint-blocker' ? 'text-orange-400 bg-orange-900/30 border-orange-700/30 font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'}`}>Blocker</button>
                    </div>
                    {supportEditMode !== 'none' && <p className={`text-[9px] px-2 py-1 rounded ${supportEditMode === 'add' ? 'bg-green-900/20 text-green-400' : supportEditMode === 'delete' ? 'bg-red-900/20 text-red-400' : supportEditMode === 'paint-enforcer' ? 'bg-blue-900/20 text-blue-400' : 'bg-orange-900/20 text-orange-400'}`}>
                      {supportEditMode === 'add' ? 'Click on model surface to add support' : supportEditMode === 'delete' ? 'Click a support point to remove it' : supportEditMode === 'paint-enforcer' ? 'Click on model to paint enforcer' : 'Click on model to paint blocker'}
                    </p>}
                    {supportEditMode === 'add' && <div className="mt-2"><span className="text-[9px] text-gray-500 block mb-1">Tip Type</span><div className="grid grid-cols-3 gap-1">{(['light', 'medium', 'heavy'] as const).map(t => (<button key={t} onClick={() => setSupportTipType(t)} className={`text-[10px] py-1 rounded capitalize ${supportTipType === t ? 'bg-indigo-600 text-white' : 'bg-gray-800 text-gray-400'}`}>{t}</button>))}</div></div>}
                    {(supportEditMode === 'paint-enforcer' || supportEditMode === 'paint-blocker') && <div className="mt-2"><label className="flex items-center justify-between text-[10px]"><span className="text-gray-500">Brush {supportBrushSize}mm</span></label><input type="range" min={1} max={20} value={supportBrushSize} onChange={e => setSupportBrushSize(+e.target.value)} className="w-full h-1 mt-1 accent-indigo-500" /></div>}
                  </div>
                  {/* Support points list */}
                  {selectedSupportData.points.length > 0 && <div className="bg-gray-800/50 rounded-xl p-3"><span className="text-[9px] text-gray-500 block mb-1">Points ({selectedSupportData.points.length})</span><ul className="space-y-0.5 max-h-32 overflow-y-auto">{selectedSupportData.points.map(p => (<li key={p.id} className="flex items-center justify-between text-[10px] px-1.5 py-0.5 rounded bg-gray-800/50"><span className="text-gray-400"><span className={`inline-block w-1.5 h-1.5 rounded-full mr-1 ${p.type === 'light' ? 'bg-green-400' : p.type === 'medium' ? 'bg-yellow-400' : 'bg-red-400'}`} />({p.x.toFixed(1)}, {p.y.toFixed(1)}, {p.z.toFixed(1)})</span><div className="flex gap-1"><select value={p.type} onChange={e => updateSupportPoint(p.id, { type: e.target.value as any })} className="bg-gray-800 border-none text-[9px] text-gray-400 px-1 py-0 rounded"><option value="light">L</option><option value="medium">M</option><option value="heavy">H</option></select><button onClick={() => deleteSupportPoint(p.id)} className="text-red-400 hover:text-red-300">x</button></div></li>))}</ul></div>}
                  {/* Painted regions */}
                  {selectedSupportData.paintedRegions.length > 0 && <div className="bg-gray-800/50 rounded-xl p-3"><span className="text-[9px] text-gray-500 block mb-1">Regions ({selectedSupportData.paintedRegions.length})</span><ul className="space-y-0.5 max-h-20 overflow-y-auto">{selectedSupportData.paintedRegions.map(r => (<li key={r.id} className="flex items-center justify-between text-[10px] px-1.5 py-0.5 rounded bg-gray-800/50"><span className={r.mode === 'enforcer' ? 'text-blue-400' : 'text-orange-400'}>{r.mode} ({r.radiusMm}mm)</span><button onClick={() => deletePaintedRegion(r.id)} className="text-red-400 hover:text-red-300">x</button></li>))}</ul></div>}
                  {hasSupportEdits && <div className="flex gap-1">{selectedSupportData.paintedRegions.some(r => r.mode === 'enforcer') && <button onClick={() => clearPaintedRegions('enforcer')} className="flex-1 text-[9px] py-1 rounded bg-blue-900/20 text-blue-400">Clear Enforcers</button>}{selectedSupportData.paintedRegions.some(r => r.mode === 'blocker') && <button onClick={() => clearPaintedRegions('blocker')} className="flex-1 text-[9px] py-1 rounded bg-orange-900/20 text-orange-400">Clear Blockers</button>}<button onClick={clearAllManualSupports} className="flex-1 text-[9px] py-1 rounded bg-red-900/20 text-red-400">Clear All</button></div>}
                </>) : <p className="text-[10px] text-gray-600 text-center py-4">Select an object to edit supports</p>}
              </div>
            ) : /* Model tab (default) */ (
              <div className="flex-1 overflow-y-auto p-3 space-y-3">
                {/* Model list */}
                <div className="bg-gray-800/50 rounded-xl p-3">
                  <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-2">Models ({models.length})</h3>
                  <ul className="space-y-0.5 max-h-40 overflow-y-auto">
                    {models.map(m => {
                      const isOverlap = overlaps.has(m.id)
                      const isOob = outOfBoundsIds.has(m.id)
                      const hasOvr = Object.values(m.overrideSettings).some(v => v !== null)
                      const hasSup = m.manualSupports.points.length > 0 || m.manualSupports.paintedRegions.length > 0
                      return (<li key={m.id} onClick={() => { setSelectedId(m.id); _savedSelectedId = m.id }}
                        className={`flex items-center gap-2 text-xs px-2 py-1.5 rounded cursor-pointer truncate transition ${m.id === selectedId ? 'bg-indigo-900/40 text-indigo-300' : 'text-gray-400 hover:bg-gray-800'}`}>
                        <span className="truncate flex-1">{m.fileName}</span>
                        {hasSup && <span className="text-[9px] text-green-400">SUP</span>}
                        {hasOvr && <span className="text-[9px] text-indigo-400">OVR</span>}
                        {m.meshValidation.status === 'error' && <span className="text-red-400 text-[9px]">ERR</span>}
                        {isOverlap && <span className="text-amber-400 text-[9px]">OVL</span>}
                        {isOob && <span className="text-red-400 text-[9px]">OOB</span>}
                      </li>)
                    })}
                  </ul>
                </div>

                {/* Selected model info + transforms + hollowing + overrides */}
                {selected && (<>
                  <div className="bg-gray-800/50 rounded-xl p-3">
                    <div className="flex items-center justify-between mb-2">
                      <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider">Selected</h3>
                      <div className="flex gap-1">
                        <button onClick={() => duplicateModel(selected.id)} className="text-[10px] px-2 py-0.5 rounded bg-gray-800 hover:bg-gray-700 text-gray-400">Dup</button>
                        <button onClick={() => deleteModel(selected.id)} className="text-[10px] px-2 py-0.5 rounded bg-red-900/30 hover:bg-red-900/50 text-red-400">Del</button>
                      </div>
                    </div>
                    <p className="text-xs text-gray-300 truncate">{selected.fileName}</p>
                    {selected.size && <p className="text-[10px] text-gray-500 mt-1">{selected.size.x} x {selected.size.y} x {selected.size.z} mm</p>}
                    {outOfBoundsIds.has(selected.id) && <p className="text-[10px] text-red-400 mt-1">Out of build volume</p>}
                    {overlaps.has(selected.id) && <p className="text-[10px] text-amber-400 mt-1">Overlapping</p>}
                    {selected.meshValidation.status !== 'pending' && <div className={`mt-2 text-[9px] px-2 py-1 rounded ${selected.meshValidation.status === 'valid' ? 'bg-green-900/20 text-green-400' : selected.meshValidation.status === 'warning' ? 'bg-amber-900/20 text-amber-400' : 'bg-red-900/20 text-red-400'}`}>{selected.meshValidation.status === 'valid' ? 'Mesh OK' : selected.meshValidation.status === 'warning' ? 'Warnings' : 'Errors'} — {selected.meshValidation.triangleCount.toLocaleString()} tris</div>}
                    {selected.meshValidation.status === 'pending' && <p className="text-[9px] text-gray-600 mt-1">Validating...</p>}
                  </div>

                  {/* Hollowing */}
                  <div className={`bg-gray-800/50 rounded-xl p-3 ${selectedHollow.enabled ? 'ring-1 ring-violet-700/40' : ''}`}>
                  <div className="flex items-center justify-between mb-2">
                    <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider">Hollowing</h3>
                    <button onClick={() => setHollowEnabled(selected.id, !selectedHollow.enabled)}
                      className={`text-[10px] px-2 py-0.5 rounded transition ${
                        selectedHollow.enabled
                          ? 'bg-violet-600/20 text-violet-400 border border-violet-600/30'
                          : 'bg-gray-800 text-gray-500 hover:bg-gray-700'
                      }`}>
                      {selectedHollow.enabled ? 'ON' : 'OFF'}
                    </button>
                  </div>

                  {selectedHollow.enabled && (
                    <div className="space-y-2">
                      <label className="flex items-center justify-between text-[10px]">
                        <span className="text-gray-500">Wall Thickness</span>
                        <div className="flex items-center gap-1">
                          <input type="number" value={selectedHollow.wallThicknessMm} step={0.1} min={0.3} max={10}
                            onChange={e => setHollowWallThickness(selected.id, +e.target.value)}
                            className="w-14 bg-gray-800 border border-gray-700 rounded px-1.5 py-0.5 text-[11px] text-gray-200 text-right" />
                          <span className="text-gray-600 text-[9px]">mm</span>
                        </div>
                      </label>

                      <input type="range" min={0.3} max={5} step={0.1} value={selectedHollow.wallThicknessMm}
                        onChange={e => setHollowWallThickness(selected.id, +e.target.value)}
                        className="w-full h-1 accent-violet-500" />

                      {/* Status indicator */}
                      <div className="flex items-center justify-between">
                        <span className={`text-[9px] ${
                          selectedHollow.stale ? 'text-amber-400' :
                          selectedHollow.appliedAt ? 'text-green-400' : 'text-gray-600'
                        }`}>
                          {selectedHollow.stale ? 'Stale — re-apply needed' :
                           selectedHollow.appliedAt ? 'Applied' : 'Not yet applied'}
                        </span>
                        <button onClick={() => applyHollow(selected.id)}
                          className={`text-[10px] px-2 py-0.5 rounded transition ${
                            selectedHollow.stale || !selectedHollow.appliedAt
                              ? 'bg-violet-600 hover:bg-violet-500 text-white'
                              : 'bg-gray-800 text-gray-500'
                          }`}>
                          {selectedHollow.stale ? 'Re-apply' : selectedHollow.appliedAt ? 'Applied' : 'Apply'}
                        </button>
                      </div>

                      {/* Volume estimate */}
                      {selected.size && (
                        <p className="text-[9px] text-gray-600">
                          Est. material saved: ~{Math.round(
                            (1 - (1 - selectedHollow.wallThicknessMm / Math.min(selected.size.x, selected.size.y, selected.size.z) * 2) ** 3) * 100
                          )}% reduction
                        </p>
                      )}
                    </div>
                  )}
                </div>

                {/* Infill (only when hollowing is enabled) */}
                {selectedHollow.enabled && (
                  <div className="bg-gray-800/50 rounded-xl p-3">
                    <h3 className="text-[9px] font-semibold text-gray-500 uppercase mb-2">Internal Structure</h3>
                    <select value={autoSupportConfig.raftType} onChange={() => {}}
                      className="w-full bg-gray-800 border border-gray-700 rounded px-1.5 py-1 text-[10px] text-gray-200 mb-2"
                      title="Infill pattern for hollow interior">
                      <option value="none">None (fully hollow)</option>
                      <option value="honeycomb">Honeycomb</option>
                      <option value="grid">Grid</option>
                      <option value="triangular">Triangular</option>
                      <option value="gyroid">Gyroid</option>
                    </select>
                    <p className="text-[8px] text-gray-600">Infill adds internal structure inside hollowed parts for strength</p>
                  </div>
                )}

                {/* Drain Holes */}
                <div className="bg-gray-800/50 rounded-xl p-3">
                  <h3 className="text-[9px] font-semibold text-gray-500 uppercase mb-2">Drain Holes</h3>
                  <p className="text-[8px] text-gray-600 mb-2">Allow uncured resin to escape from hollow interiors. Improves wash + post-cure.</p>
                  <div className="flex gap-1">
                    <button className="flex-1 text-[9px] py-1 rounded bg-indigo-600/20 text-indigo-400 hover:bg-indigo-600/30 transition">
                      + Add Hole
                    </button>
                    <button className="flex-1 text-[9px] py-1 rounded bg-gray-800 text-gray-400 hover:bg-gray-700 transition">
                      Auto-Suggest
                    </button>
                  </div>
                </div>

                {/* Position */}
                <div className="bg-gray-900 rounded-xl border border-gray-800 p-3">
                  <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-2">Position</h3>
                  <div className="grid grid-cols-3 gap-1.5">
                    {(['x', 'y', 'z'] as const).map(axis => (
                      <label key={axis} className="text-[10px] text-gray-500">
                        {axis.toUpperCase()}
                        <input type="number" step="0.1" value={selected.transform[axis]}
                          onChange={e => {
                            pushUndo(selected.id, selected.transform)
                            handleTransformChange(selected.id, { ...selected.transform, [axis]: +e.target.value })
                          }}
                          className="w-full mt-0.5 bg-gray-800 border border-gray-700 rounded px-2 py-1 text-xs text-gray-200" />
                      </label>
                    ))}
                  </div>
                </div>

                {/* Rotation */}
                <div className="bg-gray-900 rounded-xl border border-gray-800 p-3">
                  <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-2">Rotation</h3>
                  <div className="grid grid-cols-3 gap-1.5">
                    {(['rotX', 'rotY', 'rotZ'] as const).map(axis => (
                      <label key={axis} className="text-[10px] text-gray-500">
                        {axis.replace('rot', '')}
                        <input type="number" step="1" value={selected.transform[axis]}
                          onChange={e => {
                            pushUndo(selected.id, selected.transform)
                            handleTransformChange(selected.id, { ...selected.transform, [axis]: +e.target.value })
                          }}
                          className="w-full mt-0.5 bg-gray-800 border border-gray-700 rounded px-2 py-1 text-xs text-gray-200" />
                      </label>
                    ))}
                  </div>
                </div>

                {/* Scale */}
                <div className="bg-gray-900 rounded-xl border border-gray-800 p-3">
                  <div className="flex items-center justify-between mb-2">
                    <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider">Scale</h3>
                    <label className="flex items-center gap-1 text-[10px] text-gray-500 cursor-pointer">
                      <input type="checkbox" checked={uniformScale} onChange={e => setUniformScale(e.target.checked)}
                        className="rounded border-gray-600 bg-gray-800 w-3 h-3" />
                      Uniform
                    </label>
                  </div>
                  <div className="grid grid-cols-3 gap-1.5">
                    {(['scaleX', 'scaleY', 'scaleZ'] as const).map(axis => (
                      <label key={axis} className="text-[10px] text-gray-500">
                        {axis.replace('scale', '')}
                        <input type="number" step="0.01" min="0.01" value={selected.transform[axis]}
                          onChange={e => {
                            pushUndo(selected.id, selected.transform)
                            const val = +e.target.value
                            if (uniformScale) {
                              handleTransformChange(selected.id, { ...selected.transform, scaleX: val, scaleY: val, scaleZ: val })
                            } else {
                              handleTransformChange(selected.id, { ...selected.transform, [axis]: val })
                            }
                          }}
                          className="w-full mt-0.5 bg-gray-800 border border-gray-700 rounded px-2 py-1 text-xs text-gray-200" />
                      </label>
                    ))}
                  </div>
                </div>

                {/* Per-object settings */}
                <div className="bg-gray-900 rounded-xl border border-gray-800 p-3">
                  <div className="flex items-center justify-between mb-2">
                    <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider">Object Overrides</h3>
                    <button onClick={() => setShowSettings(!showSettings)}
                      className="text-[10px] text-indigo-400 hover:text-indigo-300 transition">
                      {showSettings ? 'Collapse' : `Expand${hasOverrides ? ` (${Object.values(selected.overrideSettings).filter(v => v !== null).length})` : ''}`}
                    </button>
                  </div>

                  {!showSettings && hasOverrides && (
                    <div className="flex flex-wrap gap-1">
                      {selected.overrideSettings.supportEnabled !== null && <OvrBadge label="Support" />}
                      {selected.overrideSettings.supportDensity !== null && <OvrBadge label="Density" />}
                      {selected.overrideSettings.supportType !== null && <OvrBadge label="Type" />}
                      {selected.overrideSettings.exposureMs !== null && <OvrBadge label="Exposure" />}
                      {selected.overrideSettings.hollowingEnabled !== null && <OvrBadge label="Hollow" />}
                    </div>
                  )}

                  {showSettings && (
                    <div className="space-y-3">
                      <p className="text-[9px] text-gray-600">
                        Blank/unchecked = use global. Set a value = override for this part only.
                      </p>

                      {/* Exposure section */}
                      <div>
                        <div className="text-[9px] font-semibold text-amber-400/70 uppercase tracking-wider mb-1">Exposure</div>
                        <div className="space-y-1">
                          <SettingsField label="Normal (ms)" value={selected.overrideSettings.exposureMs}
                            onChange={v => patchSettings('exposureMs', v)} placeholder="Global" step={100} min={100} />
                          <SettingsField label="Bottom (ms)" value={selected.overrideSettings.bottomExposureMs}
                            onChange={v => patchSettings('bottomExposureMs', v)} placeholder="Global" step={1000} min={1000} />
                          <SettingsField label="Lift Dist (mm)" value={selected.overrideSettings.liftDistanceMm}
                            onChange={v => patchSettings('liftDistanceMm', v)} placeholder="Global" step={0.5} min={0} />
                          <SettingsField label="Lift Speed" value={selected.overrideSettings.liftSpeedMmPerMin}
                            onChange={v => patchSettings('liftSpeedMmPerMin', v)} placeholder="Global" step={10} min={1} />
                        </div>
                      </div>

                      {/* Support section */}
                      <div>
                        <div className="text-[9px] font-semibold text-green-400/70 uppercase tracking-wider mb-1">Supports</div>
                        <div className="space-y-1">
                          <BoolField label="Enable Supports" value={selected.overrideSettings.supportEnabled}
                            onChange={v => patchSettings('supportEnabled', v)} />
                          <SelectField label="Type" value={selected.overrideSettings.supportType}
                            onChange={v => patchSettings('supportType', v)}
                            options={[{ v: 'normal', l: 'Normal' }, { v: 'tree', l: 'Tree' }]} />
                          <SelectField label="Placement" value={selected.overrideSettings.supportPlacement}
                            onChange={v => patchSettings('supportPlacement', v)}
                            options={[{ v: 'buildplate', l: 'Build Plate' }, { v: 'everywhere', l: 'Everywhere' }]} />
                          <SettingsField label="Density" value={selected.overrideSettings.supportDensity}
                            onChange={v => patchSettings('supportDensity', v)} placeholder="Global" step={0.05} min={0.05} max={1} />
                          <SettingsField label="Overhang (deg)" value={selected.overrideSettings.supportOverhangAngleDeg}
                            onChange={v => patchSettings('supportOverhangAngleDeg', v)} placeholder="Global" step={1} min={0} max={90} />
                          <SettingsField label="XY Dist (mm)" value={selected.overrideSettings.supportXYDistanceMm}
                            onChange={v => patchSettings('supportXYDistanceMm', v)} placeholder="Global" step={0.05} min={0} />
                          <SettingsField label="Z Dist (mm)" value={selected.overrideSettings.supportZDistanceMm}
                            onChange={v => patchSettings('supportZDistanceMm', v)} placeholder="Global" step={0.05} min={0} />
                          <BoolField label="Interface" value={selected.overrideSettings.supportInterfaceEnabled}
                            onChange={v => patchSettings('supportInterfaceEnabled', v)} />
                        </div>
                      </div>

                      {/* Hollowing section */}
                      <div>
                        <div className="text-[9px] font-semibold text-violet-400/70 uppercase tracking-wider mb-1">Hollowing</div>
                        <div className="space-y-1">
                          <BoolField label="Enable Hollowing" value={selected.overrideSettings.hollowingEnabled}
                            onChange={v => patchSettings('hollowingEnabled', v)} />
                          <SettingsField label="Wall (mm)" value={selected.overrideSettings.hollowWallThicknessMm}
                            onChange={v => patchSettings('hollowWallThicknessMm', v)} placeholder="Global" step={0.1} min={0.3} />
                        </div>
                      </div>

                      {hasOverrides && (
                        <button onClick={resetSettings}
                          className="w-full text-[10px] text-amber-400 hover:text-amber-300 transition mt-1 py-1 rounded bg-amber-900/20 hover:bg-amber-900/30">
                          Reset all to global defaults
                        </button>
                      )}
                    </div>
                  )}
                </div>

                {/* Manual Support Editing */}
                <div className="bg-gray-900 rounded-xl border border-gray-800 p-3">
                  <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-2">Support Editing</h3>

                  {/* Mode switcher */}
                  <div className="grid grid-cols-3 gap-1 mb-3">
                    {([
                      ['none', 'View', 'text-gray-400 bg-gray-800'],
                      ['add', '+ Add', 'text-green-400 bg-green-900/30 border-green-700/30'],
                      ['delete', '- Del', 'text-red-400 bg-red-900/30 border-red-700/30'],
                    ] as const).map(([mode, label, cls]) => (
                      <button key={mode} onClick={() => enterSupportEditMode(mode as SupportEditMode)}
                        className={`text-[10px] py-1 rounded border transition ${
                          supportEditMode === mode ? cls + ' border font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                        }`}>{label}</button>
                    ))}
                  </div>
                  <div className="grid grid-cols-2 gap-1 mb-3">
                    <button onClick={() => enterSupportEditMode(supportEditMode === 'paint-enforcer' ? 'none' : 'paint-enforcer')}
                      className={`text-[10px] py-1 rounded border transition ${
                        supportEditMode === 'paint-enforcer' ? 'text-blue-400 bg-blue-900/30 border-blue-700/30 font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                      }`}>Enforcer</button>
                    <button onClick={() => enterSupportEditMode(supportEditMode === 'paint-blocker' ? 'none' : 'paint-blocker')}
                      className={`text-[10px] py-1 rounded border transition ${
                        supportEditMode === 'paint-blocker' ? 'text-orange-400 bg-orange-900/30 border-orange-700/30 font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                      }`}>Blocker</button>
                  </div>

                  {/* Active mode indicator */}
                  {supportEditMode !== 'none' && (
                    <div className={`text-[10px] px-2 py-1.5 rounded mb-3 ${
                      supportEditMode === 'add' ? 'bg-green-900/20 text-green-400' :
                      supportEditMode === 'delete' ? 'bg-red-900/20 text-red-400' :
                      supportEditMode === 'paint-enforcer' ? 'bg-blue-900/20 text-blue-400' :
                      'bg-orange-900/20 text-orange-400'
                    }`}>
                      {supportEditMode === 'add' && 'Click on the model surface to add a support point'}
                      {supportEditMode === 'delete' && 'Click a support point to remove it'}
                      {supportEditMode === 'paint-enforcer' && 'Click on the model to paint enforcer regions'}
                      {supportEditMode === 'paint-blocker' && 'Click on the model to paint blocker regions'}
                    </div>
                  )}

                  {/* Tip type (for add mode) */}
                  {supportEditMode === 'add' && (
                    <div className="mb-3">
                      <span className="text-[9px] text-gray-500 block mb-1">Tip Type</span>
                      <div className="grid grid-cols-3 gap-1">
                        {(['light', 'medium', 'heavy'] as const).map(t => (
                          <button key={t} onClick={() => setSupportTipType(t)}
                            className={`text-[10px] py-1 rounded transition capitalize ${
                              supportTipType === t ? 'bg-indigo-600 text-white' : 'bg-gray-800 text-gray-400 hover:bg-gray-700'
                            }`}>{t}</button>
                        ))}
                      </div>
                    </div>
                  )}

                  {/* Brush size (for paint modes) */}
                  {(supportEditMode === 'paint-enforcer' || supportEditMode === 'paint-blocker') && (
                    <div className="mb-3">
                      <label className="flex items-center justify-between text-[10px]">
                        <span className="text-gray-500">Brush Size</span>
                        <span className="text-gray-400">{supportBrushSize} mm</span>
                      </label>
                      <input type="range" min={1} max={20} value={supportBrushSize}
                        onChange={e => setSupportBrushSize(+e.target.value)}
                        className="w-full h-1 mt-1 accent-indigo-500" />
                    </div>
                  )}

                  {/* Viewport interaction hint */}

                  {/* Support points list */}
                  {selectedSupportData.points.length > 0 && (
                    <div className="mb-2">
                      <span className="text-[9px] text-gray-500 block mb-1">
                        Support Points ({selectedSupportData.points.length})
                      </span>
                      <ul className="space-y-0.5 max-h-40 overflow-y-auto">
                        {selectedSupportData.points.map(p => (
                          <li key={p.id}
                            onClick={() => setSelectedManualSupportId(selectedManualSupportId === p.id ? null : p.id)}
                            className={`flex flex-col text-[10px] px-1.5 py-0.5 rounded cursor-pointer transition-colors
                              ${selectedManualSupportId === p.id ? 'bg-teal-900/40 ring-1 ring-teal-500/50' : 'bg-gray-800/50 hover:bg-gray-700/50'}`}>
                            <span className="text-gray-400 truncate">
                              <span className={`inline-block w-1.5 h-1.5 rounded-full mr-1 ${
                                p.engineStatus === 'error' ? 'bg-fuchsia-500' :
                                p.engineStatus === 'collision' ? 'bg-red-400' :
                                p.engineStatus === 'uncoverable' ? 'bg-red-600' :
                                p.engineStatus === 'routed' ? 'bg-teal-400' :
                                p.engineStatus === 'bundled' ? 'bg-green-400' :
                                p.type === 'light' ? 'bg-green-400' : p.type === 'medium' ? 'bg-yellow-400' : 'bg-red-400'
                              }`} />
                              ({p.x.toFixed(1)}, {p.y.toFixed(1)}, {p.z.toFixed(1)})
                              {p.provisional && <span className="text-gray-500 ml-1 italic">preview</span>}
                              {!p.provisional && p.changeReasons && p.changeReasons.length > 0 && (
                                <span className="text-amber-400 ml-1" title={`delta=${(p.geometryDelta ?? 0).toFixed(3)} | ${p.changeReasons.join(', ')}`}>{p.changeReasons[0]}</span>
                              )}
                              {p.engineStatus === 'error' && <span className="text-fuchsia-400 ml-1">failed</span>}
                              {p.engineStatus === 'collision' && <span className="text-red-400 ml-1">collision</span>}
                              {(p.finalStatus === 'uncoverable' || p.engineStatus === 'uncoverable') && <span className="text-red-400 ml-1">no path</span>}
                            </span>
                            <div className="flex gap-1">
                              {p.engineStatus === 'error' && (
                                <button onClick={() => retrySupportPoint(p.id)}
                                  className="text-fuchsia-400 hover:text-fuchsia-300 transition text-[9px]">retry</button>
                              )}
                              <select value={p.type} onChange={e => updateSupportPoint(p.id, { type: e.target.value as any })}
                                className="bg-gray-800 border-none text-[9px] text-gray-400 px-1 py-0 rounded">
                                <option value="light">Light</option>
                                <option value="medium">Med</option>
                                <option value="heavy">Heavy</option>
                              </select>
                              <button onClick={(e) => { e.stopPropagation(); deleteSupportPoint(p.id); if (selectedManualSupportId === p.id) setSelectedManualSupportId(null) }}
                                className="text-red-400 hover:text-red-300 transition">x</button>
                            </div>
                            {/* B3: Per-support size controls when selected */}
                            {selectedManualSupportId === p.id && (
                              <div className="mt-1 grid grid-cols-3 gap-1" onClick={e => e.stopPropagation()}>
                                <label className="text-[9px] text-gray-500">
                                  Tip
                                  <input type="number" step="0.1" min="0.1" max="5" value={p.tipDiameterMm}
                                    onChange={e => { updateSupportPoint(p.id, { tipDiameterMm: +e.target.value }); retrySupportPoint(p.id) }}
                                    className="w-full bg-gray-900 border border-gray-700 rounded px-1 py-0.5 text-[9px] text-gray-300" />
                                </label>
                                <label className="text-[9px] text-gray-500">
                                  Shaft
                                  <input type="number" step="0.1" min="0.2" max="10" value={p.shaftDiameterMm}
                                    onChange={e => { updateSupportPoint(p.id, { shaftDiameterMm: +e.target.value }); retrySupportPoint(p.id) }}
                                    className="w-full bg-gray-900 border border-gray-700 rounded px-1 py-0.5 text-[9px] text-gray-300" />
                                </label>
                                <label className="text-[9px] text-gray-500">
                                  Base
                                  <input type="number" step="0.5" min="0.5" max="20" value={p.baseDiameterMm}
                                    onChange={e => { updateSupportPoint(p.id, { baseDiameterMm: +e.target.value }); retrySupportPoint(p.id) }}
                                    className="w-full bg-gray-900 border border-gray-700 rounded px-1 py-0.5 text-[9px] text-gray-300" />
                                </label>
                              </div>
                            )}
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}

                  {/* Painted regions list */}
                  {selectedSupportData.paintedRegions.length > 0 && (
                    <div className="mb-2">
                      <span className="text-[9px] text-gray-500 block mb-1">
                        Painted Regions ({selectedSupportData.paintedRegions.length})
                      </span>
                      <ul className="space-y-0.5 max-h-20 overflow-y-auto">
                        {selectedSupportData.paintedRegions.map(r => (
                          <li key={r.id} className="flex items-center justify-between text-[10px] px-1.5 py-0.5 rounded bg-gray-800/50">
                            <span className={`${r.mode === 'enforcer' ? 'text-blue-400' : 'text-orange-400'}`}>
                              {r.mode === 'enforcer' ? 'Enforcer' : 'Blocker'} ({r.radiusMm}mm)
                            </span>
                            <button onClick={() => deletePaintedRegion(r.id)}
                              className="text-red-400 hover:text-red-300 transition">x</button>
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}

                  {/* Clear actions */}
                  {hasSupportEdits && (
                    <div className="flex gap-1">
                      {selectedSupportData.paintedRegions.some(r => r.mode === 'enforcer') && (
                        <button onClick={() => clearPaintedRegions('enforcer')}
                          className="flex-1 text-[9px] py-1 rounded bg-blue-900/20 text-blue-400 hover:bg-blue-900/30 transition">
                          Clear Enforcers
                        </button>
                      )}
                      {selectedSupportData.paintedRegions.some(r => r.mode === 'blocker') && (
                        <button onClick={() => clearPaintedRegions('blocker')}
                          className="flex-1 text-[9px] py-1 rounded bg-orange-900/20 text-orange-400 hover:bg-orange-900/30 transition">
                          Clear Blockers
                        </button>
                      )}
                      <button onClick={clearAllManualSupports}
                        className="flex-1 text-[9px] py-1 rounded bg-red-900/20 text-red-400 hover:bg-red-900/30 transition">
                        Clear All
                      </button>
                    </div>
                  )}

                  {/* Status */}
                  {!hasSupportEdits && supportEditMode === 'none' && (
                    <p className="text-[9px] text-gray-600 text-center py-1">No manual support edits</p>
                  )}
                </div>

                  {/* Transforms compact */}
                  <div className="bg-gray-800/50 rounded-xl p-3">
                    <h3 className="text-[9px] font-semibold text-gray-500 uppercase mb-1">Position / Rotation / Scale</h3>
                    <div className="grid grid-cols-3 gap-1">
                      {(['x','y','z'] as const).map(a => (
                        <input key={a} type="number" step="0.1" value={selected.transform[a]} title={`Position ${a.toUpperCase()}`}
                          onChange={e => { pushUndo(selected.id, selected.transform); handleTransformChange(selected.id, { ...selected.transform, [a]: +e.target.value }) }}
                          className="bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-center" />
                      ))}
                    </div>
                    <div className="grid grid-cols-3 gap-1 mt-1">
                      {(['rotX','rotY','rotZ'] as const).map(a => (
                        <input key={a} type="number" step="1" value={selected.transform[a]} title={`Rotation ${a.replace('rot','')}`}
                          onChange={e => { pushUndo(selected.id, selected.transform); handleTransformChange(selected.id, { ...selected.transform, [a]: +e.target.value }) }}
                          className="bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-center" />
                      ))}
                    </div>
                    <div className="grid grid-cols-3 gap-1 mt-1">
                      {(['scaleX','scaleY','scaleZ'] as const).map(a => (
                        <input key={a} type="number" step="0.01" min="0.01" value={selected.transform[a]} title={`Scale ${a.replace('scale','')}`}
                          onChange={e => { pushUndo(selected.id, selected.transform); const v=+e.target.value; if(uniformScale) handleTransformChange(selected.id,{...selected.transform,scaleX:v,scaleY:v,scaleZ:v}); else handleTransformChange(selected.id,{...selected.transform,[a]:v}) }}
                          className="bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-center" />
                      ))}
                    </div>
                  </div>

                  {/* Per-object overrides compact */}
                  <div className="bg-gray-800/50 rounded-xl p-3">
                    <div className="flex items-center justify-between mb-1">
                      <h3 className="text-[9px] font-semibold text-gray-500 uppercase">Object Overrides</h3>
                      <button onClick={() => setShowSettings(!showSettings)} className="text-[9px] text-indigo-400">
                        {showSettings ? 'Hide' : hasOverrides ? `Show (${Object.values(selected.overrideSettings).filter(v=>v!==null).length})` : 'Show'}
                      </button>
                    </div>
                    {showSettings && <div className="space-y-1 mt-2">
                      <SettingsField label="Exposure" value={selected.overrideSettings.exposureMs} onChange={v=>patchSettings('exposureMs',v)} step={100} min={100} />
                      <SettingsField label="Bottom Exp" value={selected.overrideSettings.bottomExposureMs} onChange={v=>patchSettings('bottomExposureMs',v)} step={1000} min={1000} />
                      <BoolField label="Supports" value={selected.overrideSettings.supportEnabled} onChange={v=>patchSettings('supportEnabled',v)} />
                      <SettingsField label="Density" value={selected.overrideSettings.supportDensity} onChange={v=>patchSettings('supportDensity',v)} step={0.05} min={0} max={1} />
                      <BoolField label="Hollow" value={selected.overrideSettings.hollowingEnabled} onChange={v=>patchSettings('hollowingEnabled',v)} />
                      <SettingsField label="Wall mm" value={selected.overrideSettings.hollowWallThicknessMm} onChange={v=>patchSettings('hollowWallThicknessMm',v)} step={0.1} min={0.3} />
                      {hasOverrides && <button onClick={resetSettings} className="w-full text-[9px] py-1 rounded bg-amber-900/20 text-amber-400 mt-1">Reset all</button>}
                    </div>}
                  </div>
                </>)}
              </div>
            )
          ) : (
            <div className="flex-1 flex items-center justify-center">
              <span className="text-[9px] text-gray-600 uppercase tracking-widest whitespace-nowrap"
                style={{ writingMode: 'vertical-rl', transform: 'rotate(180deg)' }}>
                Panel
              </span>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Per-object settings field helpers ──────────────────────────────────────────

function SettingsField({ label, value, onChange, placeholder, step, min, max }: {
  label: string; value: number | null; onChange: (v: number | null) => void
  placeholder?: string; step?: number; min?: number; max?: number
}) {
  return (
    <label className="flex items-center gap-1.5">
      <span className="text-[10px] text-gray-500 w-24 flex-shrink-0">{label}</span>
      <div className="flex-1 flex items-center gap-1">
        <input type="number" value={value ?? ''} placeholder={placeholder ?? 'Global'}
          step={step} min={min} max={max}
          onChange={e => onChange(e.target.value === '' ? null : +e.target.value)}
          className="w-full bg-gray-800 border border-gray-700 rounded px-1.5 py-0.5 text-[11px] text-gray-200 placeholder:text-gray-600" />
        {value !== null && (
          <button onClick={() => onChange(null)} className="text-gray-600 hover:text-gray-400 text-[10px] transition" title="Reset to global">x</button>
        )}
      </div>
    </label>
  )
}

function BoolField({ label, value, onChange }: {
  label: string; value: boolean | null; onChange: (v: boolean | null) => void
}) {
  // Three states: null (global), true, false
  return (
    <div className="flex items-center gap-1.5">
      <span className="text-[10px] text-gray-500 w-24 flex-shrink-0">{label}</span>
      <div className="flex items-center gap-1">
        <button onClick={() => onChange(value === null ? true : value ? false : null)}
          className={`text-[10px] px-2 py-0.5 rounded transition ${
            value === null ? 'bg-gray-800 text-gray-600' :
            value ? 'bg-green-900/40 text-green-400 border border-green-700/30' :
            'bg-red-900/30 text-red-400 border border-red-700/30'
          }`}>
          {value === null ? 'Global' : value ? 'Yes' : 'No'}
        </button>
        {value !== null && (
          <button onClick={() => onChange(null)} className="text-gray-600 hover:text-gray-400 text-[10px] transition" title="Reset to global">x</button>
        )}
      </div>
    </div>
  )
}

function SelectField({ label, value, onChange, options }: {
  label: string; value: string | null; onChange: (v: string | null) => void
  options: { v: string; l: string }[]
}) {
  return (
    <div className="flex items-center gap-1.5">
      <span className="text-[10px] text-gray-500 w-24 flex-shrink-0">{label}</span>
      <div className="flex items-center gap-1">
        <select value={value ?? '__global__'} onChange={e => onChange(e.target.value === '__global__' ? null : e.target.value)}
          className="bg-gray-800 border border-gray-700 rounded px-1.5 py-0.5 text-[11px] text-gray-200">
          <option value="__global__">Global</option>
          {options.map(o => <option key={o.v} value={o.v}>{o.l}</option>)}
        </select>
        {value !== null && (
          <button onClick={() => onChange(null)} className="text-gray-600 hover:text-gray-400 text-[10px] transition" title="Reset to global">x</button>
        )}
      </div>
    </div>
  )
}

function OvrBadge({ label }: { label: string }) {
  return <span className="text-[9px] px-1.5 py-0.5 rounded bg-indigo-900/30 text-indigo-400 border border-indigo-700/20">{label}</span>
}
