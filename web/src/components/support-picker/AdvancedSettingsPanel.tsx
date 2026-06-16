/**
 * ChiTuBox-style Advanced Settings panel for manual support sizing.
 * Tabbed: Top / Middle / Bottom / Raft with Auto/Manual toggle + presets.
 * C2: Illustrated shape picker cards instead of plain dropdowns.
 * C4: Live SVG cross-section diagram at top.
 * C5: Tooltips on all parameters.
 */
import { useState } from 'react'
import {
  IconTouchSphere, IconTouchSkate, IconTouchNone,
  IconConnCone, IconConnCylinder, IconConnPyramid,
  IconPillarCylinder, IconPillarCube, IconPillarCross,
} from './SupportIcons'

// ── Types ──

export type SizingMode = 'auto' | 'manual'
export type SupportPresetName = 'custom' | 'light' | 'medium' | 'heavy'
export type TouchShapeName = 'sphere' | 'skate' | 'none'
export type SupportShapeName = 'cone' | 'cylinder' | 'cube' | 'cross' | 'pyramid'

export interface AdvancedSupportSettings {
  sizingMode: SizingMode
  preset: SupportPresetName
  topTouchShape: TouchShapeName
  topContactDepthMm: number
  topTipUpperDiaMm: number
  topTipLowerDiaMm: number
  topTipAngleDeg: number
  topConnectionShape: SupportShapeName
  topConnectionLengthMm: number
  middlePillarDiaMm: number
  middlePillarShape: SupportShapeName
  bottomBaseDiaMm: number
  bottomBaseThicknessMm: number
  raftThicknessMm: number
}

