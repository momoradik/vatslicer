import axios from 'axios'
import type {
  MachineProfile, PrintProfile, CncTool,
  PrintJob, CustomGCodeBlock, BrandingSettings, Material, ResinPrintProfile, ResinMaterial
} from '../types'

const http = axios.create({ baseURL: '/api' })

// ── Machine Profiles ──────────────────────────────────────────────────────
export const machineProfilesApi = {
  getAll: () => http.get<MachineProfile[]>('/machine-profiles').then(r => r.data),
  getById: (id: string) => http.get<MachineProfile>(`/machine-profiles/${id}`).then(r => r.data),
  create: (data: Partial<MachineProfile>) => http.post<MachineProfile>('/machine-profiles', data).then(r => r.data),
  update: (id: string, data: Partial<MachineProfile>) => http.put<MachineProfile>(`/machine-profiles/${id}`, data).then(r => r.data),
  updateOffsets: (id: string, offsets: object) => http.put(`/machine-profiles/${id}/offsets`, offsets).then(r => r.data),
  duplicate: (id: string, name?: string) => http.post<MachineProfile>(`/machine-profiles/${id}/duplicate`, { name }).then(r => r.data),
  delete: (id: string) => http.delete(`/machine-profiles/${id}`),
}

// ── Print Profiles ────────────────────────────────────────────────────────
export const printProfilesApi = {
  getAll: () => http.get<PrintProfile[]>('/print-profiles').then(r => r.data),
  getById: (id: string) => http.get<PrintProfile>(`/print-profiles/${id}`).then(r => r.data),
  create: (data: Partial<PrintProfile>) => http.post<PrintProfile>('/print-profiles', data).then(r => r.data),
  update: (id: string, data: Partial<PrintProfile>) => http.put<PrintProfile>(`/print-profiles/${id}`, data).then(r => r.data),
  delete: (id: string) => http.delete(`/print-profiles/${id}`),
}

// ── CNC Tools ─────────────────────────────────────────────────────────────
export const toolsApi = {
  getAll: () => http.get<CncTool[]>('/tools').then(r => r.data),
  getById: (id: string) => http.get<CncTool>(`/tools/${id}`).then(r => r.data),
  create: (data: Partial<CncTool>) => http.post<CncTool>('/tools', data).then(r => r.data),
  update: (id: string, data: object) => http.put<CncTool>(`/tools/${id}`, data).then(r => r.data),
  delete: (id: string) => http.delete(`/tools/${id}`),
}

// ── Jobs ──────────────────────────────────────────────────────────────────
export const jobsApi = {
  getAll: () => http.get<PrintJob[]>('/jobs').then(r => r.data),
  getById: (id: string) => http.get<PrintJob>(`/jobs/${id}`).then(r => r.data),

  uploadStl: (formData: FormData) =>
    http.post<{ jobId: string }>('/jobs/upload-stl', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    }).then(r => r.data),

  slice: (id: string) => http.post(`/jobs/${id}/slice`).then(r => r.data),

  generateToolpaths: (
    id: string, toolId: string, machineEveryN: number,
    machineInnerWalls = false, avoidSupports = false,
    supportClearanceMm = 2.0,
    autoMachiningFrequency = false,
    zSafetyOffsetMm = 0,
    spindleRpmOverride: number | null = null,
    spindleStartX = 0, spindleStartY = 0, spindleStartZ: number | null = null,
    spindleEndX = 0, spindleEndY = 0, spindleEndZ: number | null = null,
  ) =>
    http.post(`/jobs/${id}/generate-toolpaths`, {
      cncToolId: toolId,
      machineEveryNLayers: machineEveryN,
      machineInnerWalls,
      avoidSupports,
      supportClearanceMm,
      autoMachiningFrequency,
      zSafetyOffsetMm,
      spindleRpmOverride,
      spindleStartX, spindleStartY, spindleStartZ,
      spindleEndX, spindleEndY, spindleEndZ,
    }).then(r => r.data),

  planHybrid: (id: string, machineEveryN: number) =>
    http.post(`/jobs/${id}/plan-hybrid`, { machineEveryNLayers: machineEveryN }).then(r => r.data),

  getPrintGCode: (id: string) =>
    http.get<string>(`/jobs/${id}/print-gcode`, { responseType: 'text' }).then(r => r.data),

  getToolpathGCode: (id: string) =>
    http.get<string>(`/jobs/${id}/toolpath-gcode`, { responseType: 'text' }).then(r => r.data),

  downloadGCode: (id: string) =>
    http.get(`/jobs/${id}/gcode`, { responseType: 'blob' }).then(r => r.data),

  getHybridGCode: (id: string) =>
    http.get<string>(`/jobs/${id}/gcode`, { responseType: 'text' }).then(r => r.data),

  mergeBeds: (
    jobIds: string[], layerStep: number, name?: string,
    hybrid = false, cncParams?: {
      cncToolId: string; machineEveryNLayers: number;
      machineInnerWalls: boolean; avoidSupports: boolean;
      supportClearanceMm: number; autoMachiningFrequency: boolean;
      zSafetyOffsetMm: number; spindleRpmOverride: number | null;
    },
  ) =>
    http.post<{ jobId: string; mergedPath: string; beds: number; layerStep: number; totalLayers: number }>(
      '/jobs/merge-beds', { jobIds, layerStep, name, hybrid, ...cncParams }).then(r => r.data),

  deleteJob: (id: string) => http.delete(`/jobs/${id}`),
}

