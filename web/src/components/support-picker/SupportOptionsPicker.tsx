/**
 * Visual support options picker — illustrated card grid replacing dropdowns/checkboxes.
 * Each option is a card with an SVG illustration + label. Groups as labeled sections.
 */
import React from 'react'
import {
  IconSingle, IconForked, IconTree,
  IconPointContact, IconLineContact, IconFaceContact,
  IconReinfNone, IconReinfPairwise, IconReinfTriangular, IconReinfGlobal, IconBranchAttach,
  IconBaseNone, IconBaseMiniRaft, IconBaseFullGrid, IconBaseFullHex,
  IconDrainage, IconForceDriven,
  IconDensityLight, IconDensityMedium, IconDensityHeavy,
} from './SupportIcons'

// ── Types ──

export interface SupportOptionsConfig {
  // Group 1: Support Type
  supportType: 'single' | 'forked' | 'tree'
  forkTips: 2 | 3 | 4
  // Group 2: Contact Style
  contactStyle: 'point' | 'line' | 'face'
  // Group 3: Reinforcement
  reinforcementMode: 'none' | 'pairwise' | 'triangular' | 'global'
  branchAttachment: boolean
  // Group 4: Base / Raft
  raftMode: 'none' | 'mini' | 'fullGrid' | 'fullHex'
  // Group 5: Smart Options
  drainageAware: boolean
  forceDriven: boolean
  // Group 6: Density
  density: 'light' | 'medium' | 'heavy'
}

export const DEFAULT_SUPPORT_OPTIONS: SupportOptionsConfig = {
  supportType: 'single',
  forkTips: 3,
  contactStyle: 'point',
  reinforcementMode: 'pairwise',
  branchAttachment: false,
  raftMode: 'mini',
  drainageAware: false,
  forceDriven: false,
  density: 'medium',
}

interface Props {
  value: SupportOptionsConfig
  onChange: (v: SupportOptionsConfig) => void
}

// ── Card component ──

function OptionCard({
  icon, label, selected, onClick, description, ariaLabel,
}: {
  icon: React.ReactNode; label: string; selected: boolean; onClick: () => void
  description?: string; ariaLabel?: string
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      aria-label={ariaLabel ?? label}
      onClick={onClick}
      className={`
        relative flex flex-col items-center gap-1 p-2 rounded-lg border-2 transition-all duration-150
        focus:outline-none focus:ring-2 focus:ring-teal-400/50
        ${selected
          ? 'border-teal-400 dark:border-teal-300 bg-teal-500/10 dark:bg-teal-400/10 scale-[1.03] shadow-[0_0_8px_rgba(20,184,166,0.25)]'
          : 'border-gray-700 dark:border-gray-600 hover:border-gray-500 dark:hover:border-gray-400 hover:scale-[1.01]'}
        cursor-pointer select-none
      `}
    >
      <div className="w-14 h-14">{icon}</div>
      <span className={`text-[10px] font-medium ${selected ? 'text-teal-300 dark:text-teal-200' : 'text-gray-400 dark:text-gray-300'}`}>{label}</span>
      {description && <span className="text-[8px] text-gray-500 dark:text-gray-400 text-center leading-tight">{description}</span>}
    </button>
  )
}

function ToggleCard({
  icon, label, enabled, onToggle, ariaLabel,
}: {
  icon: React.ReactNode; label: string; enabled: boolean; onToggle: () => void; ariaLabel?: string
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={enabled}
      aria-label={ariaLabel ?? label}
      onClick={onToggle}
      className={`
        relative flex flex-col items-center gap-1 p-2 rounded-lg border-2 transition-all duration-150
        focus:outline-none focus:ring-2 focus:ring-teal-400/50
        ${enabled
          ? 'border-teal-400 dark:border-teal-300 bg-teal-500/10 dark:bg-teal-400/10'
          : 'border-gray-700 dark:border-gray-600 hover:border-gray-500 opacity-60'}
        cursor-pointer select-none
      `}
    >
      <div className="w-14 h-14">{icon}</div>
      <span className={`text-[10px] font-medium ${enabled ? 'text-teal-300' : 'text-gray-500'}`}>{label}</span>
    </button>
  )
}

function GroupLabel({ children }: { children: React.ReactNode }) {
  return <div className="text-[9px] font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-1.5">{children}</div>
}

// ── Main Picker ──