const PRESET_VALUES: Record<Exclude<SupportPresetName, 'custom'>, Omit<AdvancedSupportSettings, 'sizingMode' | 'preset'>> = {
  light: {
    topTouchShape: 'sphere', topContactDepthMm: 0.2, topTipUpperDiaMm: 0.4, topTipLowerDiaMm: 0.3, topTipAngleDeg: 45,
    topConnectionShape: 'cone', topConnectionLengthMm: 1.0,
    middlePillarDiaMm: 0.6, middlePillarShape: 'cylinder',
    bottomBaseDiaMm: 2.0, bottomBaseThicknessMm: 0.8,
    raftThicknessMm: 0.3,
  },
  medium: {
    topTouchShape: 'sphere', topContactDepthMm: 0.3, topTipUpperDiaMm: 0.6, topTipLowerDiaMm: 0.4, topTipAngleDeg: 45,
    topConnectionShape: 'cone', topConnectionLengthMm: 1.5,
    middlePillarDiaMm: 1.0, middlePillarShape: 'cylinder',
    bottomBaseDiaMm: 3.0, bottomBaseThicknessMm: 1.0,
    raftThicknessMm: 0.5,
  },
  heavy: {
    topTouchShape: 'sphere', topContactDepthMm: 0.4, topTipUpperDiaMm: 1.0, topTipLowerDiaMm: 0.6, topTipAngleDeg: 45,
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

function NumField({ label, unit, value, min, max, step, disabled, onChange, tooltip }: {
  label: string; unit: string; value: number; min: number; max: number; step: number
  disabled?: boolean; onChange: (v: number) => void; tooltip?: string
}) {
  return (
    <label className="flex items-center justify-between text-[10px]" title={tooltip}>
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

// C2: Illustrated shape picker card
function ShapeCard({ icon, label, selected, disabled, onClick }: {
  icon: React.ReactNode; label: string; selected: boolean; disabled?: boolean; onClick: () => void
}) {
  return (
    <button type="button" onClick={onClick} disabled={disabled}
      aria-pressed={selected}
      className={`flex flex-col items-center gap-0.5 p-1 rounded-md border transition-all duration-150
        ${disabled ? 'opacity-40 cursor-not-allowed border-gray-800' :
          selected
            ? 'border-teal-400 bg-teal-500/10 scale-[1.03] shadow-[0_0_6px_rgba(20,184,166,0.2)]'
            : 'border-gray-700 hover:border-gray-500 hover:scale-[1.01] cursor-pointer'}`}>
      <div className="w-8 h-8">{icon}</div>
      <span className={`text-[8px] font-medium ${selected ? 'text-teal-300' : 'text-gray-500'}`}>{label}</span>
    </button>
  )
}

// C4: Live SVG cross-section diagram
function CrossSectionDiagram({ v }: { v: AdvancedSupportSettings }) {
  const W = 120, H = 200
  const cx = W / 2
  // Scale dimensions for diagram (1mm = ~10px, clamped)
  const s = (mm: number) => Math.min(Math.max(mm * 8, 4), 50)
  const tipR = s(v.topTipUpperDiaMm / 2)
  const tipLR = s(v.topTipLowerDiaMm / 2)
  const connLen = Math.min(v.topConnectionLengthMm * 12, 40)
  const pillarR = s(v.middlePillarDiaMm / 2)
  const baseR = s(v.bottomBaseDiaMm / 2)
  const baseH = Math.min(v.bottomBaseThicknessMm * 10, 25)
  const raftH = Math.min(v.raftThicknessMm * 10, 15)

  // Layout from top to bottom
  const contactY = 10
  const tipY = contactY + s(v.topContactDepthMm)
  const connEndY = tipY + connLen
  const pillarEndY = H - baseH - raftH - 10
  const baseEndY = pillarEndY + baseH
  const raftEndY = baseEndY + raftH

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="w-full" style={{ maxHeight: 140 }}>
      {/* Part surface */}
      <line x1={10} y1={contactY} x2={W - 10} y2={contactY} className="stroke-gray-500" strokeWidth={2} />
      <text x={W - 8} y={contactY - 2} className="fill-gray-600" fontSize={7} textAnchor="end">Part</text>

      {/* Contact depth */}
      <circle cx={cx} cy={contactY} r={tipR} className="fill-teal-400/30 stroke-teal-400" strokeWidth={1} />

      {/* Connection taper */}
      <polygon points={`${cx - tipLR},${tipY} ${cx + tipLR},${tipY} ${cx + pillarR},${connEndY} ${cx - pillarR},${connEndY}`}
        className="fill-teal-400/15 stroke-teal-400/60" strokeWidth={0.8} />

      {/* Pillar */}
      <rect x={cx - pillarR} y={connEndY} width={pillarR * 2} height={pillarEndY - connEndY}
        className="fill-teal-400/10 stroke-teal-400/40" strokeWidth={0.8} />

      {/* Base */}
      <polygon points={`${cx - pillarR},${pillarEndY} ${cx + pillarR},${pillarEndY} ${cx + baseR},${baseEndY} ${cx - baseR},${baseEndY}`}
        className="fill-amber-400/20 stroke-amber-400/60" strokeWidth={0.8} />

      {/* Raft */}
      {raftH > 1 && (
        <rect x={cx - baseR - 4} y={baseEndY} width={(baseR + 4) * 2} height={raftH}
          rx={1} className="fill-cyan-400/15 stroke-cyan-400/40" strokeWidth={0.8} />
      )}

      {/* Dimension labels */}
      <text x={4} y={tipY + 4} className="fill-gray-500" fontSize={6}>{v.topContactDepthMm}mm</text>
      <text x={4} y={(tipY + connEndY) / 2 + 3} className="fill-gray-500" fontSize={6}>{v.topTipUpperDiaMm}/{v.topTipLowerDiaMm} {v.topTipAngleDeg}°</text>
      <text x={4} y={(connEndY + pillarEndY) / 2} className="fill-gray-500" fontSize={6}>{v.middlePillarDiaMm}mm</text>
      <text x={4} y={baseEndY - 2} className="fill-gray-500" fontSize={6}>{v.bottomBaseDiaMm}mm</text>

      {/* Build plate */}
      <line x1={5} y1={raftEndY + 3} x2={W - 5} y2={raftEndY + 3} className="stroke-gray-400" strokeWidth={2} />
      <text x={W - 8} y={raftEndY + 10} className="fill-gray-600" fontSize={7} textAnchor="end">Plate</text>
    </svg>
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

  return (
    <div className="space-y-2">
      {/* Auto / Manual toggle */}
      <div className="flex items-center gap-2">
        <div className="flex rounded-md overflow-hidden border border-gray-700 text-[10px]" role="radiogroup" aria-label="Sizing Mode">
          {(['auto', 'manual'] as const).map(m => (
            <button key={m} type="button" role="radio" aria-checked={value.sizingMode === m}
              onClick={() => onChange({ ...value, sizingMode: m })}
              className={`px-3 py-1 transition-colors capitalize
                ${value.sizingMode === m
                  ? 'bg-teal-500/20 text-teal-300 font-semibold'
                  : 'bg-gray-800 text-gray-500 hover:text-gray-300'}`}>
              {m === 'auto' ? 'Auto (Physics)' : 'Manual'}
            </button>
          ))}
        </div>
        <select value={value.preset} onChange={e => applyPreset(e.target.value as SupportPresetName)}
          aria-label="Size Preset"
          className="bg-gray-800 border border-gray-700 rounded px-2 py-1 text-[10px] text-gray-300">
          <option value="custom">Custom</option>
          <option value="light">Light</option>
          <option value="medium">Medium</option>
          <option value="heavy">Heavy</option>
        </select>
      </div>

      {/* C4: Live cross-section diagram */}
      {!disabled && (
        <div className="border border-gray-700/50 rounded-md p-1 bg-gray-900/50">
          <CrossSectionDiagram v={value} />
        </div>
      )}

      {/* Tab bar */}
      <div className="flex border-b border-gray-700" role="tablist" aria-label="Support Sections">
        {tabs.map(t => (
          <button key={t.key} type="button" role="tab" aria-selected={activeTab === t.key}
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
      <div className="space-y-2 pt-1">
        {activeTab === 'top' && <>
          {/* C2: Touch shape cards */}
          <div className="text-[9px] text-gray-500 mb-1">Touch Shape</div>
          <div className="grid grid-cols-3 gap-1">
            <ShapeCard icon={<IconTouchSphere />} label="Sphere" selected={value.topTouchShape === 'sphere'}
              disabled={disabled} onClick={() => set('topTouchShape', 'sphere')} />
            <ShapeCard icon={<IconTouchSkate />} label="Skate" selected={value.topTouchShape === 'skate'}
              disabled={disabled} onClick={() => set('topTouchShape', 'skate')} />
            <ShapeCard icon={<IconTouchNone />} label="None" selected={value.topTouchShape === 'none'}
              disabled={disabled} onClick={() => set('topTouchShape', 'none')} />
          </div>
          <NumField label="Contact Depth" unit="mm" value={value.topContactDepthMm} min={0.05} max={2} step={0.05}
            disabled={disabled} onChange={v => set('topContactDepthMm', v)}
            tooltip="How deep the tip penetrates the model surface. 0.2-0.4mm typical." />
          <NumField label="Tip Upper Dia" unit="mm" value={value.topTipUpperDiaMm} min={0.1} max={5} step={0.1}
            disabled={disabled} onChange={v => set('topTipUpperDiaMm', v)}
            tooltip="Diameter at the tip-model contact. Smaller = easier removal. 0.3-1.0mm typical." />
          <NumField label="Tip Lower Dia" unit="mm" value={value.topTipLowerDiaMm} min={0.1} max={5} step={0.1}
            disabled={disabled} onChange={v => {
              // Linked: derive angle from lower dia + connection length + upper dia
              const halfDelta = (value.topTipUpperDiaMm - v) / 2
              const angle = value.topConnectionLengthMm > 0
                ? Math.round(Math.atan2(halfDelta, value.topConnectionLengthMm) * 180 / Math.PI)
                : value.topTipAngleDeg
              onChange({ ...value, topTipLowerDiaMm: v, topTipAngleDeg: Math.max(5, Math.min(85, angle)), preset: 'custom' })
            }}
            tooltip="Diameter at the bottom of the tip taper. Linked to tip angle. 0.3-0.6mm typical." />
          <NumField label="Tip Angle" unit="°" value={value.topTipAngleDeg} min={5} max={85} step={1}
            disabled={disabled} onChange={v => {
              // Linked: derive lower dia from angle + connection length + upper dia
              const halfDelta = Math.tan(v * Math.PI / 180) * value.topConnectionLengthMm
              const lowerDia = Math.max(0.1, Math.round((value.topTipUpperDiaMm - halfDelta * 2) * 10) / 10)
              onChange({ ...value, topTipAngleDeg: v, topTipLowerDiaMm: Math.max(0.1, lowerDia), preset: 'custom' })
            }}
            tooltip="Taper angle of the tip cone. Linked to tip lower diameter. 30-60° typical." />
          {/* C2: Connection shape cards */}
          <div className="text-[9px] text-gray-500 mb-1 mt-2">Connection Shape</div>
          <div className="grid grid-cols-3 gap-1">
            <ShapeCard icon={<IconConnCone />} label="Cone" selected={value.topConnectionShape === 'cone'}
              disabled={disabled} onClick={() => set('topConnectionShape', 'cone')} />
            <ShapeCard icon={<IconConnCylinder />} label="Cylinder" selected={value.topConnectionShape === 'cylinder'}
              disabled={disabled} onClick={() => set('topConnectionShape', 'cylinder')} />
            <ShapeCard icon={<IconConnPyramid />} label="Pyramid" selected={value.topConnectionShape === 'pyramid'}
              disabled={disabled} onClick={() => set('topConnectionShape', 'pyramid')} />
          </div>
          <NumField label="Connection Length" unit="mm" value={value.topConnectionLengthMm} min={0.1} max={10} step={0.1}
            disabled={disabled} onChange={v => set('topConnectionLengthMm', v)}
            tooltip="Length of the taper from tip to pillar. 1.0-2.0mm typical." />
        </>}

        {activeTab === 'middle' && <>
          <NumField label="Pillar Diameter" unit="mm" value={value.middlePillarDiaMm} min={0.2} max={10} step={0.1}
            disabled={disabled} onChange={v => set('middlePillarDiaMm', v)}
            tooltip="Main shaft diameter. Thicker = stronger but more material. 0.6-2.0mm typical." />
          {/* C2: Pillar shape cards */}
          <div className="text-[9px] text-gray-500 mb-1 mt-2">Pillar Shape</div>
          <div className="grid grid-cols-3 gap-1">
            <ShapeCard icon={<IconPillarCylinder />} label="Cylinder" selected={value.middlePillarShape === 'cylinder'}
              disabled={disabled} onClick={() => set('middlePillarShape', 'cylinder')} />
            <ShapeCard icon={<IconPillarCube />} label="Cube" selected={value.middlePillarShape === 'cube'}
              disabled={disabled} onClick={() => set('middlePillarShape', 'cube')} />
            <ShapeCard icon={<IconPillarCross />} label="Cross" selected={value.middlePillarShape === 'cross'}
              disabled={disabled} onClick={() => set('middlePillarShape', 'cross')} />
          </div>
        </>}

        {activeTab === 'bottom' && <>
          <NumField label="Base Diameter" unit="mm" value={value.bottomBaseDiaMm} min={0.5} max={20} step={0.1}
            disabled={disabled} onChange={v => set('bottomBaseDiaMm', v)}
            tooltip="Pedestal diameter at the build plate. Wider = better adhesion. 2-5mm typical." />
          <NumField label="Base Thickness" unit="mm" value={value.bottomBaseThicknessMm} min={0.1} max={10} step={0.1}
            disabled={disabled} onChange={v => set('bottomBaseThicknessMm', v)}
            tooltip="Height of the base pedestal cone. 0.5-2.0mm typical." />
        </>}

        {activeTab === 'raft' && <>
          <NumField label="Raft Thickness" unit="mm" value={value.raftThicknessMm} min={0.1} max={5} step={0.1}
            disabled={disabled} onChange={v => set('raftThicknessMm', v)}
            tooltip="Raft pad thickness under each support base. 0.2-1.0mm typical." />
        </>}
      </div>

      {/* D6: Safety warnings when manual values are below recommended minimums */}
      {!disabled && (() => {
        const warnings: string[] = []
        // Physics minimum: tip radius >= 0.25mm (SupportSizer.R_TIP_MIN), so diameter >= 0.5mm
        if (value.topTipUpperDiaMm < 0.5) warnings.push(`Tip ${value.topTipUpperDiaMm}mm is below recommended minimum (0.5mm) — may tear off during peel`)
        // Physics minimum: pillar radius >= 0.3mm, so diameter >= 0.6mm
        if (value.middlePillarDiaMm < 0.6) warnings.push(`Pillar ${value.middlePillarDiaMm}mm is below recommended minimum (0.6mm) — may buckle`)
        // Base should be at least 2x pillar for adhesion
        if (value.bottomBaseDiaMm < value.middlePillarDiaMm * 1.5) warnings.push(`Base ${value.bottomBaseDiaMm}mm is small relative to pillar — may detach from plate`)
        return warnings.length > 0 ? (
          <div className="space-y-0.5">
            {warnings.map((w, i) => (
              <div key={i} className="text-[8px] px-2 py-1 rounded bg-amber-900/20 border border-amber-800/30 text-amber-400">
                {w}
              </div>
            ))}
          </div>
        ) : null
      })()}

      {disabled && (
        <div className="text-[9px] text-gray-600 italic bg-gray-800/30 rounded-md px-2 py-1.5">
          Dimensions are managed automatically by the physics engine.
          Switch to <span className="text-teal-500 font-medium cursor-pointer" onClick={() => onChange({ ...value, sizingMode: 'manual' })}>Manual</span> to override.
        </div>
      )}
    </div>
  )
}