// ── Custom G-code Blocks ──────────────────────────────────────────────────
export const customGCodeApi = {
  getAll: () => http.get<CustomGCodeBlock[]>('/custom-gcode-blocks').then(r => r.data),
  create: (data: Partial<CustomGCodeBlock>) => http.post<CustomGCodeBlock>('/custom-gcode-blocks', data).then(r => r.data),
  update: (id: string, data: Partial<CustomGCodeBlock>) => http.put<CustomGCodeBlock>(`/custom-gcode-blocks/${id}`, data).then(r => r.data),
  toggle: (id: string, enabled: boolean) => http.patch(`/custom-gcode-blocks/${id}/toggle`, { enabled }),
  delete: (id: string) => http.delete(`/custom-gcode-blocks/${id}`),
}

// ── Resin Print Profiles ──────────────────────────────────────────────
export const resinPrintProfilesApi = {
  getAll: () => http.get<ResinPrintProfile[]>('/resin-print-profiles').then(r => r.data),
  getById: (id: string) => http.get<ResinPrintProfile>(`/resin-print-profiles/${id}`).then(r => r.data),
  create: (data: Partial<ResinPrintProfile>) => http.post<ResinPrintProfile>('/resin-print-profiles', data).then(r => r.data),
  update: (id: string, data: Partial<ResinPrintProfile>) => http.put<ResinPrintProfile>(`/resin-print-profiles/${id}`, data).then(r => r.data),
  duplicate: (id: string, name?: string) => http.post<ResinPrintProfile>(`/resin-print-profiles/${id}/duplicate`, { name }).then(r => r.data),
  delete: (id: string) => http.delete(`/resin-print-profiles/${id}`),
}

// ── Mesh Validation ──────────────────────────────────────────────────
export const meshApi = {
  validate: (formData: FormData) =>
    http.post<{
      isValid: boolean; triangleCount: number
      degenerateTriangles: number; nanInfVertices: number; flippedNormals: number
      nonManifoldEdges: number; openEdges: number; boundsValid: boolean; volumeMm3: number
      sizeX: number; sizeY: number; sizeZ: number
      warnings: string[]; errors: string[]
    }>('/mesh/validate', formData, { headers: { 'Content-Type': 'multipart/form-data' } }).then(r => r.data),
}

// ── Resin Materials ──────────────────────────────────────────────────
export const resinMaterialsApi = {
  getAll: () => http.get<ResinMaterial[]>('/resin-materials').then(r => r.data),
  getById: (id: string) => http.get<ResinMaterial>(`/resin-materials/${id}`).then(r => r.data),
  create: (data: Partial<ResinMaterial>) => http.post<ResinMaterial>('/resin-materials', data).then(r => r.data),
  update: (id: string, data: Partial<ResinMaterial>) => http.put<ResinMaterial>(`/resin-materials/${id}`, data).then(r => r.data),
  duplicate: (id: string, name?: string) => http.post<ResinMaterial>(`/resin-materials/${id}/duplicate`, { name }).then(r => r.data),
  delete: (id: string) => http.delete(`/resin-materials/${id}`),
}

// ── Auto Support Generation ──────────────────────────────────────────
export interface GeneratedSupportData {
  x: number; y: number; contactZ: number; baseZ: number
  tipDiameter: number; columnDiameter: number; baseDiameter: number
  normalX: number; normalY: number; normalZ: number
}
export interface RaftData {
  type: string; minX: number; minY: number; maxX: number; maxY: number
  thicknessMm: number; marginMm: number
}
export interface SkirtData {
  minX: number; minY: number; maxX: number; maxY: number
  layers: number; distanceMm: number; widthMm: number
}
export const autoSupportApi = {
  generate: (fd: FormData) =>
    http.post<{
      supportCount: number; overhangFaceCount: number; elapsedMs: number; orientation: string
      supports: GeneratedSupportData[]; raft: RaftData | null; skirt: SkirtData | null
    }>('/auto-support', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 30000,
    }).then(r => r.data),
}

