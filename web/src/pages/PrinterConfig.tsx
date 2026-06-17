import { useState, useEffect } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { machineProfilesApi } from '../api/client'
import type { MachineProfile, MachineType, PrinterOrientation, AALevel } from '../types'

// ── Field helpers ─────────────────────────────────────────────────────────────

function Field({ label, hint, children, error }: {
  label: string; hint?: string; children: React.ReactNode; error?: string
}) {
  return (
    <label className="block">
      <span className="text-xs font-medium text-gray-400">{label}</span>
      {hint && <span className="text-[10px] text-gray-600 ml-2">{hint}</span>}
      <div className="mt-1">{children}</div>
      {error && <span className="text-[10px] text-red-400 mt-0.5 block">{error}</span>}
    </label>
  )
}

function NumInput({ value, onChange, min, max, step, unit, disabled }: {
  value: number; onChange: (v: number) => void; min?: number; max?: number; step?: number; unit?: string; disabled?: boolean
}) {
  return (
    <div className="flex items-center gap-1">
      <input type="number" value={value} min={min} max={max} step={step ?? 0.01} disabled={disabled}
        onChange={e => onChange(+e.target.value)}
        className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-1.5 text-sm text-gray-200
                   focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none transition disabled:opacity-40" />
      {unit && <span className="text-[10px] text-gray-500 whitespace-nowrap">{unit}</span>}
    </div>
  )
}

function SelectInput<T extends string>({ value, onChange, options }: {
  value: T; onChange: (v: T) => void; options: { value: T; label: string }[]
}) {
  return (
    <select value={value} onChange={e => onChange(e.target.value as T)}
      className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-1.5 text-sm text-gray-200
                 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none transition">
      {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  )
}

function CheckInput({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label className="flex items-center gap-2 cursor-pointer">
      <input type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)}
        className="rounded border-gray-600 bg-gray-800 text-indigo-500 focus:ring-indigo-500" />
      <span className="text-sm text-gray-300">{label}</span>
    </label>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-gray-900/50 border border-gray-800 rounded-xl p-5">
      <h3 className="text-sm font-semibold text-indigo-400 mb-4 uppercase tracking-wide">{title}</h3>
      <div className="grid grid-cols-2 gap-4">{children}</div>
    </div>
  )
}

// ── Default values for new profiles ───────────────────────────────────────────

function makeDefaults(type: MachineType): Partial<MachineProfile> {
  const isResin = type === 'MSLA' || type === 'DLP'
  return {
    type,
    bedWidthMm: isResin ? 68.04 : 220,
    bedDepthMm: isResin ? 120.96 : 220,
    bedHeightMm: isResin ? 155 : 250,
    orientation: 'BottomUp' as PrinterOrientation,
    resolutionX: isResin ? 1620 : 0,
    resolutionY: isResin ? 2880 : 0,
    pixelPitchUm: 0,
    mirrorX: false, mirrorY: false,
    buildOffsetXMm: 0, buildOffsetYMm: 0,
    defaultLayerHeightMm: 0.05,
    defaultBottomLayerCount: 5,
    defaultNormalExposureMs: 2500,
    defaultBottomExposureMs: 30000,
    lightOffDelayMs: 0,
    liftDistanceMm: 5, liftSpeedMmPerMin: 60,
    retractDistanceMm: 5, retractSpeedMmPerMin: 150,
    bottomLiftDistanceMm: 8, bottomLiftSpeedMmPerMin: 45,
    restTimeAfterLiftMs: 0, restTimeAfterRetractMs: 0,
    antiAliasing: 'None' as AALevel,
    exportFormat: 'ctb',
    extruderCount: isResin ? 0 : 1,
  }
}

// ── Main Component ────────────────────────────────────────────────────────────

