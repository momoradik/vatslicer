/**
 * ChiTuBox-style Advanced Settings panel for manual support sizing.
 * Tabbed: Top / Middle / Bottom / Raft with Auto/Manual toggle + presets.
 */
import { useState } from 'react'

// ── Types ──

export type SizingMode = 'auto' | 'manual'
export type SupportPresetName = 'custom' | 'light' | 'medium' | 'heavy'
export type TouchShapeName = 'sphere' | 'skate' | 'none'
export type SupportShapeName = 'cone' | 'cylinder' | 'cube' | 'cross' | 'pyramid'

export interface AdvancedSupportSettings {
  sizingMode: SizingMode
  preset: SupportPresetName
  // Top
  topTouchShape: TouchShapeName
  topContactDepthMm: number
  topTipUpperDiaMm: number
  topTipLowerDiaMm: number
  topConnectionShape: SupportShapeName
  topConnectionLengthMm: number
  // Middle
  middlePillarDiaMm: number
  middlePillarShape: SupportShapeName
  // Bottom
  bottomBaseDiaMm: number
  bottomBaseThicknessMm: number
  // Raft
  raftThicknessMm: number
}

// Preset seed values (diameters in mm)
const PRESET_VALUES: Record<Exclude<SupportPresetName, 'custom'>, Omit<AdvancedSupportSettings, 'sizingMode' | 'preset'>> = {
  light: {
    topTouchShape: 'sphere', topContactDepthMm: 0.2, topTipUpperDiaMm: 0.4, topTipLowerDiaMm: 0.3,
    topConnectionShape: 'cone', topConnectionLengthMm: 1.0,
    middlePillarDiaMm: 0.6, middlePillarShape: 'cylinder',
    bottomBaseDiaMm: 2.0, bottomBaseThicknessMm: 0.8,
    raftThicknessMm: 0.3,
  },
  medium: {
    topTouchShape: 'sphere', topContactDepthMm: 0.3, topTipUpperDiaMm: 0.6, topTipLowerDiaMm: 0.4,
    topConnectionShape: 'cone', topConnectionLengthMm: 1.5,
    middlePillarDiaMm: 1.0, middlePillarShape: 'cylinder',
    bottomBaseDiaMm: 3.0, bottomBaseThicknessMm: 1.0,
    raftThicknessMm: 0.5,
  },
  heavy: {
    topTouchShape: 'sphere', topContactDepthMm: 0.4, topTipUpperDiaMm: 1.0, topTipLowerDiaMm: 0.6,
    topConnectionShape: 'cone', topConnectionLengthMm: 2.0,
    middlePillarDiaMm: 1.6, middlePillarShape: 'cylinder',
    bottomBaseDiaMm: 4.0, bottomBaseThicknessMm: 1.5,
    raftThicknessMm: 0.8,
  },
}

export const DEFAULT_ADVANCED_SETTINGS: AdvancedSupportSettings = {
  sizingMode: 'auto',
  preset: 'custom',
  ...PRESET_VALUES.medium,
}

// ── Helpers ──

type Tab = 'top' | 'middle' | 'bottom' | 'raft'

function NumField({ label, unit, value, min, max, step, disabled, onChange }: {
  label: string; unit: string; value: number; min: number; max: number; step: number
  disabled?: boolean; onChange: (v: number) => void
}) {
  return (
    <label className="flex items-center justify-between text-[10px]">
      <span className={disabled ? 'text-gray-600' : 'text-gray-400'}>{label}</span>
      <div className="flex items-center gap-1">
        <input type="number" value={value} min={min} max={max} step={step} disabled={disabled}
          onChange={e => onChange(Math.min(max, Math.max(min, Number(e.target.value))))}
          className={`w-16 bg-gray-800 border rounded px-1 py-0.5 text-[10px] text-right
            ${disabled ? 'border-gray-800 text-gray-600 cursor-not-allowed' : 'border-gray-700 text-gray-200'}`} />
        <span className="text-gray-600 text-[9px] w-5">{unit}</span>
      </div>
    </label>
  )
}

function ShapeSelect({ label, value, options, disabled, onChange }: {
  label: string; value: string; options: { value: string; label: string }[]
  disabled?: boolean; onChange: (v: string) => void
}) {
  return (
    <label className="flex items-center justify-between text-[10px]">
      <span className={disabled ? 'text-gray-600' : 'text-gray-400'}>{label}</span>
      <select value={value} disabled={disabled} onChange={e => onChange(e.target.value)}
        className={`bg-gray-800 border rounded px-1 py-0.5 text-[10px]
          ${disabled ? 'border-gray-800 text-gray-600 cursor-not-allowed' : 'border-gray-700 text-gray-200'}`}>
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  )
}

// ── Main Component ──

interface Props {
  value: AdvancedSupportSettings
  onChange: (v: AdvancedSupportSettings) => void
}

