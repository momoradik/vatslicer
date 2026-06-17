import { useEffect, useRef, useCallback, forwardRef, useImperativeHandle, useState } from 'react'
import * as THREE from 'three'
;(window as any).__THREE = THREE // expose for headless raycast tests in commitOrientation
import { STLLoader } from 'three/examples/jsm/loaders/STLLoader.js'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { mergeVertices } from 'three/examples/jsm/utils/BufferGeometryUtils.js'
import { computeBoundsTree, disposeBoundsTree, acceleratedRaycast } from 'three-mesh-bvh'
import { GizmoManager } from './GizmoManager'

// Wire three-mesh-bvh into Three.js globally — accelerates ALL raycasts from O(n) to O(log n).
// This is the single highest-impact change for viewport responsiveness on large meshes.
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree
THREE.Mesh.prototype.raycast = acceleratedRaycast

// ── Public types ──────────────────────────────────────────────────────────────

export interface BuildVolume { width: number; depth: number; height: number }

/**
 * All values in print-space:
 *   x/y = left-right / front-back on the bed (mm from center)
 *   z   = height above bed (mm)
 *   rotX/Y = tilt; rotZ = spin on bed (degrees)
 *   scaleX/Y/Z = multipliers (1.0 = unscaled)
 *
 * Three.js mapping (Y-up):  threeX=printX  threeY=printZ  threeZ=printY
 */
export interface ModelTransform {
  x: number; y: number; z: number
  rotX: number; rotY: number; rotZ: number
  scaleX: number; scaleY: number; scaleZ: number
}

export const DEFAULT_TRANSFORM: ModelTransform = {
  x: 0, y: 0, z: 0,
  rotX: 0, rotY: 0, rotZ: 0,
  scaleX: 1, scaleY: 1, scaleZ: 1,
}

export interface ModelEntry {
  id: string
  name: string
  url: string
  transform: ModelTransform
}

export interface StlViewerHandle {
  placeFaceOnBed(): void
  resetTransform(): void
  centerOnBed(): void
  placeOnBed(): void
  autoArrange(): void
}

// Support editing types
export interface SupportPointData {
  id: string
  x: number; y: number; z: number
  type: 'light' | 'medium' | 'heavy'
  // Advanced: segment-based geometry
  segments?: { part: string; x1: number; y1: number; z1: number; r1: number; x2: number; y2: number; z2: number; r2: number }[]
}
export interface CrossBraceDisplayData {
  x1: number; y1: number; z1: number; x2: number; y2: number; z2: number; diameter: number
}
export interface PaintedRegionData {
  id: string
  mode: 'enforcer' | 'blocker'
  cx: number; cy: number; cz: number
  radiusMm: number
}
export type SupportEditMode = 'none' | 'add' | 'delete' | 'paint-enforcer' | 'paint-blocker'

interface Props {
  models: ModelEntry[]
  selectedId: string | null
  className?: string
  buildVolume?: BuildVolume
  onModelLoaded?: (id: string, size: { x: number; y: number; z: number }) => void
  onTransformChange?: (id: string, t: ModelTransform) => void
  onModelSelect?: (id: string | null) => void
  onBoundsChange?: (id: string, out: boolean) => void
  onFaceSelected?: (selected: boolean) => void
  onSizeChange?: (id: string, size: { x: number; y: number; z: number }) => void
  // Support editing
  supportEditMode?: SupportEditMode
  supportPoints?: SupportPointData[]
  paintedRegions?: PaintedRegionData[]
  supportTipType?: 'light' | 'medium' | 'heavy'
  supportBrushSize?: number
  onSupportPointAdd?: (x: number, y: number, z: number, nx: number, ny: number, nz: number, faceIndex?: number, baryU?: number, baryV?: number) => void
  onSupportPointDelete?: (id: string) => void
  onSupportPointSelect?: (id: string | null) => void
  selectedManualSupportId?: string | null
  onPaintRegionAdd?: (mode: 'enforcer' | 'blocker', cx: number, cy: number, cz: number) => void
  crossBraces?: CrossBraceDisplayData[]
  // V2 support mesh (binary STL ArrayBuffer) — renders as single mesh instead of individual cylinders
  supportMeshBuffer?: ArrayBuffer | null
  // Backend centering offset — used to reverse XY/Z centering on the return path
  supportMeshOffset?: { x: number; y: number; z: number } | null
  // ALL models' support meshes — rendered simultaneously (each in its own space)
  allSupportMeshes?: { modelId: string; buffer: ArrayBuffer; offset: { x: number; y: number; z: number } | null }[]
  // Manual support markers — rendered as real engine geometry when available, proxy sphere otherwise
  manualMarkers?: {
    id: string; x: number; y: number; z: number; shaftDiameter: number; uncoverable: boolean
    engineStatus?: string; engineMeshBase64?: string
    engineMeshOffset?: { x: number; y: number; z: number }
    provisional?: boolean
  }[]
  // True once commitOrientation has completed — clicks are blocked until this is true
  orientationCommitted?: boolean
  // Raft/Skirt visualization
  raftData?: { type: string; minX: number; minY: number; maxX: number; maxY: number; thicknessMm: number } | null
  skirtData?: { minX: number; minY: number; maxX: number; maxY: number; layers: number; distanceMm: number; widthMm: number } | null
  /** Analyze mode: color mesh faces by overhang angle (red/yellow/green) */
  analyzeMode?: boolean
  /** Overhang angle threshold in degrees (default 45) */
  analyzeAngleDeg?: number
}

// ── Coordinate-space helpers ──────────────────────────────────────────────────
//  print-space → Three.js:  rotZ (spin on bed) → euler.y   rotY (depth tilt) → euler.z
//  print scale: scaleY(depth)→scale.z  scaleZ(height)→scale.y

function applyTransform(group: THREE.Group, t: ModelTransform) {
  group.position.set(t.x, t.z, t.y)
  group.rotation.set(
    THREE.MathUtils.degToRad(t.rotX),
    THREE.MathUtils.degToRad(t.rotZ),
    THREE.MathUtils.degToRad(t.rotY),
    'XYZ',
  )
  group.scale.set(t.scaleX, t.scaleZ, t.scaleY)
}

function extractTransform(group: THREE.Group, _prev: ModelTransform): ModelTransform {
  return {
    x: group.position.x,
    y: group.position.z,
    z: group.position.y,
    rotX: THREE.MathUtils.radToDeg(group.rotation.x),
    rotY: THREE.MathUtils.radToDeg(group.rotation.z),
    rotZ: THREE.MathUtils.radToDeg(group.rotation.y),
    scaleX: group.scale.x,
    scaleY: group.scale.z,
    scaleZ: group.scale.y,
  }
}

function modelIsOOB(group: THREE.Group, bv: BuildVolume): boolean {
  // Check only the first child mesh (the model), not attached support meshes
  const modelMesh = group.children[0] as THREE.Mesh | undefined
  if (!modelMesh) return false
  // Use precomputed geometry.boundingBox + matrixWorld transform (8 corners)
  // instead of setFromObject which walks ALL vertices (~5-15ms for 695K tris).
  const geo = modelMesh.geometry
  if (!geo.boundingBox) geo.computeBoundingBox()
  const wb = geo.boundingBox!.clone().applyMatrix4(modelMesh.matrixWorld)
  return (
    wb.min.x < -bv.width / 2 || wb.max.x > bv.width / 2 ||
    wb.min.z < -bv.depth / 2 || wb.max.z > bv.depth / 2 ||
    wb.min.y < -0.01 || wb.max.y > bv.height
  )
}

// ── Per-model internal state ──────────────────────────────────────────────────

interface MeshData {
  group: THREE.Group
  mesh: THREE.Mesh
  url: string
  naturalSize: THREE.Vector3   // un-scaled, in Three.js units
  currentTransform: ModelTransform
  faceNormal: THREE.Vector3 | null     // local geometry space
  faceHitPoint: THREE.Vector3 | null   // world space
  arrowHelper: THREE.ArrowHelper | null
}

// ── Constants ─────────────────────────────────────────────────────────────────

const DEFAULT_BV: BuildVolume = { width: 220, depth: 220, height: 250 }

// Model colors
const C_NORMAL   = 0x1e40af  // unselected blue
const C_SELECTED = 0x3b82f6  // selected bright blue
const C_FACE     = 0xf97316  // face selected orange
const C_OOB      = 0xef4444  // out-of-bounds red
const C_OOB_SEL  = 0xdc2626  // out-of-bounds selected

// ── Component ─────────────────────────────────────────────────────────────────