export default function PrinterConfig() {
  const qc = useQueryClient()
  const { data: profiles = [], isLoading } = useQuery({ queryKey: ['machine-profiles'], queryFn: machineProfilesApi.getAll })

  // Filter to show only resin printers (and any for now)
  const resinProfiles = profiles.filter(p => !(p as any).isDeleted)

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [editState, setEditState] = useState<Partial<MachineProfile> | null>(null)
  const [isCreating, setIsCreating] = useState(false)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [renaming, setRenaming] = useState(false)
  const [renameName, setRenameName] = useState('')

  const selected = resinProfiles.find(p => p.id === selectedId) ?? null

  // Sync edit state when selection changes
  useEffect(() => {
    if (selected) {
      setEditState({ ...selected })
      setIsCreating(false)
    }
  }, [selectedId])

  // ── Mutations ───────────────────────────────────────────────────────────────

  const createMut = useMutation({
    mutationFn: (data: Partial<MachineProfile>) => {
      const { type, bedWidthMm, bedDepthMm, bedHeightMm } = data as any
      return machineProfilesApi.create({
        name: data.name, type, bedWidthMm, bedDepthMm, bedHeightMm,
        extruderCount: data.extruderCount ?? 0,
        orientation: data.orientation, resolutionX: data.resolutionX, resolutionY: data.resolutionY,
        pixelPitchUm: data.pixelPitchUm, mirrorX: data.mirrorX, mirrorY: data.mirrorY,
        buildOffsetXMm: data.buildOffsetXMm, buildOffsetYMm: data.buildOffsetYMm,
        defaultLayerHeightMm: data.defaultLayerHeightMm, defaultBottomLayerCount: data.defaultBottomLayerCount,
        defaultNormalExposureMs: data.defaultNormalExposureMs, defaultBottomExposureMs: data.defaultBottomExposureMs,
        lightOffDelayMs: data.lightOffDelayMs,
        liftDistanceMm: data.liftDistanceMm, liftSpeedMmPerMin: data.liftSpeedMmPerMin,
        retractDistanceMm: data.retractDistanceMm, retractSpeedMmPerMin: data.retractSpeedMmPerMin,
        bottomLiftDistanceMm: data.bottomLiftDistanceMm, bottomLiftSpeedMmPerMin: data.bottomLiftSpeedMmPerMin,
        restTimeAfterLiftMs: data.restTimeAfterLiftMs, restTimeAfterRetractMs: data.restTimeAfterRetractMs,
        antiAliasing: data.antiAliasing, exportFormat: data.exportFormat,
      } as any)
    },
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ['machine-profiles'] })
      setSelectedId(created.id)
      setIsCreating(false)
    },
  })

  const updateMut = useMutation({
    mutationFn: ({ id, data }: { id: string; data: any }) => machineProfilesApi.update(id, data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['machine-profiles'] }),
  })

  const deleteMut = useMutation({
    mutationFn: (id: string) => machineProfilesApi.delete(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['machine-profiles'] })
      setSelectedId(null)
      setEditState(null)
    },
  })

  const duplicateMut = useMutation({
    mutationFn: (id: string) => machineProfilesApi.duplicate(id),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ['machine-profiles'] })
      setSelectedId(created.id)
    },
  })

  // ── Validation ──────────────────────────────────────────────────────────────

  function validate(s: Partial<MachineProfile>): Record<string, string> {
    const e: Record<string, string> = {}
    if (!s.name?.trim()) e.name = 'Name is required'
    if (!s.bedWidthMm || s.bedWidthMm <= 0) e.bedWidthMm = 'Must be > 0'
    if (!s.bedDepthMm || s.bedDepthMm <= 0) e.bedDepthMm = 'Must be > 0'
    if (!s.bedHeightMm || s.bedHeightMm <= 0) e.bedHeightMm = 'Must be > 0'
    const isResin = s.type === 'MSLA' || s.type === 'DLP'
    if (isResin) {
      if (!s.resolutionX || s.resolutionX <= 0) e.resolutionX = 'Must be > 0'
      if (!s.resolutionY || s.resolutionY <= 0) e.resolutionY = 'Must be > 0'
      if (!s.defaultNormalExposureMs || s.defaultNormalExposureMs <= 0) e.defaultNormalExposureMs = 'Must be > 0'
      if (!s.defaultBottomExposureMs || s.defaultBottomExposureMs <= 0) e.defaultBottomExposureMs = 'Must be > 0'
    }
    return e
  }

  // ── Save handler ────────────────────────────────────────────────────────────

  function handleSave() {
    if (!editState) return
    const errs = validate(editState)
    setErrors(errs)
    if (Object.keys(errs).length > 0) return

    if (isCreating) {
      createMut.mutate(editState)
    } else if (selected) {
      const isResin = editState.type === 'MSLA' || editState.type === 'DLP'
      updateMut.mutate({
        id: selected.id,
        data: {
          name: editState.name,
          bedWidthMm: editState.bedWidthMm,
          bedDepthMm: editState.bedDepthMm,
          bedHeightMm: editState.bedHeightMm,
          ...(isResin ? {
            resinSettings: {
              orientation: editState.orientation,
              resolutionX: editState.resolutionX, resolutionY: editState.resolutionY,
              pixelPitchUm: editState.pixelPitchUm,
              mirrorX: editState.mirrorX, mirrorY: editState.mirrorY,
              buildOffsetXMm: editState.buildOffsetXMm, buildOffsetYMm: editState.buildOffsetYMm,
              defaultLayerHeightMm: editState.defaultLayerHeightMm,
              defaultBottomLayerCount: editState.defaultBottomLayerCount,
              defaultNormalExposureMs: editState.defaultNormalExposureMs,
              defaultBottomExposureMs: editState.defaultBottomExposureMs,
              lightOffDelayMs: editState.lightOffDelayMs,
              liftDistanceMm: editState.liftDistanceMm, liftSpeedMmPerMin: editState.liftSpeedMmPerMin,
              retractDistanceMm: editState.retractDistanceMm, retractSpeedMmPerMin: editState.retractSpeedMmPerMin,
              bottomLiftDistanceMm: editState.bottomLiftDistanceMm, bottomLiftSpeedMmPerMin: editState.bottomLiftSpeedMmPerMin,
              restTimeAfterLiftMs: editState.restTimeAfterLiftMs, restTimeAfterRetractMs: editState.restTimeAfterRetractMs,
              antiAliasing: editState.antiAliasing, exportFormat: editState.exportFormat,
            }
          } : {}),
        },
      })
    }
  }

  // ── New profile ─────────────────────────────────────────────────────────────

  function startCreate(type: MachineType) {
    const defaults = makeDefaults(type)
    setEditState({ ...defaults, name: `New ${type} Printer` })
    setIsCreating(true)
    setSelectedId(null)
    setErrors({})
  }

  // ── Update helper ───────────────────────────────────────────────────────────

  function patch(field: string, value: any) {
    setEditState(prev => prev ? { ...prev, [field]: value } : prev)
    setErrors(prev => { const n = { ...prev }; delete n[field]; return n })
  }

  // ── Computed pixel pitch ────────────────────────────────────────────────────

  const computedPixelPitch = editState && editState.resolutionX && editState.resolutionX > 0 && editState.bedWidthMm
    ? (editState.bedWidthMm / editState.resolutionX * 1000).toFixed(1)
    : null

  const isResin = editState?.type === 'MSLA' || editState?.type === 'DLP'

  // ── Render ──────────────────────────────────────────────────────────────────

  if (isLoading) return <div className="p-8 text-gray-500">Loading...</div>

  return (
    <div className="flex h-[calc(100vh-4rem)]">
      {/* ── Left: Profile list ──────────────────────────────────── */}
      <div className="w-64 border-r border-gray-800 flex flex-col flex-shrink-0">
        <div className="p-4 border-b border-gray-800">
          <h2 className="text-lg font-bold text-gray-200">Printers</h2>
          <div className="flex gap-2 mt-3">
            <button onClick={() => startCreate('MSLA')}
              className="flex-1 text-xs px-2 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white font-medium transition">
              + LCD/MSLA
            </button>
            <button onClick={() => startCreate('DLP')}
              className="flex-1 text-xs px-2 py-1.5 rounded-lg bg-violet-600 hover:bg-violet-500 text-white font-medium transition">
              + DLP
            </button>
          </div>
        </div>

        <ul className="flex-1 overflow-y-auto py-2">
          {resinProfiles.map(p => (
            <li key={p.id}
              onClick={() => { setSelectedId(p.id); setIsCreating(false); setErrors({}) }}
              className={`mx-2 px-3 py-2.5 rounded-lg cursor-pointer transition text-sm ${
                p.id === selectedId
                  ? 'bg-indigo-600/20 text-indigo-300 border border-indigo-500/30'
                  : 'text-gray-400 hover:bg-gray-800 hover:text-gray-200 border border-transparent'
              }`}>
              <div className="font-medium truncate">{p.name}</div>
              <div className="text-[10px] text-gray-500 mt-0.5">
                {p.type} {p.isResinPrinter ? `${p.resolutionX}x${p.resolutionY}` : ''} {p.orientation === 'TopDown' ? 'Top-Down' : 'Bottom-Up'}
              </div>
            </li>
          ))}
          {resinProfiles.length === 0 && !isCreating && (
            <li className="px-4 py-8 text-center text-gray-600 text-xs">No printers configured.<br/>Click + to add one.</li>
          )}
        </ul>
      </div>

      {/* ── Right: Editor ───────────────────────────────────────── */}
      <div className="flex-1 overflow-y-auto">
        {editState ? (
          <div className="max-w-3xl mx-auto p-6 space-y-6">
            {/* Header with actions */}
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                {renaming ? (
                  <input autoFocus value={renameName} onChange={e => setRenameName(e.target.value)}
                    onBlur={() => { patch('name', renameName); setRenaming(false) }}
                    onKeyDown={e => { if (e.key === 'Enter') { patch('name', renameName); setRenaming(false) } }}
                    className="text-xl font-bold bg-transparent border-b border-indigo-500 text-gray-100 outline-none px-1" />
                ) : (
                  <h2 className="text-xl font-bold text-gray-100 cursor-pointer hover:text-indigo-400 transition"
                    onClick={() => { setRenaming(true); setRenameName(editState.name ?? '') }}>
                    {editState.name || 'Untitled Printer'}
                  </h2>
                )}
                <span className={`text-[10px] px-2 py-0.5 rounded-full font-semibold uppercase tracking-wider ${
                  editState.type === 'DLP' ? 'bg-violet-900/50 text-violet-400' : 'bg-indigo-900/50 text-indigo-400'
                }`}>{editState.type}</span>
              </div>
              <div className="flex items-center gap-2">
                {!isCreating && selected && (
                  <>
                    <button onClick={() => duplicateMut.mutate(selected.id)}
                      className="text-xs px-3 py-1.5 rounded-lg bg-gray-800 hover:bg-gray-700 text-gray-300 transition">
                      Duplicate
                    </button>
                    <button onClick={() => { if (confirm(`Delete "${selected.name}"?`)) deleteMut.mutate(selected.id) }}
                      className="text-xs px-3 py-1.5 rounded-lg bg-red-900/40 hover:bg-red-900/70 text-red-400 transition">
                      Delete
                    </button>
                  </>
                )}
                <button onClick={handleSave} disabled={createMut.isPending || updateMut.isPending}
                  className="text-xs px-4 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white font-medium transition disabled:opacity-50">
                  {isCreating ? 'Create' : 'Save Changes'}
                </button>
              </div>
            </div>

            {(createMut.isError || updateMut.isError) && (
              <div className="bg-red-900/30 border border-red-800 rounded-lg px-4 py-2 text-sm text-red-300">
                {(createMut.error as any)?.response?.data || (updateMut.error as any)?.response?.data || 'An error occurred'}
              </div>
            )}

            {/* ── General ──────────────────────────────────────────── */}
            <Section title="General">
              <Field label="Printer Name" error={errors.name}>
                <input value={editState.name ?? ''} onChange={e => patch('name', e.target.value)}
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-1.5 text-sm text-gray-200
                             focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none transition" />
              </Field>
              <Field label="Printer Type">
                <SelectInput value={editState.type ?? 'MSLA'} onChange={v => patch('type', v)}
                  options={[{ value: 'MSLA', label: 'LCD / MSLA' }, { value: 'DLP', label: 'DLP' }]} />
              </Field>
              <Field label="Architecture">
                <SelectInput value={editState.orientation ?? 'BottomUp'}
                  onChange={v => patch('orientation', v)}
                  options={[{ value: 'BottomUp', label: 'Bottom-Up (Inverted)' }, { value: 'TopDown', label: 'Top-Down' }]} />
              </Field>
              <Field label="Export Format">
                <SelectInput value={editState.exportFormat ?? 'ctb'} onChange={v => patch('exportFormat', v)}
                  options={[
                    { value: 'ctb', label: '.ctb (ChiTuBox CTB v3)' },
                    { value: 'cbddlp', label: '.cbddlp (Anycubic CBDDLP)' },
                    { value: 'photon', label: '.photon (Anycubic Photon)' },
                    { value: 'pwmx', label: '.pwmx (Photon Mono X)' },
                    { value: 'pwms', label: '.pwms (Photon Mono SE)' },
                    { value: 'pwmb', label: '.pwmb (Photon Mono)' },
                    { value: 'sl1', label: '.sl1 (Prusa SL1 ZIP+PNG)' },
                    { value: 'zip', label: '.zip (Generic ZIP+PNG)' },
                  ]} />
              </Field>
            </Section>

            {/* ── Recoater (Top-Down printers) ────────────────────── */}
            {editState.orientation === 'TopDown' && (
              <Section title="Recoater">
                <Field label="Has Recoater">
                  <CheckInput checked={(editState as any).hasRecoater ?? false}
                    onChange={v => patch('hasRecoater', v)} label="Printer has a recoater blade/roller" />
                </Field>
                {(editState as any).hasRecoater && (<>
                  <Field label="Recoater Type">
                    <SelectInput value={(editState as any).recoaterType ?? 'blade'} onChange={v => patch('recoaterType', v)}
                      options={[{ value: 'blade', label: 'Blade' }, { value: 'roller', label: 'Roller' }]} />
                  </Field>
                  <Field label="Sweep Direction">
                    <SelectInput value={(editState as any).recoaterDirection ?? 'X'} onChange={v => patch('recoaterDirection', v)}
                      options={[{ value: 'X', label: 'X axis' }, { value: 'Y', label: 'Y axis' }]} />
                  </Field>
                  <Field label="Recoater Speed">
                    <NumInput value={(editState as any).recoaterSpeedMmPerS ?? 50} onChange={v => patch('recoaterSpeedMmPerS', v)} min={1} unit="mm/s" />
                  </Field>
                  <Field label="Clearance">
                    <NumInput value={(editState as any).recoaterClearanceMm ?? 2} onChange={v => patch('recoaterClearanceMm', v)} min={0.5} unit="mm" />
                  </Field>
                </>)}
              </Section>
            )}

            {/* ── Build Volume ──────────────────────────────────────── */}
            <Section title="Build Volume">
              <Field label="Width" error={errors.bedWidthMm}>
                <NumInput value={editState.bedWidthMm ?? 0} onChange={v => patch('bedWidthMm', v)} min={1} unit="mm" />
              </Field>
              <Field label="Depth" error={errors.bedDepthMm}>
                <NumInput value={editState.bedDepthMm ?? 0} onChange={v => patch('bedDepthMm', v)} min={1} unit="mm" />
              </Field>
              <Field label="Height" error={errors.bedHeightMm}>
                <NumInput value={editState.bedHeightMm ?? 0} onChange={v => patch('bedHeightMm', v)} min={1} unit="mm" />
              </Field>
              <div />
              {isResin && (
                <>
                  <Field label="Build Offset X">
                    <NumInput value={editState.buildOffsetXMm ?? 0} onChange={v => patch('buildOffsetXMm', v)} unit="mm" />
                  </Field>
                  <Field label="Build Offset Y">
                    <NumInput value={editState.buildOffsetYMm ?? 0} onChange={v => patch('buildOffsetYMm', v)} unit="mm" />
                  </Field>
                </>
              )}
            </Section>

            {/* ── Image / Resolution ───────────────────────────────── */}
            {isResin && (
              <Section title="Image / Resolution">
                <Field label="Resolution X" hint="pixels" error={errors.resolutionX}>
                  <NumInput value={editState.resolutionX ?? 0} onChange={v => patch('resolutionX', v)} min={1} step={1} unit="px" />
                </Field>
                <Field label="Resolution Y" hint="pixels" error={errors.resolutionY}>
                  <NumInput value={editState.resolutionY ?? 0} onChange={v => patch('resolutionY', v)} min={1} step={1} unit="px" />
                </Field>
                <Field label="Pixel Pitch" hint="0 = auto from build width/resolution">
                  <NumInput value={editState.pixelPitchUm ?? 0} onChange={v => patch('pixelPitchUm', v)} min={0} unit="um" />
                </Field>
                <Field label="Calculated Pixel Size">
                  <div className="text-sm text-gray-300 py-1.5">
                    {computedPixelPitch ? `${computedPixelPitch} um` : '--'}
                  </div>
                </Field>
                <Field label="Anti-Aliasing">
                  <SelectInput value={editState.antiAliasing ?? 'None'} onChange={v => patch('antiAliasing', v)}
                    options={[
                      { value: 'None', label: 'None (1x)' },
                      { value: 'X2', label: '2x' },
                      { value: 'X4', label: '4x' },
                      { value: 'X8', label: '8x' },
                    ]} />
                </Field>
                <div className="space-y-3">
                  <CheckInput checked={editState.mirrorX ?? false} onChange={v => patch('mirrorX', v)} label="Mirror X" />
                  <CheckInput checked={editState.mirrorY ?? false} onChange={v => patch('mirrorY', v)} label="Mirror Y" />
                </div>
              </Section>
            )}

            {/* ── Exposure Defaults ─────────────────────────────────── */}
            {isResin && (
              <Section title="Exposure Defaults">
                <Field label="Default Layer Height">
                  <NumInput value={editState.defaultLayerHeightMm ?? 0.05} onChange={v => patch('defaultLayerHeightMm', v)} min={0.01} step={0.01} unit="mm" />
                </Field>
                <Field label="Bottom Layer Count">
                  <NumInput value={editState.defaultBottomLayerCount ?? 5} onChange={v => patch('defaultBottomLayerCount', Math.round(v))} min={1} step={1} />
                </Field>
                <Field label="Normal Exposure" error={errors.defaultNormalExposureMs}>
                  <NumInput value={editState.defaultNormalExposureMs ?? 2500} onChange={v => patch('defaultNormalExposureMs', v)} min={100} step={100} unit="ms" />
                </Field>
                <Field label="Bottom Exposure" error={errors.defaultBottomExposureMs}>
                  <NumInput value={editState.defaultBottomExposureMs ?? 30000} onChange={v => patch('defaultBottomExposureMs', v)} min={100} step={100} unit="ms" />
                </Field>
                <Field label="Light-Off Delay">
                  <NumInput value={editState.lightOffDelayMs ?? 0} onChange={v => patch('lightOffDelayMs', v)} min={0} step={100} unit="ms" />
                </Field>
              </Section>
            )}

            {/* ── Motion / Peel Settings ────────────────────────────── */}
            {isResin && (
              <Section title="Motion / Peel Settings">
                <Field label="Lift Distance">
                  <NumInput value={editState.liftDistanceMm ?? 5} onChange={v => patch('liftDistanceMm', v)} min={0} unit="mm" />
                </Field>
                <Field label="Lift Speed">
                  <NumInput value={editState.liftSpeedMmPerMin ?? 60} onChange={v => patch('liftSpeedMmPerMin', v)} min={1} unit="mm/min" />
                </Field>
                <Field label="Retract Distance">
                  <NumInput value={editState.retractDistanceMm ?? 5} onChange={v => patch('retractDistanceMm', v)} min={0} unit="mm" />
                </Field>
                <Field label="Retract Speed">
                  <NumInput value={editState.retractSpeedMmPerMin ?? 150} onChange={v => patch('retractSpeedMmPerMin', v)} min={1} unit="mm/min" />
                </Field>
                <Field label="Bottom Lift Distance">
                  <NumInput value={editState.bottomLiftDistanceMm ?? 8} onChange={v => patch('bottomLiftDistanceMm', v)} min={0} unit="mm" />
                </Field>
                <Field label="Bottom Lift Speed">
                  <NumInput value={editState.bottomLiftSpeedMmPerMin ?? 45} onChange={v => patch('bottomLiftSpeedMmPerMin', v)} min={1} unit="mm/min" />
                </Field>
                <Field label="Rest After Lift">
                  <NumInput value={editState.restTimeAfterLiftMs ?? 0} onChange={v => patch('restTimeAfterLiftMs', v)} min={0} step={100} unit="ms" />
                </Field>
                <Field label="Rest After Retract">
                  <NumInput value={editState.restTimeAfterRetractMs ?? 0} onChange={v => patch('restTimeAfterRetractMs', v)} min={0} step={100} unit="ms" />
                </Field>
              </Section>
            )}

            {/* ── Estimated Print Info ──────────────────────────────── */}
            {isResin && editState.resolutionX && editState.resolutionY && editState.bedWidthMm && editState.bedDepthMm && (
              <div className="bg-gray-900/30 border border-gray-800 rounded-xl p-5">
                <h3 className="text-sm font-semibold text-gray-500 mb-3 uppercase tracking-wide">Calculated Summary</h3>
                <div className="grid grid-cols-3 gap-4 text-sm">
                  <div>
                    <span className="text-gray-500 text-xs">Pixel Size</span>
                    <div className="text-gray-200 font-medium">{computedPixelPitch ? `${computedPixelPitch} um` : '--'}</div>
                  </div>
                  <div>
                    <span className="text-gray-500 text-xs">Image Size</span>
                    <div className="text-gray-200 font-medium">{editState.resolutionX} x {editState.resolutionY} px</div>
                  </div>
                  <div>
                    <span className="text-gray-500 text-xs">Build Area</span>
                    <div className="text-gray-200 font-medium">{editState.bedWidthMm} x {editState.bedDepthMm} mm</div>
                  </div>
                </div>
              </div>
            )}
          </div>
        ) : (
          <div className="flex items-center justify-center h-full text-gray-600">
            <div className="text-center">
              <svg className="w-16 h-16 mx-auto mb-4 opacity-30" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1}
                  d="M17 17h2a2 2 0 002-2v-4a2 2 0 00-2-2H5a2 2 0 00-2 2v4a2 2 0 002 2h2m2 4h6a2 2 0 002-2v-4a2 2 0 00-2-2H9a2 2 0 00-2 2v4a2 2 0 002 2zm8-12V5a2 2 0 00-2-2H9a2 2 0 00-2 2v4h10z" />
              </svg>
              <p className="text-sm">Select a printer or create a new one</p>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
