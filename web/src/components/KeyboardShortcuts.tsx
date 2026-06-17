import { useState, useEffect } from 'react'

const shortcuts = [
  { keys: ['Ctrl', 'Z'], action: 'Undo last transform' },
  { keys: ['Ctrl', 'S'], action: 'Save project' },
  { keys: ['Delete'], action: 'Delete selected model' },
  { keys: ['A'], action: 'Toggle Analyze mode' },
  { keys: ['?'], action: 'Show keyboard shortcuts' },
  { keys: ['Esc'], action: 'Deselect / close panel' },
  { keys: ['1'], action: 'Add support mode' },
  { keys: ['2'], action: 'Delete support mode' },
  { keys: ['0'], action: 'Normal mode (no edit)' },
]

export default function KeyboardShortcuts() {
  const [open, setOpen] = useState(false)

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === '?' && !e.ctrlKey && !e.metaKey) {
        e.preventDefault()
        setOpen(p => !p)
      }
      if (e.key === 'Escape' && open) setOpen(false)
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [open])

  if (!open) return null

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm"
      onClick={() => setOpen(false)}>
      <div className="bg-gray-900 border border-gray-700 rounded-2xl p-6 w-96 shadow-2xl"
        onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between mb-4">
          <h3 className="text-sm font-semibold text-white">Keyboard Shortcuts</h3>
          <button onClick={() => setOpen(false)}
            className="text-gray-500 hover:text-gray-300 transition text-lg leading-none">&times;</button>
        </div>
        <div className="space-y-2">
          {shortcuts.map((s, i) => (
            <div key={i} className="flex items-center justify-between py-1">
              <span className="text-xs text-gray-400">{s.action}</span>
              <div className="flex gap-1">
                {s.keys.map((k, j) => (
                  <kbd key={j} className="px-2 py-0.5 rounded bg-gray-800 border border-gray-600 text-[10px] text-gray-300 font-mono min-w-[24px] text-center">
                    {k}
                  </kbd>
                ))}
              </div>
            </div>
          ))}
        </div>
        <p className="text-[10px] text-gray-600 mt-4 text-center">Press ? to toggle this overlay</p>
      </div>
    </div>
  )
}
