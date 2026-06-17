import { useLocation } from 'react-router-dom'
import { useAppStore } from '../../store'

const PAGE_TITLES: Record<string, string> = {
  '/dashboard': 'Dashboard',
  '/import': 'Slicer',
  '/printer-config': 'Printer Setup',
  '/settings': 'Settings',
  '/design-review': 'Design Review',
}

export default function Header() {
  const { machineConnected, extruderTemp, bedTemp } = useAppStore()
  const location = useLocation()
  const title = PAGE_TITLES[location.pathname] ?? ''

  return (
    <header className="h-12 bg-gray-900/80 backdrop-blur-sm border-b border-gray-800/60 flex items-center justify-between px-6 shrink-0">
      <div className="flex items-center gap-2">
        <h2 className="text-sm font-medium text-gray-300">{title}</h2>
      </div>

      <div className="flex items-center gap-5 text-xs">
        {extruderTemp !== null && (
          <span className="text-orange-400 font-mono">
            <svg className="w-3.5 h-3.5 inline-block mr-1 -mt-0.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M14 4v10.54a4 4 0 1 1-4 0V4a2 2 0 0 1 4 0z" /></svg>
            {extruderTemp.toFixed(1)}°C
          </span>
        )}
        {bedTemp !== null && (
          <span className="text-blue-400 font-mono">
            <svg className="w-3.5 h-3.5 inline-block mr-1 -mt-0.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="2" y="6" width="20" height="12" rx="2" /></svg>
            {bedTemp.toFixed(1)}°C
          </span>
        )}

        <div className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[11px] font-medium transition-colors
          ${machineConnected
            ? 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/20'
            : 'bg-gray-800 text-gray-500 border border-gray-700/50'}`}
        >
          <span className={`w-1.5 h-1.5 rounded-full ${machineConnected ? 'bg-emerald-400 animate-pulse' : 'bg-gray-600'}`} />
          {machineConnected ? 'Connected' : 'Offline'}
        </div>
      </div>
    </header>
  )
}
