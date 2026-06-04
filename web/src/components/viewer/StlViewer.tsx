import { useEffect, useRef, useCallback, forwardRef, useImperativeHandle, useState } from 'react'
import * as THREE from 'three'
import { STLLoader } from 'three/examples/jsm/loaders/STLLoader.js'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { GizmoManager } from './GizmoManager'

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
  onSupportPointAdd?: (x: number, y: number, z: number, nx: number, ny: number, nz: number) => void
  onSupportPointDelete?: (id: string) => void
  onPaintRegionAdd?: (mode: 'enforcer' | 'blocker', cx: number, cy: number, cz: number) => void
  crossBraces?: CrossBraceDisplayData[]
  // V2 support mesh (binary STL ArrayBuffer) — renders as single mesh instead of individual cylinders
  supportMeshBuffer?: ArrayBuffer | null
  // Backend centering offset — used to reverse XY/Z centering on the return path
  supportMeshOffset?: { x: number; y: number; z: number } | null
  // Manual support markers — rendered as proxy spheres + preview pillars
  manualMarkers?: { id: string; x: number; y: number; z: number; shaftDiameter: number; uncoverable: boolean }[]
  // True once commitOrientation has completed — clicks are blocked until this is true
  orientationCommitted?: boolean
  // Raft/Skirt visualization
  raftData?: { type: string; minX: number; minY: number; maxX: number; maxY: number; thicknessMm: number } | null
  skirtData?: { minX: number; minY: number; maxX: number; maxY: number; layers: number; distanceMm: number; widthMm: number } | null
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
  const modelMesh = group.children[0]
  if (!modelMesh) return false
  const wb = new THREE.Box3().setFromObject(modelMesh, true)
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
    onPaintRegionAdd,
    crossBraces: _crossBraces,
    supportMeshBuffer,
    supportMeshOffset,
    manualMarkers,
    orientationCommitted,
    raftData,
    skirtData,
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

  // Support edit mode ref (avoids stale closure in mousedown/mouseup handler)
  const supportEditModeRef = useRef(supportEditMode)
  supportEditModeRef.current = supportEditMode
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
    renderer.setPixelRatio(window.devicePixelRatio)
    renderer.shadowMap.enabled = true
    mount.appendChild(renderer.domElement)
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
          // Use draggingIdRef to track gizmo drags too (sentinel value)
          draggingIdRef.current = '__gizmo__'
        }
        return
      }

      // ── 2. In support edit mode, skip drag/face-select — let the click handler do its job
      if (supportEditModeRef.current && supportEditModeRef.current !== 'none') return

      // ── 3. Check model meshes ─────────────────────────────────────────────
      const allMeshes: THREE.Mesh[] = []
      for (const d of meshMapRef.current.values()) allMeshes.push(d.mesh)

      if (allMeshes.length === 0) {
        emptyClickPxRef.current = { x: e.clientX, y: e.clientY }
        return
      }

      const hits = raycaster.intersectObjects(allMeshes, false)
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
            // Re-seat on bed after rotation
            if (result.rotationChanged) {
              const wb = new THREE.Box3().setFromObject(data.group, true)
              if (wb.min.y < 0) data.group.position.y -= wb.min.y
            }
            data.currentTransform = extractTransform(data.group, data.currentTransform)
            onBoundsChangeRef.current?.(selId, modelIsOOB(data.group, buildVolumeRef.current))
            const now = Date.now()
            if (now - lastCbMs >= 40) {
              lastCbMs = now
              const wb = new THREE.Box3().setFromObject(data.group)
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
      if (boxHelperRef.current) {
        const selId = selectedIdRef.current
        if (selId) {
          const d = meshMapRef.current.get(selId)
          if (d) boxHelperRef.current.box.setFromObject(d.group, true)
        }
      }
      gizmo.update()   // reposition + rescale gizmo every frame
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
            geometry.computeVertexNormals()
            geometry.center()
            geometry.computeBoundingBox()
            const bb   = geometry.boundingBox!
            const size = new THREE.Vector3()
            bb.getSize(size)
            geometry.translate(0, size.y / 2, 0) // base at Y=0 (Y is now height)
            geometry.computeBoundingBox()
            geometry.computeBoundingSphere()

            const mesh = new THREE.Mesh(
              geometry,
              new THREE.MeshPhongMaterial({ color: C_NORMAL, specular: 0x222222, shininess: 40, side: THREE.DoubleSide }),
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
      geometry.computeVertexNormals()

      const material = new THREE.MeshPhongMaterial({
        color: 0x14b8a6,
        specular: 0x444444,
        transparent: true,
        opacity: 0.7,
        shininess: 50,
        side: THREE.DoubleSide,
        depthWrite: false,
      })

      const mesh = new THREE.Mesh(geometry, material)
      mesh.renderOrder = 1 // render after model so transparency works correctly

      // Add to SCENE at identity — NOT to model group.
      // Both model and supports are in world space at identity.
      sceneRef.current.add(mesh)
      v2MeshRef.current = mesh

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

    // Scale marker radius to model bbox diagonal, clamped to [0.3, 2.0] mm
    let markerRadius = 0.5
    const selId = selectedIdRef.current
    const selData = selId ? meshMapRef.current.get(selId) : null
    if (selData) {
      const diag = selData.naturalSize.length()
      markerRadius = Math.max(0.3, Math.min(2.0, diag * 0.012))
    }

    const group = new THREE.Group()
    group.name = 'manual-support-markers'

    const sharedGeo = new THREE.SphereGeometry(markerRadius, 10, 10)
    markerSharedGeoRef.current = sharedGeo

    // Collect raycast targets: model meshes + existing V2 support mesh
    const modelMeshes: THREE.Mesh[] = []
    meshMapRef.current.forEach(d => modelMeshes.push(d.mesh))
    const allTargets = [...modelMeshes]
    if (v2MeshRef.current) allTargets.push(v2MeshRef.current)

    const downDir = new THREE.Vector3(0, -1, 0) // world -Y = build direction (Y-up)
    const rc = new THREE.Raycaster()
    const DRAINAGE_CLEARANCE = 1.0 // mm — resin drainage spacing

    markers.forEach(m => {
      const shaftR = Math.max(0.1, (m.shaftDiameter ?? 0.6) / 2)

      // ── Raycast straight down from just below contact ──
      const startY = m.y - markerRadius * 0.5
      rc.set(new THREE.Vector3(m.x, startY, m.z), downDir)
      rc.far = startY + 1 // don't go below Y=-1

      const hits = rc.intersectObjects(allTargets, false)

      let pillarEndY = 0
      let status: 'clear' | 'collision' | 'bundle' = 'clear'

      for (const hit of hits) {
        if (hit.distance < shaftR * 2) continue // skip contact surface itself
        const isExistingSupport = hit.object === v2MeshRef.current
        if (isExistingSupport) {
          // -Z ray meets existing support column → will bundle
          pillarEndY = hit.point.y
          status = 'bundle'
          break
        }
        const isModel = modelMeshes.includes(hit.object as THREE.Mesh)
        if (isModel) {
          // Pillar passes through model surface → collision
          pillarEndY = hit.point.y
          status = 'collision'
          break
        }
      }

      // ── Proximity clearance check (drainage spacing) ──
      // Cast 4 offset rays around the pillar to detect features within 1mm
      if (status === 'clear') {
        const offsets = [
          new THREE.Vector3(DRAINAGE_CLEARANCE, 0, 0),
          new THREE.Vector3(-DRAINAGE_CLEARANCE, 0, 0),
          new THREE.Vector3(0, 0, DRAINAGE_CLEARANCE),
          new THREE.Vector3(0, 0, -DRAINAGE_CLEARANCE),
        ]
        const pillarHeight = m.y - pillarEndY
        for (const off of offsets) {
          // Horizontal ray from pillar midpoint outward, short range
          rc.set(new THREE.Vector3(m.x, m.y - pillarHeight * 0.5, m.z), off.normalize())
          rc.far = DRAINAGE_CLEARANCE + shaftR
          const proxHits = rc.intersectObjects(modelMeshes, false)
          if (proxHits.length > 0 && proxHits[0].distance < DRAINAGE_CLEARANCE + shaftR) {
            status = 'collision' // too close to model feature
            break
          }
        }
      }

      // Override with uncoverable from backend
      if (m.uncoverable) status = 'collision'

      // ── Colors ──
      const colors = {
        clear:     { marker: 0xff6600, emissive: 0x331100, pillar: 0x44aaff },
        collision: { marker: 0xff3333, emissive: 0x441111, pillar: 0xff3333 },
        bundle:    { marker: 0x44cc88, emissive: 0x113322, pillar: 0x44cc88 },
      }
      const c = colors[status]

      // ── Contact marker sphere ──
      const markerMat = new THREE.MeshPhongMaterial({ color: c.marker, emissive: c.emissive })
      const sphere = new THREE.Mesh(sharedGeo, markerMat)
      sphere.position.set(m.x, m.y, m.z)
      sphere.userData = { supportPointId: m.id }
      group.add(sphere)

      // ── Preview pillar ──
      const pillarHeight = m.y - pillarEndY
      if (pillarHeight < 0.1) return

      const pillarGeo = new THREE.CylinderGeometry(shaftR, shaftR, pillarHeight, 8)
      const pillarMat = new THREE.MeshPhongMaterial({
        color: c.pillar, transparent: true, opacity: 0.3, depthWrite: false,
      })
      const pillar = new THREE.Mesh(pillarGeo, pillarMat)
      pillar.position.set(m.x, pillarEndY + pillarHeight / 2, m.z)
      pillar.renderOrder = 2
      group.add(pillar)
    })

    sceneRef.current.add(group)
    manualMarkerGroupRef.current = group

    // ── Step I+J instrumentation ──
    const sphereCount = group.children.filter(c => c.userData?.supportPointId).length
    const pillarCount = group.children.length - sphereCount
    console.log(`[TRACE] I marker-render`, JSON.stringify({
      markersInScene: sphereCount,
      pointsInState: markers.length,
      pass: sphereCount === markers.length,
    }))
    console.log(`[TRACE] J preview-pillars`, JSON.stringify({
      pillarCreated: pillarCount,
      markerCount: markers.length,
    }))
  }, [manualMarkers, sceneReady])

  // ── Support callback refs (avoid stale closures) ─────────────────────────
  const onSupportPointAddRef = useRef(onSupportPointAdd)
  onSupportPointAddRef.current = onSupportPointAdd
  const onSupportPointDeleteRef = useRef(onSupportPointDelete)
  onSupportPointDeleteRef.current = onSupportPointDelete
  const onPaintRegionAddRef = useRef(onPaintRegionAdd)
  onPaintRegionAddRef.current = onPaintRegionAdd

  // ── Support editing click handler ─────────────────────────────────────────

  useEffect(() => {
    if (!sceneReady) return
    const mode = supportEditMode ?? 'none'
    if (mode === 'none') return

    const renderer = rendererRef.current!
    const camera = cameraRef.current!
    const raycaster = new THREE.Raycaster()

    const toNDC = (e: MouseEvent) => {
      const rect = renderer.domElement.getBoundingClientRect()
      return new THREE.Vector2(
        ((e.clientX - rect.left) / rect.width) * 2 - 1,
        -((e.clientY - rect.top) / rect.height) * 2 + 1
      )
    }

    // Track pointer down position to distinguish click from drag/orbit
    let pointerDownPx = { x: 0, y: 0 }
    const onPointerDown = (e: PointerEvent) => { pointerDownPx = { x: e.clientX, y: e.clientY } }

    const onClick = (e: MouseEvent) => {
      const traceId = Math.random().toString(36).slice(2, 8)
      const T = (step: string, data: Record<string, unknown>) => console.log(`[TRACE ${traceId}] ${step}`, JSON.stringify(data))

      // ── Step A: click fired ──
      const dx = e.clientX - pointerDownPx.x, dy = e.clientY - pointerDownPx.y
      const dragPx = Math.sqrt(dx * dx + dy * dy)
      const passA = e.button === 0 && dragPx <= 4
      T('A click', { button: e.button, dragPx: +dragPx.toFixed(1), pass: passA })
      if (!passA) return

      // ── Step B: orientation gate ──
      const passB = !!orientationCommittedRef.current
      T('B orientation', { orientationCommitted: passB, pass: passB })
      if (!passB) return

      // ── Step C: identity gate ──
      raycaster.setFromCamera(toNDC(e), camera)
      const identity4 = new THREE.Matrix4()
      let passC = true
      for (const [id, d] of meshMapRef.current) {
        const eq = d.group.matrixWorld.equals(identity4)
        if (!eq) { T('C identity', { modelId: id, matrixIsIdentity: false, pass: false }); passC = false }
      }
      if (!passC) return
      T('C identity', { matrixIsIdentity: true, pass: true })

      if (mode === 'add') {
        // ── Step D: raycast inputs ──
        const meshes: THREE.Mesh[] = []
        const dInfo: Record<string, unknown>[] = []
        meshMapRef.current.forEach((d, id) => {
          meshes.push(d.mesh)
          const geo = d.mesh.geometry
          const pos = geo.getAttribute('position')
          const bt = (geo as any).boundsTree
          dInfo.push({
            modelId: id,
            geoId: geo.uuid,
            vertexCount: pos?.count ?? 0,
            hasBoundingSphere: !!geo.boundingSphere,
            boundingSphereR: geo.boundingSphere?.radius?.toFixed(2) ?? 'null',
            hasBoundsTree: !!bt,
            boundsTreeVertexCount: bt?._roots?.[0]?.byteLength != null ? 'exists' : 'none',
            meshVisible: d.mesh.visible,
            meshParent: d.mesh.parent?.type ?? 'null',
          })
        })
        T('D raycast-inputs', { meshCount: meshes.length, meshes: dInfo })

        // ── Step E: raycast result ──
        const hits = raycaster.intersectObjects(meshes, false)
        const passE = hits.length > 0
        T('E raycast-result', {
          hitCount: hits.length,
          pass: passE,
          ...(passE ? {
            hitGeoId: (hits[0].object as THREE.Mesh).geometry?.uuid,
            hitFaceIndex: hits[0].faceIndex,
            hitPointWorld: [+hits[0].point.x.toFixed(3), +hits[0].point.y.toFixed(3), +hits[0].point.z.toFixed(3)],
            hitDistance: +hits[0].distance.toFixed(3),
          } : {}),
        })
        if (!passE) return

        const hit = hits[0]
        const wp = hit.point
        const wn = hit.face?.normal ?? new THREE.Vector3(0, -1, 0)

        // ── Step F: derived normal ──
        const passF = isFinite(wn.x) && isFinite(wn.y) && isFinite(wn.z) && wn.lengthSq() > 0.001
        T('F normal', { nx: +wn.x.toFixed(4), ny: +wn.y.toFixed(4), nz: +wn.z.toFixed(4), pass: passF })

        // ── Step G: callback invoked ──
        const hasCallback = !!onSupportPointAddRef.current
        T('G callback', { hasCallback, argsCount: 6, pass: hasCallback })
        if (hasCallback) {
          onSupportPointAddRef.current!(wp.x, wp.y, wp.z, wn.x, wn.y, wn.z)
        }
        e.stopPropagation()
      } else if (mode === 'delete') {
        // Raycast against manual support proxy markers
        if (manualMarkerGroupRef.current) {
          const hits = raycaster.intersectObjects(manualMarkerGroupRef.current.children, false)
          if (hits.length > 0) {
            const id = hits[0].object.userData.supportPointId
            if (id) {
              onSupportPointDeleteRef.current?.(id)
              e.stopPropagation()
            }
          }
        }
      } else if (mode === 'paint-enforcer' || mode === 'paint-blocker') {
        const meshes: THREE.Mesh[] = []
        meshMapRef.current.forEach(d => meshes.push(d.mesh))
        const hits = raycaster.intersectObjects(meshes, false)
        if (hits.length > 0) {
          const wp = hits[0].point
          const paintMode = mode === 'paint-enforcer' ? 'enforcer' : 'blocker'
          onPaintRegionAddRef.current?.(paintMode, wp.x, wp.z, wp.y)
          e.stopPropagation()
        }
      }
    }

    renderer.domElement.addEventListener('pointerdown', onPointerDown)
    renderer.domElement.addEventListener('click', onClick)
    return () => {
      renderer.domElement.removeEventListener('pointerdown', onPointerDown)
      renderer.domElement.removeEventListener('click', onClick)
    }
  }, [supportEditMode, sceneReady])

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
