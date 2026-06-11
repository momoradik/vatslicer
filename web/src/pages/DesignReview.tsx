/**
 * Dev-only design review page — renders all support icons in a labeled grid
 * for contact-sheet screenshot capture. Access at /design-review.
 */
import { useState } from 'react'
import SupportOptionsPicker, { DEFAULT_SUPPORT_OPTIONS, type SupportOptionsConfig } from '../components/support-picker/SupportOptionsPicker'
import {
  IconSingle, IconForked, IconTree,
  IconPointContact, IconLineContact, IconFaceContact,
  IconReinfNone, IconReinfPairwise, IconReinfTriangular, IconReinfGlobal, IconBranchAttach,
  IconBaseNone, IconBaseMiniRaft, IconBaseFullGrid, IconBaseFullHex,
  IconDrainage, IconForceDriven,
  IconDensityLight, IconDensityMedium, IconDensityHeavy,
} from '../components/support-picker/SupportIcons'

const ALL_ICONS = [
  { name: 'Single', icon: <IconSingle />, group: 'Type' },
  { name: 'Forked (2)', icon: <IconForked tips={2} />, group: 'Type' },
  { name: 'Forked (3)', icon: <IconForked tips={3} />, group: 'Type' },
  { name: 'Forked (4)', icon: <IconForked tips={4} />, group: 'Type' },
  { name: 'Tree', icon: <IconTree />, group: 'Type' },
  { name: 'Point', icon: <IconPointContact />, group: 'Contact' },
  { name: 'Line', icon: <IconLineContact />, group: 'Contact' },
  { name: 'Face', icon: <IconFaceContact />, group: 'Contact' },
  { name: 'Reinf None', icon: <IconReinfNone />, group: 'Reinforcement' },
  { name: 'Reinf Pairwise', icon: <IconReinfPairwise />, group: 'Reinforcement' },
  { name: 'Reinf Triangular', icon: <IconReinfTriangular />, group: 'Reinforcement' },
  { name: 'Reinf Global', icon: <IconReinfGlobal />, group: 'Reinforcement' },
  { name: 'Branch Attach', icon: <IconBranchAttach />, group: 'Reinforcement' },
  { name: 'Base None', icon: <IconBaseNone />, group: 'Base/Raft' },
  { name: 'Mini Rafts', icon: <IconBaseMiniRaft />, group: 'Base/Raft' },
  { name: 'Full Grid', icon: <IconBaseFullGrid />, group: 'Base/Raft' },
  { name: 'Full Hex', icon: <IconBaseFullHex />, group: 'Base/Raft' },
  { name: 'Drainage', icon: <IconDrainage />, group: 'Smart' },
  { name: 'Force-Driven', icon: <IconForceDriven />, group: 'Smart' },
  { name: 'Light', icon: <IconDensityLight />, group: 'Density' },
  { name: 'Medium', icon: <IconDensityMedium />, group: 'Density' },
  { name: 'Heavy', icon: <IconDensityHeavy />, group: 'Density' },
]

export default function DesignReview() {
  const [config, setConfig] = useState<SupportOptionsConfig>(DEFAULT_SUPPORT_OPTIONS)

  return (
    <div className="min-h-screen bg-gray-900 text-gray-100 p-8">
      <h1 className="text-2xl font-bold mb-6">Support Options — Design Review</h1>

      {/* Contact Sheet: all icons */}
      <section id="contact-sheet" className="mb-12">
        <h2 className="text-lg font-semibold mb-4 text-teal-400">Icon Contact Sheet ({ALL_ICONS.length} icons)</h2>
        <div className="grid grid-cols-6 gap-4">
          {ALL_ICONS.map((item, i) => (
            <div key={i} className="flex flex-col items-center gap-1 p-3 bg-gray-800 rounded-lg border border-gray-700">
              <div className="w-20 h-20">{item.icon}</div>
              <span className="text-[10px] text-gray-400">{item.group}</span>
              <span className="text-xs font-medium">{item.name}</span>
            </div>
          ))}
        </div>
      </section>

      {/* Live Picker Panel */}
      <section id="picker-panel" className="max-w-sm">
        <h2 className="text-lg font-semibold mb-4 text-teal-400">Live Picker Panel</h2>
        <div className="bg-gray-800 rounded-xl border border-gray-700 overflow-hidden">
          <SupportOptionsPicker value={config} onChange={setConfig} />
        </div>
        <pre className="mt-4 text-[10px] text-gray-500 bg-gray-800 p-3 rounded overflow-auto">
          {JSON.stringify(config, null, 2)}
        </pre>
      </section>
    </div>
  )
}
