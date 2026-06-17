import { useState } from 'react'

interface AppConfig {
  apiUrl: string
  theme: 'dark' | 'light'
  autoSave: boolean
  supportProfileSaveSlots: number
  defaultExportFormat: string
  showAdvancedOptions: boolean
}

const DEFAULT_CONFIG: AppConfig = {
  apiUrl: window.location.origin.replace(/:\d+$/, ':5000'),
  theme: 'dark',
  autoSave: true,
  supportProfileSaveSlots: 5,
  defaultExportFormat: 'ctb',
  showAdvancedOptions: false,
}

function loadConfig(): AppConfig {
  try {
    const raw = localStorage.getItem('vatslicer-settings')
    return raw ? { ...DEFAULT_CONFIG, ...JSON.parse(raw) } : DEFAULT_CONFIG
  } catch { return DEFAULT_CONFIG }
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-gray-900 border border-gray-800/60 rounded-xl p-5">
      <h3 className="text-sm font-semibold text-gray-200 mb-4">{title}</h3>
      <div className="space-y-3">{children}</div>
    </div>
  )
}

function Field({ label, description, children }: { label: string; description?: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <div>
        <div className="text-sm text-gray-300">{label}</div>
        {description && <div className="text-[11px] text-gray-500 mt-0.5">{description}</div>}
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  )
}

function Toggle({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={`relative w-10 h-5 rounded-full transition-colors duration-200 ${checked ? 'bg-teal-500' : 'bg-gray-700'}`}
    >
      <span className={`absolute top-0.5 left-0.5 w-4 h-4 rounded-full bg-white shadow transition-transform duration-200 ${checked ? 'translate-x-5' : ''}`} />
    </button>
  )
}

export default function Settings() {
  const [config, setConfig] = useState(loadConfig)
  const [saved, setSaved] = useState(false)

  const patch = <K extends keyof AppConfig>(key: K, val: AppConfig[K]) => {
    setConfig(prev => ({ ...prev, [key]: val }))
    setSaved(false)
  }

  const save = () => {
    localStorage.setItem('vatslicer-settings', JSON.stringify(config))
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const reset = () => {
    setConfig(DEFAULT_CONFIG)
    localStorage.removeItem('vatslicer-settings')
  }

  return (
    <div className="max-w-2xl mx-auto space-y-6">
      <div className="flex items-center justify-between">
        <h2 className="text-xl font-bold text-white">Settings</h2>
        <div className="flex items-center gap-3">
          <button onClick={reset} className="px-3 py-1.5 text-xs rounded-lg bg-gray-800 text-gray-400 hover:text-gray-200 hover:bg-gray-700 transition">
            Reset Defaults
          </button>
          <button onClick={save} className={`px-4 py-1.5 text-xs font-medium rounded-lg transition-all duration-200 ${saved ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30' : 'bg-teal-500 text-white hover:bg-teal-400 shadow-sm'}`}>
            {saved ? 'Saved' : 'Save'}
          </button>
        </div>
      </div>

      <Section title="General">
        <Field label="API Server" description="Backend API URL for the slicer engine">
          <input
            type="text"
            value={config.apiUrl}
            onChange={e => patch('apiUrl', e.target.value)}
            className="input w-52 text-xs"
          />
        </Field>
        <Field label="Auto-Save Projects" description="Automatically save project state on changes">
          <Toggle checked={config.autoSave} onChange={v => patch('autoSave', v)} />
        </Field>
        <Field label="Advanced Options" description="Show expert-level settings in the slicer UI">
          <Toggle checked={config.showAdvancedOptions} onChange={v => patch('showAdvancedOptions', v)} />
        </Field>
      </Section>

      <Section title="Export">
        <Field label="Default Format" description="Default export format for new printer profiles">
          <select
            value={config.defaultExportFormat}
            onChange={e => patch('defaultExportFormat', e.target.value)}
            className="input text-xs px-2 py-1.5"
          >
            <option value="ctb">.ctb (ChiTuBox)</option>
            <option value="cbddlp">.cbddlp (Anycubic)</option>
            <option value="photon">.photon (Anycubic)</option>
            <option value="pwmx">.pwmx (Photon Mono X)</option>
            <option value="sl1">.sl1 (Prusa SL1)</option>
            <option value="zip">.zip (Generic)</option>
          </select>
        </Field>
        <Field label="Save Slots" description="Number of custom support profile save slots">
          <input
            type="number"
            min={1} max={20}
            value={config.supportProfileSaveSlots}
            onChange={e => patch('supportProfileSaveSlots', parseInt(e.target.value) || 5)}
            className="input w-16 text-xs text-center"
          />
        </Field>
      </Section>

      <Section title="About">
        <div className="text-xs text-gray-400 space-y-1">
          <p><span className="text-gray-500">Version:</span> 1.0.0</p>
          <p><span className="text-gray-500">Engine:</span> VATSlicer Resin Engine V2</p>
          <p><span className="text-gray-500">Formats:</span> CTB, CBDDLP, Photon, PWMX/S/B, SL1, ZIP+PNG</p>
        </div>
      </Section>
    </div>
  )
}