// ── Advanced Support Generation ──────────────────────────────────────
export interface SupportSegmentData {
  part: string; x1: number; y1: number; z1: number; r1: number
  x2: number; y2: number; z2: number; r2: number
}
export interface AdvancedSupportData {
  id: string; type: string
  contactX: number; contactY: number; contactZ: number
  baseX: number; baseY: number; baseZ: number
  mergeX?: number; mergeY?: number; mergeZ?: number
  parentTrunkId?: string
  preset: { name: string; tipDiameterMm: number; shaftDiameterMm: number; baseDiameterMm: number }
  segments: SupportSegmentData[]
}
export interface CrossBraceData {
  supportA: string; supportB: string
  x1: number; y1: number; z1: number; x2: number; y2: number; z2: number; diameter: number
}
export const advancedSupportApi = {
  generate: (fd: FormData) =>
    http.post<{
      supportCount: number; braceCount: number; overhangFaceCount: number; elapsedMs: number
      orientation: string; supportType: string
      supports: AdvancedSupportData[]; crossBraces: CrossBraceData[]
    }>('/advanced-support', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 30000,
    }).then(r => r.data),
}

// ── V2 Support Engine (production-grade) ─────────────────────────────
export interface V2ValidationData {
  collision: { collisionFreeSupports: number; collidingSupports: number; totalCollisionPoints: number }
  structural: {
    passedBuckling: number; failedBuckling: number
    passedTensile: number; failedTensile: number
    overhangRegionsCovered: number; overhangRegionsUncovered: number
    minSafetyFactor: number; avgSafetyFactor: number; manifoldErrors: number
  }
  issues: { supportId: string; element: string; description: string }[]
}
export const supportV2Api = {
  generate: (fd: FormData) =>
    http.post<{
      engine: string; totalSupports: number; validSupports: number
      rejectedCollisions: number; totalVolumeMm3: number
      totalVolumeMl: number; totalWeightG: number; estimatedCostUsd: number
      elapsedMs: number
      meshOffset: { x: number; y: number; z: number }
      validation: V2ValidationData
      mesh: { vertices: number; faces: number; nonManifoldEdges: number; stlBase64: string | null }
      supportCount: number; braceCount: number
      uncoverableManualIds: string[]
      droppedByCapCount: number
      manualSupportMeshes: Record<string, string>  // ID → base64 STL of real generated mesh per manual support
      supports: AdvancedSupportData[]; crossBraces: CrossBraceData[]
    }>('/support-v2', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 600000, // 10 min for large models
    }).then(r => r.data),
  downloadMesh: (fd: FormData) =>
    http.post('/support-v2/mesh', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
      responseType: 'blob', timeout: 300000,
    }).then(r => r.data as Blob),
  /** Compute a single support in real time (same engine as auto, for ONE tip) */
  computeSingle: (fd: FormData) =>
    http.post<{
      status: string  // 'routed' | 'bundled' | 'collision' | 'uncoverable'
      pillarAxis: { x: number; y: number; z: number }
      baseZ: number
      bundledIntoId: string | null
      computeMs: number
      meshOffset: { x: number; y: number; z: number }
      mesh: { faces: number; stlBase64: string | null }
    }>('/support-v2/single', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 5000,
    }).then(r => r.data),
  /** Get support mesh as ArrayBuffer for Three.js STLLoader */
  getMeshBuffer: (fd: FormData) =>
    http.post('/support-v2/mesh', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
      responseType: 'arraybuffer', timeout: 300000,
    }).then(r => r.data as ArrayBuffer),
  /** Export model + supports as ZIP (model.stl + supports.stl + metadata) */
  exportCombined: (fd: FormData) =>
    http.post('/support-v2/export-combined', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
      responseType: 'blob', timeout: 300000,
    }).then(r => r.data as Blob),
  /** Auto-orient: find best orientation for minimal supports */
  autoOrient: (fd: FormData) =>
    http.post<{
      orientations: {
        rotationX: number; rotationY: number; rotationZ: number; rotationW: number
        overhangAreaMm2: number; supportVolumeMl: number; estimatedSupports: number
        score: number; description: string
      }[]
    }>('/support-v2/auto-orient', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 60000,
    }).then(r => r.data),
  /** Suggest drain hole positions for resin traps */
  suggestDrainHoles: (fd: FormData) =>
    http.post<{
      holes: {
        x: number; y: number; z: number
        normalX: number; normalY: number; normalZ: number
        diameterMm: number; trapVolumeMm3: number; reason: string
      }[]
    }>('/support-v2/drain-holes', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 60000,
    }).then(r => r.data),
  /** Check for suction cup geometry */
  suctionCheck: (fd: FormData) =>
    http.post<{
      warningCount: number
      warnings: { x: number; y: number; z: number; depthMm: number; areaRatio: number; severity: string; description: string }[]
    }>('/support-v2/suction-check', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 60000,
    }).then(r => r.data),
  /** Predict island/floating geometry risks */
  islandCheck: (fd: FormData) =>
    http.post<{
      riskCount: number; highRiskCount: number; lowestUnsupportedZ: number
      risks: { zMm: number; areaMm2: number; risk: string; x: number; y: number; z: number }[]
    }>('/support-v2/island-check', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 60000,
    }).then(r => r.data),
  /** Check for thin walls */
  thinWallCheck: (fd: FormData) =>
    http.post<{
      thinWallCount: number; minWallThicknessMm: number
      warnings: { x: number; y: number; z: number; wallThicknessMm: number }[]
    }>('/support-v2/thin-wall-check', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 60000,
    }).then(r => r.data),
  /** Compute peel force profile */
  peelForce: (fd: FormData) =>
    http.post<{
      maxPeelForceN: number; maxPeelForceZ: number; avgPeelForceN: number
      highStressLayers: number; totalLayers: number
    }>('/support-v2/peel-force', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }, timeout: 60000,
    }).then(r => r.data),
}