const StlViewer = forwardRef<StlViewerHandle, Props>(function StlViewer(
  {
    models,
    selectedId,
    className = '',
    buildVolume = DEFAULT_BV,
    onModelLoaded,
    onTransformChange,
    onModelSelect,
    onBoundsChange,
    onFaceSelected,
    onSizeChange,
    supportEditMode,
    supportPoints: _supportPoints,
    paintedRegions,
    supportTipType: _supportTipType,
    supportBrushSize: _supportBrushSize,
    onSupportPointAdd,
    onSupportPointDelete,
    onSupportPointSelect,
    selectedManualSupportId,
    onPaintRegionAdd,
    crossBraces: _crossBraces,
    supportMeshBuffer,
    supportMeshOffset,
    allSupportMeshes,
    manualMarkers,
    orientationCommitted,
    raftData,
    skirtData,
    analyzeMode,
    analyzeAngleDeg = 45,
  },
  ref,
) {
  const mountRef   = useRef<HTMLDivElement>(null)
  const rendererRef = useRef<THREE.WebGLRenderer | null>(null)
  const sceneRef   = useRef<THREE.Scene | null>(null)
  const cameraRef  = useRef<THREE.PerspectiveCamera | null>(null)
  const controlsRef = useRef<OrbitControls | null>(null)
  const animIdRef  = useRef(0)

  const buildBoxRef = useRef<THREE.LineSegments | null>(null)
  const bedMeshRef  = useRef<THREE.Mesh | null>(null)
  const boxHelperRef = useRef<{ helper: THREE.Box3Helper; box: THREE.Box3 } | null>(null)

  const meshMapRef    = useRef<Map<string, MeshData>>(new Map())
  // Expose mesh map globally so the generate function can export transformed STL
  ;(window as any).__stlViewerMeshMap = meshMapRef.current
  const loadingIdsRef = useRef<Set<string>>(new Set())
  const gizmoRef      = useRef<GizmoManager | null>(null)

  // Support edit mode — written synchronously to DOM data attribute during render.
  // This guarantees any event handler firing after this render commit sees the correct value.
  // (A useEffect would run after paint, creating a one-frame race window.)
  const supportEditModeRef = useRef(supportEditMode)
  supportEditModeRef.current = supportEditMode
  if (rendererRef.current?.domElement) {
    rendererRef.current.domElement.dataset.supportEditMode = supportEditMode ?? 'none'
  }
  const orientationCommittedRef = useRef(orientationCommitted ?? true)
  orientationCommittedRef.current = orientationCommitted ?? true

  // Drag state (model bed-plane drag)
  const draggingIdRef    = useRef<string | null>(null)
  const hasDraggedRef    = useRef(false)
  const mouseDownPxRef   = useRef({ x: 0, y: 0 })
  const dragOffsetRef    = useRef({ x: 0, z: 0 })
  // Empty-area click tracking (for deselect-on-click-empty)
  const emptyClickPxRef  = useRef<{ x: number; y: number } | null>(null)

  // Use state so effects that depend on scene being ready re-run correctly
  const [sceneReady, setSceneReady] = useState(false)

  // Stable prop refs (so closures always see the latest without re-binding)
  const modelsRef           = useRef(models);            modelsRef.current = models
  const selectedIdRef       = useRef(selectedId);        selectedIdRef.current = selectedId
  const buildVolumeRef      = useRef(buildVolume);       buildVolumeRef.current = buildVolume
  const onTransformChangeRef = useRef(onTransformChange); onTransformChangeRef.current = onTransformChange
  const onModelSelectRef    = useRef(onModelSelect);     onModelSelectRef.current = onModelSelect
  const onBoundsChangeRef   = useRef(onBoundsChange);    onBoundsChangeRef.current = onBoundsChange
  const onFaceSelectedRef   = useRef(onFaceSelected);    onFaceSelectedRef.current = onFaceSelected
  const onModelLoadedRef    = useRef(onModelLoaded);     onModelLoadedRef.current = onModelLoaded
  const onSizeChangeRef     = useRef(onSizeChange);      onSizeChangeRef.current  = onSizeChange

  // ── Color / bounds helpers ────────────────────────────────────────────────

  const getModelColor = useCallback((id: string): number => {
    const data = meshMapRef.current.get(id)
    if (!data) return C_NORMAL
    const sel = id === selectedIdRef.current
    const out = modelIsOOB(data.group, buildVolumeRef.current)
    if (out) return sel ? C_OOB_SEL : C_OOB
    if (sel && data.faceNormal) return C_FACE
    return sel ? C_SELECTED : C_NORMAL
  }, [])

  const paintMesh = useCallback((id: string) => {
    const data = meshMapRef.current.get(id)
    if (!data) return
    ;(data.mesh.material as THREE.MeshPhongMaterial).color.setHex(getModelColor(id))
  }, [getModelColor])

  const paintAll = useCallback(() => {
    for (const id of meshMapRef.current.keys()) paintMesh(id)
  }, [paintMesh])

  // ── Analyze mode: per-vertex overhang coloring ──────────────────────────
  useEffect(() => {
    for (const [_id, data] of meshMapRef.current) {
      const geo = data.mesh.geometry
      const mat = data.mesh.material as THREE.MeshPhongMaterial

      if (analyzeMode) {
        // Compute per-face colors based on face normal Z component
        // In Three.js Y-up space: face normal Y < threshold → overhang
        geo.computeVertexNormals()
        const normals = geo.getAttribute('normal')
        if (!normals) continue

        const colors = new Float32Array(normals.count * 3)
        const threshold = -Math.cos((analyzeAngleDeg) * Math.PI / 180) // Y component threshold

        const idx = geo.getIndex()
        if (idx) {
          // Indexed geometry: compute per-face normal and assign to all 3 vertices
          // Since flatShading is used, we compute face normals from positions
          const pos = geo.getAttribute('position')
          const faceColors = new Map<number, [number, number, number]>()
          for (let i = 0; i < idx.count; i += 3) {
            const ai = idx.getX(i), bi = idx.getX(i+1), ci = idx.getX(i+2)
            const ax = pos.getX(ai), ay = pos.getY(ai), az = pos.getZ(ai)
            const bx = pos.getX(bi), by = pos.getY(bi), bz = pos.getZ(bi)
            const cx2 = pos.getX(ci), cy = pos.getY(ci), cz = pos.getZ(ci)
            // Face normal via cross product
            const e1x = bx-ax, e1y = by-ay, e1z = bz-az
            const e2x = cx2-ax, e2y = cy-ay, e2z = cz-az
            let ny = e1z*e2x - e1x*e2z // cross Y component
            const len = Math.sqrt((e1y*e2z-e1z*e2y)**2 + ny**2 + (e1x*e2y-e1y*e2x)**2)
            ny = len > 1e-6 ? ny / len : 0

            let r: number, g: number, b: number
            if (ny < threshold) { r = 0.9; g = 0.15; b = 0.15 } // red: steep overhang
            else if (ny < threshold * 0.5) { r = 0.9; g = 0.7; b = 0.1 } // yellow: moderate
            else { r = 0.15; g = 0.7; b = 0.3 } // green: safe

            for (const vi of [ai, bi, ci]) {
              const existing = faceColors.get(vi)
              if (!existing || r > existing[0]) faceColors.set(vi, [r, g, b])
            }
          }
          for (const [vi, [r, g, b]] of faceColors) {
            colors[vi*3] = r; colors[vi*3+1] = g; colors[vi*3+2] = b
          }
        } else {
          // Non-indexed: every 3 vertices = one face
          const pos = geo.getAttribute('position')
          for (let i = 0; i < pos.count; i += 3) {
            const e1x = pos.getX(i+1)-pos.getX(i), e1y = pos.getY(i+1)-pos.getY(i), e1z = pos.getZ(i+1)-pos.getZ(i)
            const e2x = pos.getX(i+2)-pos.getX(i), e2y = pos.getY(i+2)-pos.getY(i), e2z = pos.getZ(i+2)-pos.getZ(i)
            let ny = e1z*e2x - e1x*e2z
            const len = Math.sqrt((e1y*e2z-e1z*e2y)**2 + ny**2 + (e1x*e2y-e1y*e2x)**2)
            ny = len > 1e-6 ? ny / len : 0

            let r: number, g: number, b: number
            if (ny < threshold) { r = 0.9; g = 0.15; b = 0.15 }
            else if (ny < threshold * 0.5) { r = 0.9; g = 0.7; b = 0.1 }
            else { r = 0.15; g = 0.7; b = 0.3 }

            for (let j = 0; j < 3; j++) {
              colors[(i+j)*3] = r; colors[(i+j)*3+1] = g; colors[(i+j)*3+2] = b
            }
          }
        }

        geo.setAttribute('color', new THREE.BufferAttribute(colors, 3))
        mat.vertexColors = true
        mat.color.setHex(0xffffff) // let vertex colors through
        mat.needsUpdate = true
      } else {
        // Remove vertex colors
        if (geo.hasAttribute('color')) {
          geo.deleteAttribute('color')
          mat.vertexColors = false
          mat.needsUpdate = true
        }
        paintAll()
      }
    }
  }, [analyzeMode, analyzeAngleDeg, paintAll])

  const checkAllBounds = useCallback(() => {
    for (const [id, data] of meshMapRef.current) {
      data.group.updateMatrixWorld(true)
      onBoundsChangeRef.current?.(id, modelIsOOB(data.group, buildVolumeRef.current))
    }
    paintAll()
  }, [paintAll])

  // ── Face selection ────────────────────────────────────────────────────────

  const clearFace = useCallback((id?: string) => {
    const target = id ?? selectedIdRef.current
    if (!target) return
    const data = meshMapRef.current.get(target)
    if (!data) return
    data.faceNormal = null
    data.faceHitPoint = null
    if (data.arrowHelper && sceneRef.current) {
      sceneRef.current.remove(data.arrowHelper)
      data.arrowHelper = null
    }
    paintMesh(target)
    onFaceSelectedRef.current?.(false)
  }, [paintMesh])

  // ── Imperative handle ─────────────────────────────────────────────────────

  useImperativeHandle(ref, () => ({

    placeFaceOnBed() {
      const id = selectedIdRef.current
      if (!id) return
      const data = meshMapRef.current.get(id)
      if (!data || !data.faceNormal) return

      // Ensure matrices are current before transforming the face normal
      data.group.updateMatrixWorld(true)

      // Transform local-space face normal → world space
      const worldNormal = data.faceNormal.clone()
        .transformDirection(data.mesh.matrixWorld)
        .normalize()

      const down = new THREE.Vector3(0, -1, 0)
      if (worldNormal.dot(down) < 0.9999) {
        const q = new THREE.Quaternion().setFromUnitVectors(worldNormal, down)
        data.group.quaternion.premultiply(q)
      }

      // Snap lowest point to bed (Y = 0) — use precise=true to get exact vertex-level AABB
      data.group.updateMatrixWorld(true)
      const wb = new THREE.Box3().setFromObject(data.group, true)
      data.group.position.y -= wb.min.y

      data.currentTransform = extractTransform(data.group, data.currentTransform)
      clearFace(id)
      checkAllBounds()
      onTransformChangeRef.current?.(id, { ...data.currentTransform })
    },

    resetTransform() {
      const id = selectedIdRef.current
      if (!id) return
      const data = meshMapRef.current.get(id)
      if (!data) return
      applyTransform(data.group, DEFAULT_TRANSFORM)
      data.currentTransform = { ...DEFAULT_TRANSFORM }
      clearFace(id)
      checkAllBounds()
      onTransformChangeRef.current?.(id, { ...DEFAULT_TRANSFORM })
    },

    centerOnBed() {
      const id = selectedIdRef.current
      if (!id) return
      const data = meshMapRef.current.get(id)
      if (!data) return
      data.group.position.x = 0
      data.group.position.z = 0
      data.currentTransform.x = 0
      data.currentTransform.y = 0
      checkAllBounds()
      onTransformChangeRef.current?.(id, { ...data.currentTransform })
    },

    placeOnBed() {
      const id = selectedIdRef.current
      if (!id) return
      const data = meshMapRef.current.get(id)
      if (!data) return
      data.group.updateMatrixWorld(true)
      const wb = new THREE.Box3().setFromObject(data.group, true)
      data.group.position.y -= wb.min.y
      if (data.group.position.y < 0) data.group.position.y = 0
      data.currentTransform.z = data.group.position.y
      checkAllBounds()
      onTransformChangeRef.current?.(id, { ...data.currentTransform })
    },

    autoArrange() {
      const bv = buildVolumeRef.current
      const SPACING = 5

      // Collect footprints from current world bounding boxes
      const fps: { id: string; w: number; d: number }[] = []
      for (const [id, data] of meshMapRef.current) {
        data.group.updateMatrixWorld(true)
        const wb = new THREE.Box3().setFromObject(data.group)
        fps.push({ id, w: wb.max.x - wb.min.x, d: wb.max.z - wb.min.z })
      }
      fps.sort((a, b) => b.w * b.d - a.w * a.d)

      let curX = -bv.width / 2 + SPACING
      let curZ = -bv.depth / 2 + SPACING
      let rowD = 0

      for (let i = 0; i < fps.length; i++) {
        const { id, w, d } = fps[i]
        if (i > 0 && curX + w > bv.width / 2 - SPACING) {
          curX = -bv.width / 2 + SPACING
          curZ += rowD + SPACING
          rowD = 0
        }
        const data = meshMapRef.current.get(id)!
        data.group.position.x = curX + w / 2
        data.group.position.z = curZ + d / 2
        data.group.updateMatrixWorld(true)
        const wb = new THREE.Box3().setFromObject(data.group)
        data.group.position.y -= wb.min.y
        if (data.group.position.y < 0) data.group.position.y = 0
        data.currentTransform = extractTransform(data.group, data.currentTransform)
        onTransformChangeRef.current?.(id, { ...data.currentTransform })
        curX += w + SPACING
        rowD = Math.max(rowD, d)
      }
      checkAllBounds()
    },

  }), [clearFace, checkAllBounds])

  // ── Scene setup (once on mount) ───────────────────────────────────────────

  useEffect(() => {
    const mount = mountRef.current
    if (!mount) return

    const scene = new THREE.Scene()
    scene.background = new THREE.Color(0x111827)
    sceneRef.current = scene

    const camera = new THREE.PerspectiveCamera(45, mount.clientWidth / mount.clientHeight, 0.1, 5000)
    camera.position.set(200, 150, 200)
    cameraRef.current = camera

    const renderer = new THREE.WebGLRenderer({ antialias: true })
    renderer.setSize(mount.clientWidth, mount.clientHeight)
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2)) // cap at 2× to prevent 4K overdraw
    renderer.shadowMap.enabled = false // shadows re-render the scene — disable for performance
    mount.appendChild(renderer.domElement)
    renderer.domElement.dataset.supportEditMode = supportEditMode ?? 'none'
    rendererRef.current = renderer

    scene.add(new THREE.AmbientLight(0xffffff, 0.55))
    const sun = new THREE.DirectionalLight(0xffffff, 1.0)
    sun.position.set(1, 2, 3)
    scene.add(sun)
    const fill = new THREE.DirectionalLight(0xffffff, 0.3)
    fill.position.set(-1, -0.5, -1)
    scene.add(fill)

    const controls = new OrbitControls(camera, renderer.domElement)
    controls.enableDamping = true
    controls.dampingFactor = 0.08
    controls.mouseButtons = {
      LEFT: THREE.MOUSE.ROTATE,
      MIDDLE: THREE.MOUSE.DOLLY,
      RIGHT: THREE.MOUSE.PAN,
    }
    controlsRef.current = controls

    scene.add(new THREE.GridHelper(800, 80, 0x374151, 0x1f2937))

    // ── Gizmo ──────────────────────────────────────────────────────────────
    const gizmo = new GizmoManager(scene, camera)
    gizmoRef.current = gizmo

    // ── Mouse interaction ─────────────────────────────────────────────────

    const raycaster = new THREE.Raycaster()
    const bedPlane  = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0)

    const toNDC = (e: MouseEvent) => {
      const r = renderer.domElement.getBoundingClientRect()
      return new THREE.Vector2(
        ((e.clientX - r.left) / r.width) * 2 - 1,
        -((e.clientY - r.top) / r.height) * 2 + 1,
      )
    }

    let lastCbMs = 0

    const emitTransform = (id: string) => {
      const data = meshMapRef.current.get(id)
      if (!data) return
      data.group.updateMatrixWorld(true)
      data.currentTransform = extractTransform(data.group, data.currentTransform)
      const wb = new THREE.Box3().setFromObject(data.group)
      const ws = wb.getSize(new THREE.Vector3())
      onSizeChangeRef.current?.(id, { x: ws.x, y: ws.z, z: ws.y })
      onBoundsChangeRef.current?.(id, modelIsOOB(data.group, buildVolumeRef.current))
      onTransformChangeRef.current?.(id, { ...data.currentTransform })
      paintMesh(id)
    }

    // Read support edit mode from DOM data attribute — immune to closure staleness
    const getSupportMode = () => renderer.domElement.dataset.supportEditMode ?? 'none'

    const onMouseDown = (e: MouseEvent) => {
      if (e.button !== 0) return

      raycaster.setFromCamera(toNDC(e), camera)
      emptyClickPxRef.current = null

      // ── 1. Check gizmo handles first ──────────────────────────────────────
      const hitHandle = gizmo.hitTest(raycaster)
      if (hitHandle) {
        const selId = selectedIdRef.current
        if (selId) {
          gizmo.startDrag(hitHandle, raycaster.ray)
          controls.enabled = false
          hasDraggedRef.current = false
          mouseDownPxRef.current = { x: e.clientX, y: e.clientY }
          draggingIdRef.current = '__gizmo__'
        }
        return
      }

      // ── 2. In support edit mode, block drag/face-select entirely.
      const mode = getSupportMode()
      if (mode !== 'none') {
        mouseDownPxRef.current = { x: e.clientX, y: e.clientY }
        draggingIdRef.current = '__support__'
        return
      }

      // ── 3. Check model meshes ─────────────────────────────────────────────
      const allMeshes: THREE.Mesh[] = []
      for (const d of meshMapRef.current.values()) allMeshes.push(d.mesh)

      if (allMeshes.length === 0) {
        emptyClickPxRef.current = { x: e.clientX, y: e.clientY }
        return
      }

      const _rcT0 = performance.now()
      const hits = raycaster.intersectObjects(allMeshes, false)
      const _rcMs = performance.now() - _rcT0
      if (_rcMs > 5) console.warn(`[Raycast] ${_rcMs.toFixed(1)}ms for ${allMeshes.reduce((s,m) => s + (m.geometry.getAttribute('position')?.count ?? 0)/3, 0)} tris`)
      if (hits.length === 0) {
        // Clicked on empty area — track for potential deselect
        emptyClickPxRef.current = { x: e.clientX, y: e.clientY }
        return
      }

      const hitMesh = hits[0].object as THREE.Mesh
      let hitId: string | null = null
      for (const [id, d] of meshMapRef.current) {
        if (d.mesh === hitMesh) { hitId = id; break }
      }
      if (!hitId) return

      // Switch selection if needed
      if (hitId !== selectedIdRef.current) {
        clearFace(selectedIdRef.current ?? undefined)
        onModelSelectRef.current?.(hitId)
      }

      // Enter bed-plane drag for this model
      draggingIdRef.current  = hitId
      hasDraggedRef.current  = false
      mouseDownPxRef.current = { x: e.clientX, y: e.clientY }
      controls.enabled       = false

      // Store face data for potential face-click
      const data = meshMapRef.current.get(hitId)!
      if (hits[0].face) {
        data.faceNormal   = hits[0].face.normal.clone()
        data.faceHitPoint = hits[0].point.clone()
      }

      // Drag offset relative to bed plane hit
      const bedTarget = new THREE.Vector3()
      raycaster.ray.intersectPlane(bedPlane, bedTarget)
      dragOffsetRef.current = {
        x: data.group.position.x - bedTarget.x,
        z: data.group.position.z - bedTarget.z,
      }
    }

    const onMouseMove = (e: MouseEvent) => {
      const id = draggingIdRef.current
      if (!id) return

      const dx = e.clientX - mouseDownPxRef.current.x
      const dy = e.clientY - mouseDownPxRef.current.y
      if (Math.sqrt(dx * dx + dy * dy) > 3) hasDraggedRef.current = true
      if (!hasDraggedRef.current) return

      raycaster.setFromCamera(toNDC(e), camera)

      // ── Gizmo drag ────────────────────────────────────────────────────────
      if (id === '__gizmo__') {
        const selId = selectedIdRef.current
        if (!selId) return
        const result = gizmo.updateDrag(raycaster.ray)
        if (result.positionChanged || result.rotationChanged) {
          const data = meshMapRef.current.get(selId)
          if (data) {
            data.group.updateMatrixWorld(true)
            // Re-seat on bed after rotation — use precomputed AABB + matrix
            if (result.rotationChanged) {
              const geo = data.mesh.geometry
              if (!geo.boundingBox) geo.computeBoundingBox()
              const wb = geo.boundingBox!.clone().applyMatrix4(data.mesh.matrixWorld)
              if (wb.min.y < 0) data.group.position.y -= wb.min.y
            }
            data.currentTransform = extractTransform(data.group, data.currentTransform)
            onBoundsChangeRef.current?.(selId, modelIsOOB(data.group, buildVolumeRef.current))
            const now = Date.now()
            if (now - lastCbMs >= 40) {
              lastCbMs = now
              const geo = data.mesh.geometry
              if (!geo.boundingBox) geo.computeBoundingBox()
              const wb = geo.boundingBox!.clone().applyMatrix4(data.mesh.matrixWorld)
              const ws = wb.getSize(new THREE.Vector3())
              onSizeChangeRef.current?.(selId, { x: ws.x, y: ws.z, z: ws.y })
              onTransformChangeRef.current?.(selId, { ...data.currentTransform })
            }
            paintMesh(selId)
          }
        }
        return
      }

      // ── Bed-plane drag ────────────────────────────────────────────────────
      const data = meshMapRef.current.get(id)
      if (!data) return

      const target = new THREE.Vector3()
      if (!raycaster.ray.intersectPlane(bedPlane, target)) return

      data.group.position.x = target.x + dragOffsetRef.current.x
      data.group.position.z = target.z + dragOffsetRef.current.z
      data.currentTransform.x = data.group.position.x
      data.currentTransform.y = data.group.position.z

      data.group.updateMatrixWorld(true)
      onBoundsChangeRef.current?.(id, modelIsOOB(data.group, buildVolumeRef.current))

      const now = Date.now()
      if (now - lastCbMs >= 40) {
        lastCbMs = now
        onTransformChangeRef.current?.(id, { ...data.currentTransform })
      }
      paintMesh(id)
    }

    const onMouseUp = (e: MouseEvent) => {
      const id = draggingIdRef.current
      controls.enabled = true

      // ── UNCONDITIONAL support-mode guard ──
      // Read from DOM data attribute — immune to React ref/closure staleness.
      const supportMode = getSupportMode()
      if (supportMode !== 'none') {
        draggingIdRef.current = null
        hasDraggedRef.current = false
        // Check if it was a click (not drag/orbit)
        const sdx = e.clientX - (mouseDownPxRef.current?.x ?? e.clientX)
        const sdy = e.clientY - (mouseDownPxRef.current?.y ?? e.clientY)
        if (Math.sqrt(sdx * sdx + sdy * sdy) <= 4) {
          if (!orientationCommittedRef.current) return
          // Identity gate
          const ident = new THREE.Matrix4()
          for (const [, d] of meshMapRef.current) {
            if (!d.group.matrixWorld.equals(ident)) return
          }
          raycaster.setFromCamera(toNDC(e), camera)
          if (supportMode === 'add') {
            const meshes: THREE.Mesh[] = []
            meshMapRef.current.forEach(d => {
              if (!d.mesh.geometry.boundingSphere) d.mesh.geometry.computeBoundingSphere()
              meshes.push(d.mesh)
            })
            const hits = raycaster.intersectObjects(meshes, false)
            if (hits.length > 0) {
              const wp = hits[0].point
              const wn = hits[0].face?.normal ?? new THREE.Vector3(0, -1, 0)
              if (isFinite(wn.x) && isFinite(wn.y) && isFinite(wn.z) && wn.lengthSq() > 0.001) {
                let baryU: number | undefined, baryV: number | undefined
                if (hits[0].uv) { baryU = hits[0].uv.x; baryV = hits[0].uv.y }
                onSupportPointAddRef.current?.(wp.x, wp.y, wp.z, wn.x, wn.y, wn.z, hits[0].faceIndex ?? undefined, baryU, baryV)
              }
            }
          } else if (supportMode === 'delete') {
            if (manualMarkerGroupRef.current) {
              const hits = raycaster.intersectObjects(manualMarkerGroupRef.current.children, false)
              if (hits.length > 0) {
                const sid = hits[0].object.userData.supportPointId
                if (sid) onSupportPointDeleteRef.current?.(sid)
              }
            }
          } else if (supportMode === 'none' && manualMarkerGroupRef.current) {
            // Click to select/deselect manual support for editing
            const hits = raycaster.intersectObjects(manualMarkerGroupRef.current.children, false)
            const sid = hits.length > 0 ? hits[0].object.userData.supportPointId : null
            onSupportPointSelectRef.current?.(sid ?? null)
          } else if (supportMode === 'paint-enforcer' || supportMode === 'paint-blocker') {
            const meshes: THREE.Mesh[] = []
            meshMapRef.current.forEach(d => meshes.push(d.mesh))
            const hits = raycaster.intersectObjects(meshes, false)
            if (hits.length > 0) {
              const wp = hits[0].point
              const paintMode = supportMode === 'paint-enforcer' ? 'enforcer' : 'blocker'
              onPaintRegionAddRef.current?.(paintMode, wp.x, wp.z, wp.y)
            }
          }
        }
        return // ALWAYS return here — never fall through to face-selection
      }

      // Empty-area single click → deselect
      if (!id && emptyClickPxRef.current) {
        const dx = e.clientX - emptyClickPxRef.current.x
        const dy = e.clientY - emptyClickPxRef.current.y
        if (Math.sqrt(dx * dx + dy * dy) <= 3 && selectedIdRef.current) {
          clearFace(selectedIdRef.current)
          onModelSelectRef.current?.(null)
        }
        emptyClickPxRef.current = null
        return
      }

      if (!id) return

      // ── End gizmo drag ────────────────────────────────────────────────────
      if (id === '__gizmo__') {
        gizmo.endDrag()
        const selId = selectedIdRef.current
        if (selId && hasDraggedRef.current) emitTransform(selId)
        draggingIdRef.current = null
        hasDraggedRef.current = false
        return
      }

      if (!hasDraggedRef.current) {
        // Was a click — handle face selection for the selected model
        const data = meshMapRef.current.get(id)
        if (data && id === selectedIdRef.current) {
          raycaster.setFromCamera(toNDC(e), camera)
          const hits = raycaster.intersectObject(data.mesh, false)

          if (hits.length > 0 && hits[0].face) {
            const face = hits[0].face
            data.faceNormal   = face.normal.clone()
            data.faceHitPoint = hits[0].point.clone()

            if (data.arrowHelper && sceneRef.current) sceneRef.current.remove(data.arrowHelper)
            const worldNorm = face.normal.clone().transformDirection(data.mesh.matrixWorld).normalize()
            const sz = new THREE.Box3().setFromObject(data.group).getSize(new THREE.Vector3())
            const arrowLen = Math.max(10, sz.length() * 0.2)
            const arrow = new THREE.ArrowHelper(worldNorm, hits[0].point, arrowLen, 0xf97316, arrowLen * 0.3, arrowLen * 0.15)
            if (sceneRef.current) sceneRef.current.add(arrow)
            data.arrowHelper = arrow

            paintMesh(id)
            onFaceSelectedRef.current?.(true)
          } else {
            clearFace(id)
          }
        }
      } else {
        // End of bed-plane drag — emit final position
        emitTransform(id)
      }

      draggingIdRef.current = null
      hasDraggedRef.current = false
    }

    // Double-click on a model → attach gizmo to selected model
    const onDblClick = (e: MouseEvent) => {
      raycaster.setFromCamera(toNDC(e), camera)
      const selId = selectedIdRef.current
      if (!selId) return
      const data = meshMapRef.current.get(selId)
      if (!data) return
      const hits = raycaster.intersectObject(data.mesh, false)
      if (hits.length > 0) {
        gizmoRef.current?.attachTo(data.group)
      }
    }

    renderer.domElement.addEventListener('mousedown', onMouseDown)
    renderer.domElement.addEventListener('dblclick', onDblClick)
    window.addEventListener('mousemove', onMouseMove)
    window.addEventListener('mouseup', onMouseUp)

    // Render loop
    const animate = () => {
      animIdRef.current = requestAnimationFrame(animate)
      controls.update()
      // Box helper: transform precomputed AABB instead of recomputing from 2M vertices.
      // geometry.boundingBox is computed ONCE on load; applyMatrix4 transforms 8 corners (~0ms)
      // vs setFromObject(group,true) which walks ALL vertices every frame (~15ms for 695K tris).
      if (boxHelperRef.current) {
        const selId = selectedIdRef.current
        if (selId) {
          const d = meshMapRef.current.get(selId)
          if (d && d.mesh.geometry.boundingBox) {
            boxHelperRef.current.box.copy(d.mesh.geometry.boundingBox).applyMatrix4(d.mesh.matrixWorld)
          }
        }
      }
      gizmo.update()
      renderer.render(scene, camera)
    }
    animate()

    // Resize observer
    const ro = new ResizeObserver(() => {
      const m = mountRef.current
      if (!m || !rendererRef.current) return
      camera.aspect = m.clientWidth / m.clientHeight
      camera.updateProjectionMatrix()
      rendererRef.current.setSize(m.clientWidth, m.clientHeight)
    })
    ro.observe(mount)

    setSceneReady(true)

    return () => {
      setSceneReady(false)
      cancelAnimationFrame(animIdRef.current)
      ro.disconnect()
      controls.dispose()
      gizmo.dispose()
      gizmoRef.current = null
      renderer.domElement.removeEventListener('mousedown', onMouseDown)
      renderer.domElement.removeEventListener('dblclick', onDblClick)
      window.removeEventListener('mousemove', onMouseMove)
      window.removeEventListener('mouseup', onMouseUp)
      renderer.dispose()
      if (renderer.domElement.parentNode === mount) mount.removeChild(renderer.domElement)
      sceneRef.current  = null
      rendererRef.current = null
      cameraRef.current = null
      controlsRef.current = null
    }
  }, [clearFace, paintMesh])

  // ── Build volume box ──────────────────────────────────────────────────────

  useEffect(() => {
    if (!sceneReady) return
    const scene = sceneRef.current!

    if (buildBoxRef.current) {
      scene.remove(buildBoxRef.current)
      buildBoxRef.current.geometry.dispose()
      ;(buildBoxRef.current.material as THREE.Material).dispose()
      buildBoxRef.current = null
    }
    if (bedMeshRef.current) {
      scene.remove(bedMeshRef.current)
      bedMeshRef.current.geometry.dispose()
      ;(bedMeshRef.current.material as THREE.Material).dispose()
      bedMeshRef.current = null
    }

    const { width, depth, height } = buildVolume
    if (width > 0 && depth > 0 && height > 0) {
      // Wireframe envelope
      const boxGeo = new THREE.BoxGeometry(width, height, depth)
      const edges   = new THREE.EdgesGeometry(boxGeo)
      boxGeo.dispose()
      const lineBox = new THREE.LineSegments(
        edges,
        new THREE.LineBasicMaterial({ color: 0x4ade80, transparent: true, opacity: 0.5 }),
      )
      lineBox.position.set(0, height / 2, 0)
      scene.add(lineBox)
      buildBoxRef.current = lineBox

      // Translucent bed plane
      const bed = new THREE.Mesh(
        new THREE.PlaneGeometry(width, depth),
        new THREE.MeshBasicMaterial({ color: 0x14532d, transparent: true, opacity: 0.25, side: THREE.DoubleSide }),
      )
      bed.rotation.x = -Math.PI / 2
      bed.position.y  = 0.05
      scene.add(bed)
      bedMeshRef.current = bed
    }

    checkAllBounds()
  }, [buildVolume, sceneReady, checkAllBounds])

  // ── Models sync ───────────────────────────────────────────────────────────

  useEffect(() => {
    if (!sceneReady) return
    const scene = sceneRef.current!

    const currentIds = new Set(models.map(m => m.id))

    // Remove deleted models
    for (const [id, data] of meshMapRef.current) {
      if (!currentIds.has(id)) {
        scene.remove(data.group)
        data.mesh.geometry.dispose()
        ;(data.mesh.material as THREE.Material).dispose()
        if (data.arrowHelper) scene.remove(data.arrowHelper)
        meshMapRef.current.delete(id)
      }
    }

    // Add new models, update existing
    for (const model of models) {
      const existing  = meshMapRef.current.get(model.id)
      const isLoading = loadingIdsRef.current.has(model.id)

      if (!existing && !isLoading) {
        // New model — load async (guard prevents duplicate loads on re-render)
        loadingIdsRef.current.add(model.id)
        const loader = new STLLoader()

        loader.load(
          model.url,
          (geometry) => {
            loadingIdsRef.current.delete(model.id)
            if (!sceneRef.current) { geometry.dispose(); return }

            // Model may have been removed while loading
            const liveModel = modelsRef.current.find(m => m.id === model.id)
            if (!liveModel) { geometry.dispose(); return }

            // Swap Y/Z axes: STL files use Z-up, Three.js uses Y-up.
            // This must happen BEFORE center() so height is along Y.
            // Swapping Y/Z reverses triangle winding (reflection), so we also
            // swap vertices 1 and 2 of each triangle to restore correct outward normals.
            const pos = geometry.getAttribute('position')
            for (let i = 0; i < pos.count; i++) {
              const y = pos.getY(i)
              const z = pos.getZ(i)
              pos.setY(i, z)  // Three.js Y = STL Z (height)
              pos.setZ(i, y)  // Three.js Z = STL Y (depth)
            }
            // Fix winding: swap vertices 1 and 2 of each triangle
            // (Y/Z swap is a reflection that reverses handedness)
            const idx = geometry.getIndex()
            if (idx) {
              for (let i = 0; i < idx.count; i += 3) {
                const a = idx.getX(i + 1)
                const b = idx.getX(i + 2)
                idx.setX(i + 1, b)
                idx.setX(i + 2, a)
              }
              idx.needsUpdate = true
            } else {
              // Non-indexed: swap position triplets
              for (let i = 0; i < pos.count; i += 3) {
                const x1 = pos.getX(i+1), y1 = pos.getY(i+1), z1 = pos.getZ(i+1)
                const x2 = pos.getX(i+2), y2 = pos.getY(i+2), z2 = pos.getZ(i+2)
                pos.setXYZ(i+1, x2, y2, z2)
                pos.setXYZ(i+2, x1, y1, z1)
              }
            }
            pos.needsUpdate = true

            // Index the geometry: STL is fully de-indexed (~2M verts for 695K tris).
            // mergeVertices recovers shared vertices (~6× reduction → ~350K unique).
            // CRITICAL: strip normals before merge (differing normals prevent vertex sharing),
            // then use flatShading which derives face normals in the shader — exact same
            // faceted STL appearance, ~6× fewer vertices for the GPU to process.
            geometry.deleteAttribute('normal') // strip normals before merge
            let indexed = mergeVertices(geometry, 1e-4)

            indexed.center()
            indexed.computeBoundingBox()
            const bb   = indexed.boundingBox!
            const size = new THREE.Vector3()
            bb.getSize(size)
            indexed.translate(0, size.y / 2, 0) // base at Y=0 (Y is now height)
            indexed.computeBoundingBox()
            indexed.computeBoundingSphere()

            ;(indexed as any).computeBoundsTree()
            geometry.dispose()

            const mesh = new THREE.Mesh(
              indexed,
              new THREE.MeshPhongMaterial({ color: C_NORMAL, specular: 0x222222, shininess: 40, side: THREE.DoubleSide, flatShading: true }),
            )
            const group = new THREE.Group()
            group.add(mesh)
            applyTransform(group, liveModel.transform)
            sceneRef.current!.add(group)

            const data: MeshData = {
              group, mesh, url: liveModel.url,
              naturalSize: size.clone(),
              currentTransform: { ...liveModel.transform },
              faceNormal: null, faceHitPoint: null, arrowHelper: null,
            }
            meshMapRef.current.set(model.id, data)

            // First model → frame camera
            if (meshMapRef.current.size === 1) {
              const bv = buildVolumeRef.current
              const d  = Math.max(size.x, size.y, size.z, bv.width, bv.depth) * 1.7
              cameraRef.current!.position.set(d, d * 0.7, d)
              cameraRef.current!.lookAt(0, size.y / 2, 0)
              controlsRef.current!.target.set(0, size.y / 2, 0)
              controlsRef.current!.update()
            }

            // Report natural size in print space (after Y/Z swap: y=height, z=depth)
            onModelLoadedRef.current?.(model.id, { x: size.x, y: size.z, z: size.y })
            paintMesh(model.id)
            checkAllBounds()
          },
          undefined,
          (err) => {
            loadingIdsRef.current.delete(model.id)
            console.error('STLLoader:', err)
          },
        )

      } else if (existing && existing.url !== model.url) {
        // URL changed — remove and let the next render re-load
        scene.remove(existing.group)
        existing.mesh.geometry.dispose()
        ;(existing.mesh.material as THREE.Material).dispose()
        if (existing.arrowHelper) scene.remove(existing.arrowHelper)
        meshMapRef.current.delete(model.id)

      } else if (existing && draggingIdRef.current !== model.id) {
        // Transform changed from parent (e.g., numeric input)
        applyTransform(existing.group, model.transform)
        existing.currentTransform = { ...model.transform }
        existing.group.updateMatrixWorld(true)
        const wb = new THREE.Box3().setFromObject(existing.group)
        const ws = wb.getSize(new THREE.Vector3())
        onSizeChangeRef.current?.(model.id, { x: ws.x, y: ws.z, z: ws.y })
        paintMesh(model.id)
        checkAllBounds()
      }
    }
  }, [models, sceneReady, paintMesh, checkAllBounds])

  // ── Selected model BoxHelper ──────────────────────────────────────────────

  useEffect(() => {
    if (!sceneReady) return
    const scene = sceneRef.current!

    if (boxHelperRef.current) {
      scene.remove(boxHelperRef.current.helper)
      boxHelperRef.current = null
    }

    if (selectedId) {
      const data = meshMapRef.current.get(selectedId)
      if (data) {
        // Gizmo is NOT auto-attached on selection — use double-click to show gizmo
        if (data.mesh.geometry.attributes.position) {
          const box = new THREE.Box3().setFromObject(data.group, true)
          const helper = new THREE.Box3Helper(box, 0xfbbf24)
          scene.add(helper)
          boxHelperRef.current = { helper, box }
        }
      }
    } else {
      gizmoRef.current?.attachTo(null)
    }

    paintAll()
  }, [selectedId, sceneReady, paintAll])

  // ── Support point & painted region rendering ───────────────────────────────

  const supportGroupRef = useRef<THREE.Group | null>(null)

  useEffect(() => {
    if (!sceneReady) return
    const scene = sceneRef.current!

    // Remove old support visuals
    if (supportGroupRef.current) {
      scene.remove(supportGroupRef.current)
      supportGroupRef.current.traverse(c => {
        if ((c as THREE.Mesh).geometry) (c as THREE.Mesh).geometry.dispose()
        if ((c as THREE.Mesh).material) ((c as THREE.Mesh).material as THREE.Material).dispose()
      })
      supportGroupRef.current = null
    }

    const regions = paintedRegions ?? []
    if (regions.length === 0 && !raftData && !skirtData) return

    const group = new THREE.Group()
    group.name = 'support-visuals'

    // V2 watertight mesh handles all support rendering.
    // This group only renders: painted regions (enforcer/blocker) + raft + skirt.

    // Render painted regions as transparent spheres
    regions.forEach(r => {
      const color = r.mode === 'enforcer' ? 0x3b82f6 : 0xf97316
      const geo = new THREE.SphereGeometry(r.radiusMm, 12, 12)
      const mat = new THREE.MeshPhongMaterial({ color, transparent: true, opacity: 0.25, depthWrite: false })
      const mesh = new THREE.Mesh(geo, mat)
      mesh.position.set(r.cx, r.cz, r.cy) // print-space → Three.js
      mesh.userData = { paintedRegionId: r.id }
      group.add(mesh)

      // Wireframe outline
      const wireGeo = new THREE.SphereGeometry(r.radiusMm, 8, 8)
      const wireMat = new THREE.MeshBasicMaterial({ color, wireframe: true, transparent: true, opacity: 0.5 })
      const wire = new THREE.Mesh(wireGeo, wireMat)
      wire.position.copy(mesh.position)
      group.add(wire)
    })

    // Render raft (flat rectangle under model)
    if (raftData) {
      const rw = raftData.maxX - raftData.minX
      const rd = raftData.maxY - raftData.minY
      const rh = raftData.thicknessMm
      const raftGeo = new THREE.BoxGeometry(rw, rh, rd)
      const raftColor = raftData.type === 'solid' ? 0x4a9eff : raftData.type === 'grid' ? 0x3b82f6 : 0x6366f1
      const raftMat = new THREE.MeshPhongMaterial({ color: raftColor, transparent: true, opacity: 0.4 })
      const raftMesh = new THREE.Mesh(raftGeo, raftMat)
      raftMesh.position.set(
        (raftData.minX + raftData.maxX) / 2,
        -rh / 2, // just below bed
        (raftData.minY + raftData.maxY) / 2
      )
      group.add(raftMesh)

      // Raft wireframe
      const raftWire = new THREE.LineSegments(
        new THREE.EdgesGeometry(raftGeo),
        new THREE.LineBasicMaterial({ color: raftColor, transparent: true, opacity: 0.6 })
      )
      raftWire.position.copy(raftMesh.position)
      group.add(raftWire)
    }

    // Render skirt (outline box at base)
    if (skirtData) {
      const sw = skirtData.maxX - skirtData.minX
      const sd = skirtData.maxY - skirtData.minY
      const sh = skirtData.layers * 0.05 // approximate layer height
      const skirtGeo = new THREE.BoxGeometry(sw, sh, sd)
      const skirtEdges = new THREE.EdgesGeometry(skirtGeo)
      const skirtLine = new THREE.LineSegments(skirtEdges,
        new THREE.LineBasicMaterial({ color: 0x22d3ee, transparent: true, opacity: 0.5 })
      )
      skirtLine.position.set(
        (skirtData.minX + skirtData.maxX) / 2,
        sh / 2,
        (skirtData.minY + skirtData.maxY) / 2
      )
      group.add(skirtLine)
    }

    scene.add(group)
    supportGroupRef.current = group
  }, [paintedRegions, raftData, skirtData, sceneReady])

  // ── V2 Support mesh rendering (single watertight mesh) ─────────────────

  const v2MeshRef = useRef<THREE.Mesh | null>(null)

  useEffect(() => {
    if (!sceneReady) return

    // Remove old V2 mesh from wherever it was attached
    if (v2MeshRef.current) {
      v2MeshRef.current.parent?.remove(v2MeshRef.current)
      v2MeshRef.current.geometry.dispose()
      ;(v2MeshRef.current.material as THREE.Material).dispose()
      v2MeshRef.current = null
    }

    if (!supportMeshBuffer || supportMeshBuffer.byteLength < 84) return
    if (!sceneRef.current) return

    try {
      const loader = new STLLoader()
      const geometry = loader.parse(supportMeshBuffer)

      // V2 mesh comes back in backend Z-up centered space.
      // Step 1: Reverse the backend's XY+Z centering to get back to Z-up world space.
      const positions = geometry.getAttribute('position')
      if (supportMeshOffset) {
        for (let i = 0; i < positions.count; i++) {
          positions.setX(i, positions.getX(i) - supportMeshOffset.x)
          positions.setY(i, positions.getY(i) - supportMeshOffset.y)
          positions.setZ(i, positions.getZ(i) - supportMeshOffset.z)
        }
      }
      // Step 2: Z-up → Y-up basis conversion (reverse of send path).
      for (let i = 0; i < positions.count; i++) {
        const y = positions.getY(i), z = positions.getZ(i)
        positions.setY(i, z)
        positions.setZ(i, y)
      }
      // Winding fix for Y↔Z swap (reflection)
      for (let i = 0; i < positions.count; i += 3) {
        const x1=positions.getX(i+1),y1=positions.getY(i+1),z1=positions.getZ(i+1)
        const x2=positions.getX(i+2),y2=positions.getY(i+2),z2=positions.getZ(i+2)
        positions.setXYZ(i+1, x2, y2, z2)
        positions.setXYZ(i+2, x1, y1, z1)
      }
      positions.needsUpdate = true

      // Index the support mesh for GPU efficiency
      geometry.deleteAttribute('normal')
      const indexedV2 = mergeVertices(geometry, 1e-4)
      geometry.dispose()

      const material = new THREE.MeshPhongMaterial({
        color: 0x14b8a6, specular: 0x444444, transparent: true, opacity: 0.7,
        shininess: 50, side: THREE.DoubleSide, depthWrite: false, flatShading: true,
      })

      const mesh = new THREE.Mesh(indexedV2, material)
      mesh.renderOrder = 1

      // Add to SCENE at identity — NOT to model group.
      sceneRef.current.add(mesh)
      v2MeshRef.current = mesh

      // Clear individual manual support previews — V2 mesh supersedes them
      if (manualMarkerGroupRef.current) {
        sceneRef.current.remove(manualMarkerGroupRef.current)
        manualMarkerGroupRef.current.children.forEach(c => {
          const m = c as THREE.Mesh
          if (m.material) (m.material as THREE.Material).dispose()
          if (m.geometry && m.geometry !== markerSharedGeoRef.current) m.geometry.dispose()
        })
        manualMarkerGroupRef.current = null
        if (markerSharedGeoRef.current) { markerSharedGeoRef.current.dispose(); markerSharedGeoRef.current = null }
      }

      // MANDATORY VERIFICATION: support bases must be at Y≈0 (bed level)
      geometry.computeBoundingBox()
      const bb = geometry.boundingBox!
      console.log('[V2 Return] support AABB: Y=[' + bb.min.y.toFixed(3) + ', ' + bb.max.y.toFixed(3) + ']',
        '| base at bed:', Math.abs(bb.min.y) < 0.5 ? 'YES' : 'NO (BROKEN)',
        '| offset applied:', supportMeshOffset ? `(${supportMeshOffset.x.toFixed(2)}, ${supportMeshOffset.y.toFixed(2)}, ${supportMeshOffset.z.toFixed(2)})` : 'none')
    } catch (err) {
      console.error('Failed to load V2 support mesh:', err)
    }
  }, [supportMeshBuffer, supportMeshOffset, sceneReady, selectedId])

  // ── Colored segment overlays (branches, braces — make types visually distinct) ──
  const segmentOverlayRef = useRef<THREE.Group | null>(null)

  useEffect(() => {
    if (!sceneReady || !sceneRef.current) return

    // Remove old overlays
    if (segmentOverlayRef.current) {
      sceneRef.current.remove(segmentOverlayRef.current)
      segmentOverlayRef.current.traverse(c => {
        if ((c as THREE.Mesh).geometry) (c as THREE.Mesh).geometry.dispose()
        if ((c as THREE.Mesh).material) ((c as THREE.Mesh).material as THREE.Material).dispose()
      })
      segmentOverlayRef.current = null
    }

    const points = _supportPoints ?? []
    const braces = _crossBraces ?? []
    if (points.length === 0 && braces.length === 0) return

    const group = new THREE.Group()
    group.name = 'support-type-overlays'

    // Helper: create a cylinder between two points
    const makeCylinder = (x1: number, y1: number, z1: number, x2: number, y2: number, z2: number, r: number, mat: THREE.Material) => {
      // Convert Z-up to Y-up: (x, z, y)
      const a = new THREE.Vector3(x1, z1, y1)
      const b = new THREE.Vector3(x2, z2, y2)
      const len = a.distanceTo(b)
      if (len < 0.01) return
      const geo = new THREE.CylinderGeometry(r, r, len, 6)
      const mesh = new THREE.Mesh(geo, mat)
      const mid = new THREE.Vector3().addVectors(a, b).multiplyScalar(0.5)
      mesh.position.copy(mid)
      const dir = new THREE.Vector3().subVectors(b, a).normalize()
      mesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir)
      group.add(mesh)
    }

    // Materials
    const branchMat = new THREE.MeshPhongMaterial({ color: 0xfbbf24, transparent: true, opacity: 0.85, depthWrite: false }) // amber for branches
    const trunkMat = new THREE.MeshPhongMaterial({ color: 0x22d3ee, transparent: true, opacity: 0.8, depthWrite: false }) // cyan for trunks
    const braceMat = new THREE.MeshPhongMaterial({ color: 0x818cf8, transparent: true, opacity: 0.8, depthWrite: false }) // indigo for braces

    // Draw branch & trunk segments from support data
    for (const sp of points) {
      if (!sp.segments) continue
      for (const seg of sp.segments) {
        if (seg.part === 'branch') {
          makeCylinder(seg.x1, seg.y1, seg.z1, seg.x2, seg.y2, seg.z2, Math.max(seg.r1, seg.r2) * 1.5, branchMat)
        }
        // Highlight thick trunk segments (radius > normal pillar — indicates shared trunk)
        if (seg.part === 'shaft' && Math.min(seg.r1, seg.r2) > 0.8) {
          makeCylinder(seg.x1, seg.y1, seg.z1, seg.x2, seg.y2, seg.z2, Math.max(seg.r1, seg.r2) * 1.2, trunkMat)
        }
      }
    }

    // Draw cross-braces in contrasting color
    for (const br of braces) {
      makeCylinder(br.x1, br.y1, br.z1, br.x2, br.y2, br.z2, br.diameter * 0.7, braceMat)
    }

    if (group.children.length > 0) {
      sceneRef.current.add(group)
      segmentOverlayRef.current = group
    }
  }, [_supportPoints, _crossBraces, sceneReady])

  // ── ALL models' support meshes (rendered simultaneously) ────────────────
  const allV2MeshesRef = useRef<Map<string, THREE.Mesh>>(new Map())

  useEffect(() => {
    if (!sceneReady || !sceneRef.current) return
    const scene = sceneRef.current
    const currentMeshes = allV2MeshesRef.current
    const incoming = allSupportMeshes ?? []
    const incomingIds = new Set(incoming.map(s => s.modelId))

    // Remove meshes for models no longer in the list
    for (const [id, mesh] of currentMeshes) {
      if (!incomingIds.has(id)) {
        scene.remove(mesh)
        mesh.geometry.dispose()
        ;(mesh.material as THREE.Material).dispose()
        currentMeshes.delete(id)
      }
    }

    // Add/update meshes for each model
    for (const entry of incoming) {
      if (!entry.buffer || entry.buffer.byteLength < 84) continue
      // Skip if already rendered with same buffer (check by byteLength as quick identity)
      const existing = currentMeshes.get(entry.modelId)
      if (existing && (existing.userData as any)._bufLen === entry.buffer.byteLength) continue

      // Remove old mesh for this model if exists
      if (existing) {
        scene.remove(existing)
        existing.geometry.dispose()
        ;(existing.material as THREE.Material).dispose()
        currentMeshes.delete(entry.modelId)
      }

      try {
        const loader = new STLLoader()
        const geometry = loader.parse(entry.buffer)
        const positions = geometry.getAttribute('position')
        // Reverse centering offset
        if (entry.offset) {
          for (let i = 0; i < positions.count; i++) {
            positions.setX(i, positions.getX(i) - entry.offset.x)
            positions.setY(i, positions.getY(i) - entry.offset.y)
            positions.setZ(i, positions.getZ(i) - entry.offset.z)
          }
        }
        // Z-up → Y-up
        for (let i = 0; i < positions.count; i++) {
          const y = positions.getY(i), z = positions.getZ(i)
          positions.setY(i, z); positions.setZ(i, y)
        }
        // Winding fix
        for (let i = 0; i < positions.count; i += 3) {
          const x1=positions.getX(i+1),y1=positions.getY(i+1),z1=positions.getZ(i+1)
          const x2=positions.getX(i+2),y2=positions.getY(i+2),z2=positions.getZ(i+2)
          positions.setXYZ(i+1, x2, y2, z2)
          positions.setXYZ(i+2, x1, y1, z1)
        }
        positions.needsUpdate = true
        // Index the support mesh (same win as model: de-indexed STL → shared vertices)
        geometry.deleteAttribute('normal')
        const indexedSupport = mergeVertices(geometry, 1e-4)
        geometry.dispose()

        const material = new THREE.MeshPhongMaterial({
          color: 0x14b8a6, specular: 0x444444, transparent: true, opacity: 0.7,
          shininess: 50, side: THREE.DoubleSide, depthWrite: false, flatShading: true,
        })
        const mesh = new THREE.Mesh(indexedSupport, material)
        mesh.renderOrder = 1
        ;(mesh.userData as any)._bufLen = entry.buffer.byteLength
        scene.add(mesh)
        currentMeshes.set(entry.modelId, mesh)
      } catch (err) {
        console.error(`Failed to load V2 support mesh for ${entry.modelId}:`, err)
      }
    }
  }, [allSupportMeshes, sceneReady])

  // ── Manual support proxy markers (visual feedback + delete raycast target) ──

  const manualMarkerGroupRef = useRef<THREE.Group | null>(null)
  const markerSharedGeoRef = useRef<THREE.SphereGeometry | null>(null)

  useEffect(() => {
    if (!sceneReady || !sceneRef.current) return

    // Remove old markers — dispose shared geometry ONCE, materials per-child
    if (manualMarkerGroupRef.current) {
      sceneRef.current.remove(manualMarkerGroupRef.current)
      manualMarkerGroupRef.current.children.forEach(c => {
        const m = c as THREE.Mesh
        if (m.material) (m.material as THREE.Material).dispose()
        if (m.geometry && m.geometry !== markerSharedGeoRef.current) m.geometry.dispose()
      })
      manualMarkerGroupRef.current = null
    }
    if (markerSharedGeoRef.current) {
      markerSharedGeoRef.current.dispose()
      markerSharedGeoRef.current = null
    }

    const markers = manualMarkers ?? []
    if (markers.length === 0) return

    const group = new THREE.Group()
    group.name = 'manual-support-markers'

    const loader = new STLLoader()

    markers.forEach(m => {
      // ── Determine status from engine or frontend hint ──
      const engineStatus = m.engineStatus ?? (m.uncoverable ? 'uncoverable' : undefined)
      const statusColors: Record<string, { marker: number; emissive: number; support: number }> = {
        routed:      { marker: 0xff6600, emissive: 0x331100, support: 0x14b8a6 },
        bundled:     { marker: 0x44cc88, emissive: 0x113322, support: 0x44cc88 },
        collision:   { marker: 0xff3333, emissive: 0x441111, support: 0xff3333 },
        uncoverable: { marker: 0xff0000, emissive: 0x440000, support: 0xff0000 },
        error:       { marker: 0xff00ff, emissive: 0x440044, support: 0xff00ff },
        pending:     { marker: 0xff6600, emissive: 0x331100, support: 0x44aaff },
      }
      const c = statusColors[engineStatus ?? 'pending']

      // ── Realistic tip marker: small sphere at actual tip radius + tapered cone ──
      const markerProvisional = m.provisional ?? false
      const tipRadius = (m.shaftDiameter ?? 0.5) / 2  // actual tip radius in mm
      const coneLength = Math.max(tipRadius * 3, 0.6)  // short tapered cone along normal
      const pillarRadius = tipRadius * 1.5

      const markerMat = new THREE.MeshPhongMaterial({
        color: c.marker, emissive: c.emissive,
        wireframe: markerProvisional,
        transparent: markerProvisional, opacity: markerProvisional ? 0.6 : 1.0,
      })

      // Tip sphere at contact point
      const isSelected = m.id === selectedManualSupportId
      const tipGeo = new THREE.SphereGeometry(tipRadius, 16, 16)
      const tipSphere = new THREE.Mesh(tipGeo, markerMat)
      tipSphere.position.set(m.x, m.y, m.z)
      tipSphere.userData = { supportPointId: m.id }
      group.add(tipSphere)

      // Selection highlight ring
      if (isSelected) {
        const ringGeo = new THREE.RingGeometry(tipRadius * 2, tipRadius * 2.8, 24)
        const ringMat = new THREE.MeshBasicMaterial({ color: 0x00ffff, side: THREE.DoubleSide, transparent: true, opacity: 0.8 })
        const ring = new THREE.Mesh(ringGeo, ringMat)
        ring.position.set(m.x, m.y, m.z)
        ring.lookAt(m.x, m.y + 1, m.z) // face up in Y-up space
        group.add(ring)
      }

      // Short tapered cone from tip downward (along -Y in viewer space)
      const coneGeo = new THREE.CylinderGeometry(tipRadius, pillarRadius, coneLength, 16)
      const coneMesh = new THREE.Mesh(coneGeo, markerMat)
      coneMesh.position.set(m.x, m.y - coneLength / 2, m.z)
      coneMesh.userData = { supportPointId: m.id }
      group.add(coneMesh)

      // ── Real engine geometry (replaces fake cylinder) ──
      if (m.engineMeshBase64) {
        try {
          const binary = atob(m.engineMeshBase64)
          const bytes = new Uint8Array(binary.length)
          for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
          const geo = loader.parse(bytes.buffer)

          // Same space conversion as V2 mesh: reverse centering + Z→Y swap + winding
          const positions = geo.getAttribute('position')
          if (m.engineMeshOffset) {
            for (let i = 0; i < positions.count; i++) {
              positions.setX(i, positions.getX(i) - m.engineMeshOffset.x)
              positions.setY(i, positions.getY(i) - m.engineMeshOffset.y)
              positions.setZ(i, positions.getZ(i) - m.engineMeshOffset.z)
            }
          }
          for (let i = 0; i < positions.count; i++) {
            const y = positions.getY(i), z = positions.getZ(i)
            positions.setY(i, z); positions.setZ(i, y)
          }
          for (let i = 0; i < positions.count; i += 3) {
            const x1=positions.getX(i+1),y1=positions.getY(i+1),z1=positions.getZ(i+1)
            const x2=positions.getX(i+2),y2=positions.getY(i+2),z2=positions.getZ(i+2)
            positions.setXYZ(i+1, x2, y2, z2)
            positions.setXYZ(i+2, x1, y1, z1)
          }
          positions.needsUpdate = true
          geo.computeVertexNormals()

          // Same material style as V2 auto mesh — reduced opacity when provisional
          const isProvisional = m.provisional ?? false
          const baseOpacity = engineStatus === 'routed' || engineStatus === 'bundled' ? 0.7 : 0.5
          const mat = new THREE.MeshPhongMaterial({
            color: c.support,
            specular: 0x444444,
            transparent: true,
            opacity: isProvisional ? baseOpacity * 0.5 : baseOpacity,
            shininess: 50,
            side: THREE.DoubleSide,
            depthWrite: false,
            wireframe: isProvisional, // provisional previews shown as wireframe
          })

          const supportMesh = new THREE.Mesh(geo, mat)
          supportMesh.renderOrder = 1
          group.add(supportMesh)
        } catch (err) {
          console.error('[ManualSupport] Failed to parse engine mesh:', err)
        }
      } else if (!engineStatus || engineStatus === 'pending') {
        // Pending engine call — show lightweight ghost (sphere only, no fake cylinder)
        // The marker sphere is already added above
      }
    })

    sceneRef.current.add(group)
    manualMarkerGroupRef.current = group

    // ── STEP J: marker render ──
    // Each marker produces 2 objects (tip sphere + cone) + optional engine mesh
    const markerObjects = group.children.filter(c => c.userData?.supportPointId)
    const uniqueIds = new Set(markerObjects.map(c => c.userData.supportPointId))
    const passJ = uniqueIds.size === markers.length
    console.log('MSADD', { step: 'J', pointsInState: markers.length, uniqueMarkerIds: uniqueIds.size, pass: passJ })
  }, [manualMarkers, sceneReady, selectedManualSupportId])

  // ── Support callback refs (avoid stale closures) ─────────────────────────
  const onSupportPointAddRef = useRef(onSupportPointAdd)
  onSupportPointAddRef.current = onSupportPointAdd
  const onSupportPointDeleteRef = useRef(onSupportPointDelete)
  onSupportPointDeleteRef.current = onSupportPointDelete
  const onSupportPointSelectRef = useRef(onSupportPointSelect)
  onSupportPointSelectRef.current = onSupportPointSelect
  const onPaintRegionAddRef = useRef(onPaintRegionAdd)
  onPaintRegionAddRef.current = onPaintRegionAdd

  // (Support editing is handled entirely by the main onMouseDown/onMouseUp handlers above.
  //  No separate click handler needed — eliminates the race condition.)

  // (callback refs declared above the click handler useEffect)

  // ── Cursor style for support editing mode ─────────────────────────────────

  useEffect(() => {
    if (!sceneReady) return
    const el = rendererRef.current?.domElement
    if (!el) return
    const mode = supportEditMode ?? 'none'
    el.style.cursor = mode === 'none' ? '' :
      mode === 'add' ? 'crosshair' :
      mode === 'delete' ? 'not-allowed' :
      'cell' // paint modes
    return () => { el.style.cursor = '' }
  }, [supportEditMode, sceneReady])

  return <div ref={mountRef} className={`w-full h-full ${className}`} />
})

export default StlViewer