export default function SupportOptionsPicker({ value, onChange }: Props) {
  const set = <K extends keyof SupportOptionsConfig>(key: K, val: SupportOptionsConfig[K]) =>
    onChange({ ...value, [key]: val })

  return (
    <div className="space-y-4 p-2" role="group" aria-label="Support Options">
      {/* Group 1: Support Type */}
      <div>
        <GroupLabel>Support Type</GroupLabel>
        <div className="grid grid-cols-3 gap-1.5" role="radiogroup" aria-label="Support Type">
          <OptionCard icon={<IconSingle />} label="Single" selected={value.supportType === 'single'} onClick={() => set('supportType', 'single')} />
          <OptionCard
            icon={<IconForked tips={value.forkTips} />}
            label="Forked"
            selected={value.supportType === 'forked'}
            onClick={() => set('supportType', 'forked')}
            description={`${value.forkTips} tips`}
          />
          <OptionCard icon={<IconTree />} label="Tree" selected={value.supportType === 'tree'} onClick={() => set('supportType', 'tree')} />
        </div>
        {value.supportType === 'forked' && (
          <div className="flex items-center gap-2 mt-1.5 px-1">
            <span className="text-[9px] text-gray-500">Tips:</span>
            {([2, 3, 4] as const).map(n => (
              <button key={n} type="button"
                onClick={() => set('forkTips', n)}
                className={`w-6 h-6 rounded text-[10px] font-bold transition-colors ${value.forkTips === n ? 'bg-teal-500 text-white' : 'bg-gray-700 text-gray-400 hover:bg-gray-600'}`}
              >{n}</button>
            ))}
          </div>
        )}
      </div>

      {/* Group 2: Contact Style */}
      <div>
        <GroupLabel>Contact Style</GroupLabel>
        <div className="grid grid-cols-3 gap-1.5" role="radiogroup" aria-label="Contact Style">
          <OptionCard icon={<IconPointContact />} label="Point" selected={value.contactStyle === 'point'} onClick={() => set('contactStyle', 'point')} />
          <OptionCard icon={<IconLineContact />} label="Line" selected={value.contactStyle === 'line'} onClick={() => set('contactStyle', 'line')} />
          <OptionCard icon={<IconFaceContact />} label="Face" selected={value.contactStyle === 'face'} onClick={() => set('contactStyle', 'face')} />
        </div>
      </div>

      {/* Group 3: Reinforcement */}
      <div>
        <GroupLabel>Reinforcement</GroupLabel>
        <div className="grid grid-cols-4 gap-1.5" role="radiogroup" aria-label="Reinforcement Mode">
          <OptionCard icon={<IconReinfNone />} label="None" selected={value.reinforcementMode === 'none'} onClick={() => set('reinforcementMode', 'none')} />
          <OptionCard icon={<IconReinfPairwise />} label="Pair" selected={value.reinforcementMode === 'pairwise'} onClick={() => set('reinforcementMode', 'pairwise')} />
          <OptionCard icon={<IconReinfTriangular />} label="Triangle" selected={value.reinforcementMode === 'triangular'} onClick={() => set('reinforcementMode', 'triangular')} />
          <OptionCard icon={<IconReinfGlobal />} label="Global" selected={value.reinforcementMode === 'global'} onClick={() => set('reinforcementMode', 'global')} />
        </div>
        <div className="mt-1.5">
          <ToggleCard icon={<IconBranchAttach />} label="Branch Attach" enabled={value.branchAttachment} onToggle={() => set('branchAttachment', !value.branchAttachment)} />
        </div>
      </div>

      {/* Group 4: Base / Raft */}
      <div>
        <GroupLabel>Base / Raft</GroupLabel>
        <div className="grid grid-cols-4 gap-1.5" role="radiogroup" aria-label="Raft Mode">
          <OptionCard icon={<IconBaseNone />} label="None" selected={value.raftMode === 'none'} onClick={() => set('raftMode', 'none')} />
          <OptionCard icon={<IconBaseMiniRaft />} label="Mini" selected={value.raftMode === 'mini'} onClick={() => set('raftMode', 'mini')} />
          <OptionCard icon={<IconBaseFullGrid />} label="Grid" selected={value.raftMode === 'fullGrid'} onClick={() => set('raftMode', 'fullGrid')} />
          <OptionCard icon={<IconBaseFullHex />} label="Hex" selected={value.raftMode === 'fullHex'} onClick={() => set('raftMode', 'fullHex')} />
        </div>
      </div>

      {/* Group 5: Smart Options */}
      <div>
        <GroupLabel>Smart Options</GroupLabel>
        <div className="grid grid-cols-2 gap-1.5">
          <ToggleCard icon={<IconDrainage />} label="Drainage" enabled={value.drainageAware} onToggle={() => set('drainageAware', !value.drainageAware)} />
          <ToggleCard icon={<IconForceDriven />} label="Force-Driven" enabled={value.forceDriven} onToggle={() => set('forceDriven', !value.forceDriven)} />
        </div>
      </div>

      {/* Group 6: Density */}
      <div>
        <GroupLabel>Density</GroupLabel>
        <div className="grid grid-cols-3 gap-1.5" role="radiogroup" aria-label="Density">
          <OptionCard icon={<IconDensityLight />} label="Light" selected={value.density === 'light'} onClick={() => set('density', 'light')} />
          <OptionCard icon={<IconDensityMedium />} label="Medium" selected={value.density === 'medium'} onClick={() => set('density', 'medium')} />
          <OptionCard icon={<IconDensityHeavy />} label="Heavy" selected={value.density === 'heavy'} onClick={() => set('density', 'heavy')} />
        </div>
      </div>
    </div>
  )
}
