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
  const wb = new THREE.Box3().setFromObject(group, true)
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
    supportPoints,
    paintedRegions,
    supportTipType: _supportTipType,
    supportBrushSize: _supportBrushSize,
    onSupportPointAdd,
    onSupportPointDelete,
    onPaintRegionAdd,
    crossBraces,
    supportMeshBuffer,
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
  const loadingIdsRef = useRef<Set<string>>(new Set())
  const gizmoRef      = useRef<GizmoManager | null>(null)

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

      // ── 2. Check model meshes ─────────────────────────────────────────────
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

            geometry.computeVertexNormals()
            geometry.center()
            geometry.computeBoundingBox()
            const bb   = geometry.boundingBox!
            const size = new THREE.Vector3()
            bb.getSize(size)
            geometry.translate(0, size.y / 2, 0) // base at Y=0
            geometry.computeBoundingBox()         // refresh BB after translate so setFromObject is correct

            const mesh = new THREE.Mesh(
              geometry,
              new THREE.MeshPhongMaterial({ color: C_NORMAL, specular: 0x222222, shininess: 40 }),
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

            // Report natural size in print space (x=X, y=depth, z=height)
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

    const pts = supportPoints ?? []
    const regions = paintedRegions ?? []
    const braces = crossBraces ?? []
    const hasAnything = pts.length > 0 || regions.length > 0 || braces.length > 0 || raftData || skirtData

    // If V2 watertight mesh is available, skip legacy segment rendering
    // (V2 mesh is rendered separately with proper geometry)
    const hasV2Mesh = supportMeshBuffer && supportMeshBuffer.byteLength > 84
    if (!hasAnything && !hasV2Mesh) return
    // When V2 mesh exists, only render raft/skirt from legacy, skip support segments
    const skipLegacySupports = !!hasV2Mesh

    const group = new THREE.Group()
    group.name = 'support-visuals'

    // ── PrusaSlicer-style support rendering ─────────────────────────────────
    // Unified teal/green color like Lychee. Sphere joints at connections.
    // Proper cone bases. Smooth visual transitions.
    const SUPPORT_COLOR = 0x2dd4bf   // teal-400 — like Lychee/ChiTuBox
    const BRACE_COLOR   = 0x14b8a6   // teal-500
    const TIP_COLOR     = 0xfbbf24   // amber — highlight contact points
    const BASE_COLOR    = 0x6366f1   // indigo — platform on bed

    const supportMat = new THREE.MeshPhongMaterial({ color: SUPPORT_COLOR, transparent: true, opacity: 0.7, shininess: 30 })
    const tipMat     = new THREE.MeshPhongMaterial({ color: TIP_COLOR, transparent: true, opacity: 0.85, shininess: 40 })
    const baseMat    = new THREE.MeshPhongMaterial({ color: BASE_COLOR, transparent: true, opacity: 0.75 })
    const braceMat   = new THREE.MeshPhongMaterial({ color: BRACE_COLOR, transparent: true, opacity: 0.5 })

    // Helper: add a cylinder between two print-space points
    const addCyl = (x1: number, y1: number, z1: number, r1: number,
                    x2: number, y2: number, z2: number, r2: number,
                    mat: THREE.Material, detail: number, pid: string) => {
      const h = Math.sqrt((x2-x1)**2 + (y2-y1)**2 + (z2-z1)**2)
      if (h < 0.005) return
      const geo = new THREE.CylinderGeometry(Math.max(0.02, r1), Math.max(0.02, r2), h, detail)
      const mesh = new THREE.Mesh(geo, mat)
      mesh.position.set((x1+x2)/2, (z1+z2)/2, (y1+y2)/2)
      const dir = new THREE.Vector3(x2-x1, z2-z1, y2-y1).normalize()
      if (dir.length() > 0.01) mesh.setRotationFromQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0,1,0), dir))
      mesh.userData = { supportPointId: pid }
      group.add(mesh)
    }

    // Helper: add a sphere joint at a print-space point
    const addSphere = (x: number, y: number, z: number, r: number,
                       mat: THREE.Material, pid: string) => {
      if (r < 0.01) return
      const geo = new THREE.SphereGeometry(r, 8, 6)
      const mesh = new THREE.Mesh(geo, mat)
      mesh.position.set(x, z, y) // print→three: x=x, y=z, z=y
      mesh.userData = { supportPointId: pid }
      group.add(mesh)
    }

    // Skip legacy cylinder rendering when V2 watertight mesh is available
    if (!skipLegacySupports) pts.forEach(p => {
      if (p.segments && p.segments.length > 0) {
        p.segments.forEach((seg, i) => {
          const isTip = seg.part === 'tip'
          const isBase = seg.part === 'base'
          const mat2 = isTip ? tipMat : isBase ? baseMat : supportMat
          const detail = isTip ? 10 : isBase ? 10 : 8

          // Draw the cylinder segment
          addCyl(seg.x1, seg.y1, seg.z1, seg.r1, seg.x2, seg.y2, seg.z2, seg.r2, mat2, detail, p.id)

          // Add sphere joint at the START of each segment (smooth connection)
          const jr = Math.max(seg.r1, seg.r2) * 0.9
          if (i === 0 && isTip) {
            // Tip contact point — small amber sphere
            addSphere(seg.x1, seg.y1, seg.z1, Math.max(seg.r1, 0.1), tipMat, p.id)
          } else if (seg.part === 'shaft' || seg.part === 'upperTaper') {
            // Junction sphere where segments connect
            addSphere(seg.x1, seg.y1, seg.z1, jr, supportMat, p.id)
          }

          // Base bottom — flat disc sphere
          if (isBase && i === p.segments!.length - 1) {
            addSphere(seg.x2, seg.y2, seg.z2, Math.max(seg.r2, 0.2), baseMat, p.id)
          }
        })
      } else {
        // Simple fallback
        addSphere(p.x, p.y, p.z, 0.25, tipMat, p.id)
        if (p.z > 0.1) addCyl(p.x, p.y, p.z, 0.1, p.x, p.y, 0, 0.1, supportMat, 6, p.id)
      }
    })

    // Render cross-braces (skip when V2 mesh handles everything)
    if (!skipLegacySupports) braces.forEach(b => {
      const h = Math.sqrt((b.x2-b.x1)**2 + (b.y2-b.y1)**2 + (b.z2-b.z1)**2)
      if (h < 0.01) return
      const r = Math.max(0.05, b.diameter / 2)
      addCyl(b.x1, b.y1, b.z1, r, b.x2, b.y2, b.z2, r, braceMat, 5, '')
      addSphere(b.x1, b.y1, b.z1, r * 1.1, braceMat, '')
      addSphere(b.x2, b.y2, b.z2, r * 1.1, braceMat, '')
    })

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
  }, [supportPoints, paintedRegions, crossBraces, supportMeshBuffer, raftData, skirtData, sceneReady])

  // ── V2 Support mesh rendering (single watertight mesh) ─────────────────

  const v2MeshRef = useRef<THREE.Mesh | null>(null)

  useEffect(() => {
    if (!sceneReady) return
    const scene = sceneRef.current!

    // Remove old V2 mesh
    if (v2MeshRef.current) {
      scene.remove(v2MeshRef.current)
      v2MeshRef.current.geometry.dispose()
      ;(v2MeshRef.current.material as THREE.Material).dispose()
      v2MeshRef.current = null
    }

    if (!supportMeshBuffer || supportMeshBuffer.byteLength < 84) return

    // Parse the binary STL buffer directly into Three.js geometry
    try {
      const loader = new STLLoader()
      const geometry = loader.parse(supportMeshBuffer)
      geometry.computeVertexNormals()

      // The STL is in print-space (X, Y, Z). Three.js uses Y-up.
      // Transform: swap Y and Z axes
      const positions = geometry.getAttribute('position')
      const normals = geometry.getAttribute('normal')
      for (let i = 0; i < positions.count; i++) {
        const y = positions.getY(i)
        const z = positions.getZ(i)
        positions.setY(i, z) // Three.js Y = print Z (height)
        positions.setZ(i, y) // Three.js Z = print Y (depth)
        if (normals) {
          const ny = normals.getY(i)
          const nz = normals.getZ(i)
          normals.setY(i, nz)
          normals.setZ(i, ny)
        }
      }
      positions.needsUpdate = true
      if (normals) normals.needsUpdate = true

      const material = new THREE.MeshPhongMaterial({
        color: 0x14b8a6, // teal-500 — professional slicer look
        specular: 0x555555,
        transparent: true,
        opacity: 0.75,
        shininess: 60,
        side: THREE.DoubleSide,
        depthWrite: true,
        flatShading: false, // smooth shading for sphere-cone geometry
      })

      const mesh = new THREE.Mesh(geometry, material)

      // The V2 mesh is already in centered print-space (XY=0 center, Z bottom=0)
      // which matches the frontend model centering. No additional transform needed
      // beyond the Y/Z swap already applied above.

      scene.add(mesh)
      v2MeshRef.current = mesh
    } catch (err) {
      console.error('Failed to load V2 support mesh:', err)
    }
  }, [supportMeshBuffer, sceneReady])

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

    const onClick = (e: MouseEvent) => {
      // Only handle left click, ignore if dragging
      if (e.button !== 0) return

      raycaster.setFromCamera(toNDC(e), camera)

      if (mode === 'add') {
        // Raycast against model meshes to find surface point
        const meshes: THREE.Mesh[] = []
        meshMapRef.current.forEach(d => meshes.push(d.mesh))
        const hits = raycaster.intersectObjects(meshes, false)
        if (hits.length > 0) {
          const hit = hits[0]
          // Convert Three.js position back to print-space (x=x, y=z, z=y)
          const wp = hit.point
          const wn = hit.face?.normal ?? new THREE.Vector3(0, 1, 0)
          onSupportPointAddRef.current?.(wp.x, wp.z, wp.y, wn.x, wn.z, wn.y)
          e.stopPropagation()
        }
      } else if (mode === 'delete') {
        // Raycast against support point spheres
        if (supportGroupRef.current) {
          const supportMeshes = supportGroupRef.current.children.filter(
            c => (c as THREE.Mesh).userData?.supportPointId
          )
          const hits = raycaster.intersectObjects(supportMeshes, false)
          if (hits.length > 0) {
            const id = hits[0].object.userData.supportPointId
            onSupportPointDeleteRef.current?.(id)
            e.stopPropagation()
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

    renderer.domElement.addEventListener('click', onClick)
    return () => renderer.domElement.removeEventListener('click', onClick)
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