// ── Prep Tools (drain holes, support optimization) ───────────────────
export const prepToolsApi = {
  suggestDrainHoles: (fd: FormData) =>
    http.post<{ count: number; holes: { x: number; y: number; z: number; diameterMm: number; depthMm: number }[] }>(
      '/prep-tools/suggest-drain-holes', fd, { headers: { 'Content-Type': 'multipart/form-data' } }).then(r => r.data),
  optimizeSupports: (fd: FormData) =>
    http.post<{
      originalCount: number; finalCount: number; addedForReinforcement: number
      removedForReduction: number; recoaterReinforcements: number; warnings: string[]
      supports: GeneratedSupportData[]
    }>('/prep-tools/optimize-supports', fd, { headers: { 'Content-Type': 'multipart/form-data' } }).then(r => r.data),
  nest: (parts: { id: string; widthMm: number; depthMm: number }[], plateWidthMm: number, plateDepthMm: number) =>
    http.post<{
      placements: { id: string; centerX: number; centerY: number; rotated90: boolean }[]
      overflow: string[]
      utilization: number
    }>('/prep-tools/nest', {
      parts, plateWidthMm, plateDepthMm, partGapMm: 2, plateMarginMm: 3,
    }).then(r => r.data),
}

// ── Resin Slicing ────────────────────────────────────────────────────
export const resinSliceApi = {
  slice: (formData: FormData) =>
    http.post<{
      jobId: string; layerCount: number; bottomLayerCount: number
      layerHeightMm: number; resolutionX: number; resolutionY: number
      normalExposureMs: number; bottomExposureMs: number
      totalHeightMm: number; estimatedPrintTimeMin: number; elapsedMs: number
      printerName: string; profileName: string
    }>('/resin-slice', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
      timeout: 300000, // 5 min max for large models
    }).then(r => r.data),
  getLayerImageUrl: (jobId: string, layerIndex: number) =>
    `/api/resin-slice/${jobId}/layer/${layerIndex}`,
  getLayerData: (jobId: string) =>
    http.get<{
      jobId: string; layerCount: number; bottomLayerCount: number
      layerHeightMm: number; resolutionX: number; resolutionY: number
      totalHeightMm: number; estimatedPrintTimeMin: number
      layers: {
        index: number; zHeightMm: number; layerThicknessMm: number
        type: string; exposureMs: number; liftDistanceMm: number
        liftSpeedMmPerMin: number; lightOffDelayMs: number
        imageFileName: string; contourCount: number; imageSizeBytes: number; isEmpty: boolean
      }[]
    }>(`/resin-slice/${jobId}/layers`).then(r => r.data),
  /** Export a sliced job to a printer file format */
  exportJob: (jobId: string, format: string) =>
    http.get(`/resin-slice/${jobId}/export/${format}`, {
      responseType: 'blob', timeout: 300000,
    }).then(r => r.data as Blob),
  /** Get supported export formats */
  getFormats: () =>
    http.get<{ format: string; name: string; extension: string }[]>('/resin-slice/formats')
      .then(r => r.data),
}

// ── Branding ──────────────────────────────────────────────────────────────
export const brandingApi = {
  get: () => http.get<BrandingSettings>('/branding').then(r => r.data),
  update: (data: BrandingSettings) => http.put<BrandingSettings>('/branding', data).then(r => r.data),
}

// ── Materials ─────────────────────────────────────────────────────────────
export const materialsApi = {
  getAll: () => http.get<Material[]>('/materials').then(r => r.data),
  create: (data: Partial<Material>) => http.post<Material>('/materials', data).then(r => r.data),
  update: (id: string, data: Partial<Material>) => http.put<Material>(`/materials/${id}`, data).then(r => r.data),
  delete: (id: string) => http.delete(`/materials/${id}`),
}
