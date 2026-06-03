import { useState, useCallback, useRef, useMemo } from 'react'
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
import { machineProfilesApi, resinPrintProfilesApi, resinSliceApi, meshApi, supportV2Api, type AdvancedSupportData, type CrossBraceData } from '../api/client'

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

// ── Manual support data (per-object) ──────────────────────────────────────────

interface SupportPoint {
  id: string
  x: number; y: number; z: number    // contact point on mesh surface (mm)
  tipDiameterMm: number              // support tip size
  type: 'light' | 'medium' | 'heavy'
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
}

const EMPTY_PREP: PrepState = {
  autoSupports: [], advancedSupports: [], crossBraces: [],
  raft: null, skirt: null,
  locked: false, stale: false, generatedAt: null,
  v2Stats: null, v2MeshBuffer: null,
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

  // ── Job-level support state (driven by active print profile, overridable) ──
  const [jobSupportEnabled, setJobSupportEnabled] = useState(() => _savedSupportEnabled)
  const [jobSupportType, setJobSupportType] = useState<'normal' | 'tree'>(() => _savedSupportType)
  const [jobSupportPlacement, setJobSupportPlacement] = useState<'buildplate' | 'everywhere'>(() => _savedSupportPlacement)

  // Support editing mode
  type SupportEditMode = 'none' | 'add' | 'delete' | 'paint-enforcer' | 'paint-blocker'
  const [supportEditMode, setSupportEditMode] = useState<SupportEditMode>('none')
  const [supportBrushSize, setSupportBrushSize] = useState(3) // mm
  const [supportTipType, setSupportTipType] = useState<'light' | 'medium' | 'heavy'>('medium')

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
    if (!file.name.toLowerCase().endsWith('.stl')) return
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

  const addSupportPoint = (x: number, y: number, z: number) => {
    if (!selectedId) return
    const tipDiameter = supportTipType === 'light' ? 0.3 : supportTipType === 'medium' ? 0.5 : 0.8
    const point: SupportPoint = { id: mkId(), x, y, z, tipDiameterMm: tipDiameter, type: supportTipType }
    updateModels(prev => prev.map(m =>
      m.id === selectedId ? { ...m, manualSupports: { ...m.manualSupports, points: [...m.manualSupports.points, point] } } : m
    ))
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

  // ── Auto-support generation ───────────────────────────────────────────────

  const [autoSupportConfig, setAutoSupportConfig] = useState({
    overhangAngle: 45, density: 0.5, tipDiameter: 0.4,
    supportType: 'medium' as string,
    crossBracing: true,
    raftEnabled: false, raftType: 'grid' as string,
    skirtEnabled: false, skirtLayers: 3, skirtDistance: 2.0,
    supportExposurePct: 100,
    // V2 advanced features
    treeSupports: true,
    hollowSupports: true,
    hollowMinHeight: 20,
    hollowWallThickness: 0.6,
    latticePattern: 'grid' as string, // grid | honeycomb | cross | solid
    miniRafts: true,
    raftMargin: 1.5,
    raftThickness: 0.3,
    materialPreset: 'standard' as string, // standard | tough | flexible | castable | dental
  })
  const [generating, setGenerating] = useState(false)

  const generateAutoSupports = async () => {
    if (!selectedId || !selected) return
    setGenerating(true)
    try {
      const resp = await fetch(selected.url)
      const blob = await resp.blob()
      const fd = new FormData()
      fd.append('stlFile', blob, selected.fileName)
      fd.append('orientation', activePrinter?.orientation ?? 'BottomUp')
      if (selectedPrinterId) fd.append('printerId', selectedPrinterId)
      fd.append('overhangAngleDeg', String(autoSupportConfig.overhangAngle))
      fd.append('density', String(autoSupportConfig.density))
      fd.append('tipDiameterMm', String(autoSupportConfig.tipDiameter))
      fd.append('supportType', autoSupportConfig.supportType)
      fd.append('placement', jobSupportPlacement)
      fd.append('crossBracingEnabled', String(autoSupportConfig.crossBracing))
      fd.append('raftEnabled', String(autoSupportConfig.raftEnabled))
      fd.append('raftType', autoSupportConfig.raftType)
      fd.append('skirtEnabled', String(autoSupportConfig.skirtEnabled))
      fd.append('skirtLayers', String(autoSupportConfig.skirtLayers))
      fd.append('skirtDistanceMm', String(autoSupportConfig.skirtDistance))

      // Map preset selection to V2 engine parameters
      const presetMap: Record<string, { pin: number; back: number; pillar: number; base: number }> = {
        'light':          { pin: 0.1,  back: 0.3,  pillar: 0.3,  base: 1.2 },
        'medium':         { pin: 0.2,  back: 0.5,  pillar: 0.5,  base: 2.0 },
        'heavy':          { pin: 0.4,  back: 0.75, pillar: 0.75, base: 3.0 },
        'point-tip':      { pin: 0.08, back: 0.3,  pillar: 0.35, base: 1.0 },
        'needle-tip':     { pin: 0.05, back: 0.2,  pillar: 0.3,  base: 1.0 },
        'mushroom-tip':   { pin: 0.3,  back: 0.5,  pillar: 0.5,  base: 2.0 },
        'cross-tip':      { pin: 0.3,  back: 0.5,  pillar: 0.5,  base: 2.2 },
      }
      const preset = presetMap[autoSupportConfig.supportType] ?? presetMap['medium']
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

      updateModels(prev => prev.map(m => m.id === selectedId ? {
        ...m, prep: {
          autoSupports: v2Result.supports.map((s: any) => ({
            x: s.contactX, y: s.contactY, contactZ: s.contactZ, baseZ: s.baseZ,
            tipDiameter: s.preset?.tipDiameterMm ?? 0.4, columnDiameter: s.preset?.shaftDiameterMm ?? 0.8, baseDiameter: s.preset?.baseDiameterMm ?? 2.0,
          })),
          advancedSupports: v2Result.supports,
          crossBraces: v2Result.crossBraces,
          raft: null,
          skirt: null,
          locked: true,
          stale: false,
          generatedAt: Date.now(),
          v2Stats,
          v2MeshBuffer: meshBuffer,
        }
      } : m))
    } catch (err: any) {
      console.error('Auto-support failed:', err)
      alert('Support generation failed: ' + (err?.message || 'Unknown error'))
    } finally {
      setGenerating(false)
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
                    updateModels(prev => prev.map(m => m.id === id ? { ...m, size } : m))
                  }}
                  onBoundsChange={(id, out) => {
                    updateModels(prev => prev.map(m => m.id === id ? { ...m, isOutOfBounds: out } : m))
                  }}
                  buildVolume={buildVolume}
                  supportEditMode={supportEditMode}
                  supportPoints={[
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
                  ]}
                  crossBraces={selectedPrep.crossBraces}
                  supportMeshBuffer={selectedPrep.v2MeshBuffer}
                  paintedRegions={selectedSupportData.paintedRegions}
                  supportTipType={supportTipType}
                  supportBrushSize={supportBrushSize}
                  onSupportPointAdd={(x, y, z) => addSupportPoint(x, y, z)}
                  onSupportPointDelete={(id) => deleteSupportPoint(id)}
                  onPaintRegionAdd={(mode, cx, cy, cz) => addPaintedRegion(mode, cx, cy, cz)}
                  raftData={selectedPrep.raft}
                  skirtData={selectedPrep.skirt}
                />
                <label className="absolute bottom-3 right-3 cursor-pointer text-xs px-3 py-1.5 rounded-lg
                                  bg-gray-800/80 hover:bg-gray-700/90 text-gray-400 hover:text-gray-200
                                  transition backdrop-blur-sm border border-gray-700/50">
                  + Add Model
                  <input type="file" accept=".stl" multiple className="hidden" onChange={handleFileInput} />
                </label>
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
                  <input type="file" accept=".stl" multiple className="hidden" onChange={handleFileInput} />
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

                  {/* Auto Support Generation */}
                  <div className="bg-gray-800/50 rounded-xl p-3">
                    <h3 className="text-xs font-semibold text-green-400 uppercase tracking-wider mb-2">Auto Support (V2 Engine)</h3>
                    {/* Support type selector — V2 presets */}
                    <select value={autoSupportConfig.supportType}
                      onChange={e => setAutoSupportConfig(p => ({ ...p, supportType: e.target.value }))}
                      className="w-full bg-gray-800 border border-gray-700 rounded-lg px-2 py-1.5 text-[10px] text-gray-200 mb-2">
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
                    <div className="space-y-1.5">
                      <label className="flex items-center justify-between text-[10px]">
                        <span className="text-gray-500">Overhang Angle</span>
                        <div className="flex items-center gap-1">
                          <input type="number" value={autoSupportConfig.overhangAngle} min={10} max={80} step={5}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, overhangAngle: +e.target.value }))}
                            className="w-12 bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-right" />
                          <span className="text-gray-600 text-[9px]">deg</span>
                        </div>
                      </label>
                      <label className="flex items-center justify-between text-[10px]">
                        <span className="text-gray-500">Density</span>
                        <input type="range" min={0.1} max={1} step={0.1} value={autoSupportConfig.density}
                          onChange={e => setAutoSupportConfig(p => ({ ...p, density: +e.target.value }))}
                          className="w-20 h-1 accent-green-500" />
                      </label>
                      <label className="flex items-center justify-between text-[10px]">
                        <span className="text-gray-500">Tip Size</span>
                        <div className="flex items-center gap-1">
                          <input type="number" value={autoSupportConfig.tipDiameter} min={0.1} max={2} step={0.1}
                            onChange={e => setAutoSupportConfig(p => ({ ...p, tipDiameter: +e.target.value }))}
                            className="w-12 bg-gray-800 border border-gray-700 rounded px-1 py-0.5 text-[10px] text-gray-200 text-right" />
                          <span className="text-gray-600 text-[9px]">mm</span>
                        </div>
                      </label>
                    </div>

                    {/* Support Exposure */}
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
                            alert(`Best orientation: ${best.description}\nOverhang: ${best.overhangAreaMm2.toFixed(0)}mm²\nEst. supports: ${best.estimatedSupports}`)
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
                      {generating ? 'Generating (V2 Engine)...' : selectedPrep.generatedAt ? 'Regenerate (V2)' : 'Generate Supports (V2)'}
                    </button>
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
                        <button key={mode} onClick={() => setSupportEditMode(mode as SupportEditMode)}
                          className={`text-[10px] py-1 rounded border transition ${
                            supportEditMode === mode ? 'font-medium bg-indigo-600/20 text-indigo-300 border-indigo-600/30' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                          }`}>{label}</button>
                      ))}
                    </div>
                    <div className="grid grid-cols-2 gap-1 mb-2">
                      <button onClick={() => setSupportEditMode(supportEditMode === 'paint-enforcer' ? 'none' : 'paint-enforcer')}
                        className={`text-[10px] py-1 rounded border transition ${supportEditMode === 'paint-enforcer' ? 'text-blue-400 bg-blue-900/30 border-blue-700/30 font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'}`}>Enforcer</button>
                      <button onClick={() => setSupportEditMode(supportEditMode === 'paint-blocker' ? 'none' : 'paint-blocker')}
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
                      <button key={mode} onClick={() => setSupportEditMode(mode as SupportEditMode)}
                        className={`text-[10px] py-1 rounded border transition ${
                          supportEditMode === mode ? cls + ' border font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                        }`}>{label}</button>
                    ))}
                  </div>
                  <div className="grid grid-cols-2 gap-1 mb-3">
                    <button onClick={() => setSupportEditMode(supportEditMode === 'paint-enforcer' ? 'none' : 'paint-enforcer')}
                      className={`text-[10px] py-1 rounded border transition ${
                        supportEditMode === 'paint-enforcer' ? 'text-blue-400 bg-blue-900/30 border-blue-700/30 font-medium' : 'text-gray-500 bg-gray-800/50 border-transparent hover:bg-gray-800'
                      }`}>Enforcer</button>
                    <button onClick={() => setSupportEditMode(supportEditMode === 'paint-blocker' ? 'none' : 'paint-blocker')}
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
                      <ul className="space-y-0.5 max-h-24 overflow-y-auto">
                        {selectedSupportData.points.map(p => (
                          <li key={p.id} className="flex items-center justify-between text-[10px] px-1.5 py-0.5 rounded bg-gray-800/50">
                            <span className="text-gray-400 truncate">
                              <span className={`inline-block w-1.5 h-1.5 rounded-full mr-1 ${
                                p.type === 'light' ? 'bg-green-400' : p.type === 'medium' ? 'bg-yellow-400' : 'bg-red-400'
                              }`} />
                              ({p.x.toFixed(1)}, {p.y.toFixed(1)}, {p.z.toFixed(1)})
                            </span>
                            <div className="flex gap-1">
                              <select value={p.type} onChange={e => updateSupportPoint(p.id, { type: e.target.value as any })}
                                className="bg-gray-800 border-none text-[9px] text-gray-400 px-1 py-0 rounded">
                                <option value="light">Light</option>
                                <option value="medium">Med</option>
                                <option value="heavy">Heavy</option>
                              </select>
                              <button onClick={() => deleteSupportPoint(p.id)}
                                className="text-red-400 hover:text-red-300 transition">x</button>
                            </div>
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