export default function AdvancedSettingsPanel({ value, onChange }: Props) {
  const [activeTab, setActiveTab] = useState<Tab>('top')
  const disabled = value.sizingMode === 'auto'

  const set = <K extends keyof AdvancedSupportSettings>(key: K, val: AdvancedSupportSettings[K]) =>
    onChange({ ...value, [key]: val, preset: 'custom' as const })

  const applyPreset = (p: SupportPresetName) => {
    if (p === 'custom') { onChange({ ...value, preset: 'custom' }); return }
    onChange({ ...value, ...PRESET_VALUES[p], preset: p, sizingMode: 'manual' })
  }

  const tabs: { key: Tab; label: string }[] = [
    { key: 'top', label: 'Top' },
    { key: 'middle', label: 'Middle' },
    { key: 'bottom', label: 'Bottom' },
    { key: 'raft', label: 'Raft' },
  ]

  const touchShapes = [
    { value: 'sphere', label: 'Sphere' }, { value: 'skate', label: 'Skate' }, { value: 'none', label: 'None' },
  ]
  const supportShapes = [
    { value: 'cone', label: 'Cone' }, { value: 'cylinder', label: 'Cylinder' },
    { value: 'cube', label: 'Cube' }, { value: 'cross', label: 'Cross' }, { value: 'pyramid', label: 'Pyramid' },
  ]

  return (
    <div className="space-y-2">
      {/* Auto / Manual toggle */}
      <div className="flex items-center gap-2">
        <div className="flex rounded-md overflow-hidden border border-gray-700 text-[10px]">
          {(['auto', 'manual'] as const).map(m => (
            <button key={m} type="button"
              onClick={() => onChange({ ...value, sizingMode: m })}
              className={`px-3 py-1 transition-colors capitalize
                ${value.sizingMode === m
                  ? 'bg-teal-500/20 text-teal-300 font-semibold'
                  : 'bg-gray-800 text-gray-500 hover:text-gray-300'}`}>
              {m}
            </button>
          ))}
        </div>
        <select value={value.preset} onChange={e => applyPreset(e.target.value as SupportPresetName)}
          className="bg-gray-800 border border-gray-700 rounded px-2 py-1 text-[10px] text-gray-300">
          <option value="custom">Custom</option>
          <option value="light">Light</option>
          <option value="medium">Medium</option>
          <option value="heavy">Heavy</option>
        </select>
      </div>

      {/* Tab bar */}
      <div className="flex border-b border-gray-700">
        {tabs.map(t => (
          <button key={t.key} type="button"
            onClick={() => setActiveTab(t.key)}
            className={`px-3 py-1 text-[10px] font-medium transition-colors border-b-2 -mb-px
              ${activeTab === t.key
                ? 'border-teal-400 text-teal-300'
                : 'border-transparent text-gray-500 hover:text-gray-300'}`}>
            {t.label}
          </button>
        ))}
      </div>

      {/* Tab content */}
      <div className="space-y-1.5 pt-1">
        {activeTab === 'top' && <>
          <ShapeSelect label="Touch Shape" value={value.topTouchShape} options={touchShapes}
            disabled={disabled} onChange={v => set('topTouchShape', v as TouchShapeName)} />
          <NumField label="Contact Depth" unit="mm" value={value.topContactDepthMm} min={0.05} max={2} step={0.05}
            disabled={disabled} onChange={v => set('topContactDepthMm', v)} />
          <NumField label="Tip Upper Dia" unit="mm" value={value.topTipUpperDiaMm} min={0.1} max={5} step={0.1}
            disabled={disabled} onChange={v => set('topTipUpperDiaMm', v)} />
          <NumField label="Tip Lower Dia" unit="mm" value={value.topTipLowerDiaMm} min={0.1} max={5} step={0.1}
            disabled={disabled} onChange={v => set('topTipLowerDiaMm', v)} />
          <ShapeSelect label="Connection Shape" value={value.topConnectionShape} options={supportShapes}
            disabled={disabled} onChange={v => set('topConnectionShape', v as SupportShapeName)} />
          <NumField label="Connection Length" unit="mm" value={value.topConnectionLengthMm} min={0.1} max={10} step={0.1}
            disabled={disabled} onChange={v => set('topConnectionLengthMm', v)} />
        </>}

        {activeTab === 'middle' && <>
          <NumField label="Pillar Diameter" unit="mm" value={value.middlePillarDiaMm} min={0.2} max={10} step={0.1}
            disabled={disabled} onChange={v => set('middlePillarDiaMm', v)} />
          <ShapeSelect label="Pillar Shape" value={value.middlePillarShape} options={supportShapes}
            disabled={disabled} onChange={v => set('middlePillarShape', v as SupportShapeName)} />
        </>}

        {activeTab === 'bottom' && <>
          <NumField label="Base Diameter" unit="mm" value={value.bottomBaseDiaMm} min={0.5} max={20} step={0.1}
            disabled={disabled} onChange={v => set('bottomBaseDiaMm', v)} />
          <NumField label="Base Thickness" unit="mm" value={value.bottomBaseThicknessMm} min={0.1} max={10} step={0.1}
            disabled={disabled} onChange={v => set('bottomBaseThicknessMm', v)} />
        </>}

        {activeTab === 'raft' && <>
          <NumField label="Raft Thickness" unit="mm" value={value.raftThicknessMm} min={0.1} max={5} step={0.1}
            disabled={disabled} onChange={v => set('raftThicknessMm', v)} />
        </>}
      </div>

      {disabled && (
        <p className="text-[9px] text-gray-600 italic">
          Switch to Manual mode to edit dimensions. Auto uses physics-driven sizing.
        </p>
      )}
    </div>
  )
}
