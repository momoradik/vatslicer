/**
 * Support option icons — clean flat-modern SVG illustrations.
 * All share viewBox="0 0 120 120", consistent stroke weight (2.5), round caps/joins.
 * Scene: part near top, build plate near bottom, supports between.
 * Colors use CSS custom properties for theme compatibility.
 */
import React from 'react'

// Shared constants
const VB = "0 0 120 120"
const SW = 2.5 // stroke width
const LC = "round" as const
// Color roles (Tailwind classes applied via className on the SVG container)
// Part = currentColor (text color), Supports = stroke/fill set per element
const PLATE_Y = 100 // build plate Y position
const PART_Y = 25  // part bottom Y position

// Shared scene elements
const Plate = () => (
  <line x1={15} y1={PLATE_Y} x2={105} y2={PLATE_Y} className="stroke-gray-400 dark:stroke-gray-500" strokeWidth={3} strokeLinecap={LC} />
)
const PartBar = ({ x1 = 25, x2 = 95, y = PART_Y }: { x1?: number; x2?: number; y?: number }) => (
  <rect x={x1} y={y - 6} width={x2 - x1} height={12} rx={3} className="fill-gray-300 dark:fill-gray-600 stroke-gray-400 dark:stroke-gray-500" strokeWidth={1.5} />
)
const Tip = ({ x, y }: { x: number; y: number }) => (
  <circle cx={x} cy={y} r={3} className="fill-teal-400 dark:fill-teal-300" />
)
const Pillar = ({ x1, y1, x2, y2, accent = false }: { x1: number; y1: number; x2: number; y2: number; accent?: boolean }) => (
  <line x1={x1} y1={y1} x2={x2} y2={y2} className={accent ? "stroke-amber-400 dark:stroke-amber-300" : "stroke-teal-500 dark:stroke-teal-400"} strokeWidth={SW} strokeLinecap={LC} />
)

// ═══ GROUP 1: SUPPORT TYPE ═══

export const IconSingle = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    <Tip x={60} y={PART_Y} />
    <Pillar x1={60} y1={PART_Y + 3} x2={60} y2={PLATE_Y} />
    <circle cx={60} cy={PLATE_Y} r={4} className="fill-teal-500/30 dark:fill-teal-400/20" />
  </svg>
)

export const IconForked = ({ tips = 3 }: { tips?: number }) => {
  const forkY = 55
  const cx = 60
  const spread = tips === 2 ? 15 : tips === 3 ? 18 : 22
  const tipXs = Array.from({ length: tips }, (_, i) => cx - spread / 2 + (spread / (tips - 1)) * i)
  return (
    <svg viewBox={VB} className="w-full h-full">
      <Plate /><PartBar />
      {tipXs.map((tx, i) => <React.Fragment key={i}><Tip x={tx} y={PART_Y} /><Pillar x1={tx} y1={PART_Y + 3} x2={cx} y2={forkY} /></React.Fragment>)}
      <circle cx={cx} cy={forkY} r={3.5} className="fill-amber-400 dark:fill-amber-300" />
      <Pillar x1={cx} y1={forkY} x2={cx} y2={PLATE_Y} />
      <circle cx={cx} cy={PLATE_Y} r={4} className="fill-teal-500/30 dark:fill-teal-400/20" />
    </svg>
  )
}

export const IconTree = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[35, 55, 75, 85].map((tx, i) => <Tip key={i} x={tx} y={PART_Y} />)}
    {/* Branches merge at two levels */}
    <Pillar x1={35} y1={PART_Y + 3} x2={45} y2={50} />
    <Pillar x1={55} y1={PART_Y + 3} x2={45} y2={50} />
    <circle cx={45} cy={50} r={3} className="fill-amber-400 dark:fill-amber-300" />
    <Pillar x1={75} y1={PART_Y + 3} x2={80} y2={45} />
    <Pillar x1={85} y1={PART_Y + 3} x2={80} y2={45} />
    <circle cx={80} cy={45} r={3} className="fill-amber-400 dark:fill-amber-300" />
    {/* Merge into trunk */}
    <Pillar x1={45} y1={50} x2={60} y2={70} />
    <Pillar x1={80} y1={45} x2={60} y2={70} />
    <circle cx={60} cy={70} r={4} className="fill-amber-400 dark:fill-amber-300" />
    <Pillar x1={60} y1={70} x2={60} y2={PLATE_Y} />
    <circle cx={60} cy={PLATE_Y} r={5} className="fill-teal-500/30 dark:fill-teal-400/20" />
  </svg>
)

// ═══ GROUP 2: CONTACT STYLE ═══

export const IconPointContact = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    <Tip x={60} y={PART_Y} />
    <circle cx={60} cy={PART_Y} r={6} className="stroke-amber-400 dark:stroke-amber-300 fill-none" strokeWidth={1.5} strokeDasharray="3 2" />
    <Pillar x1={60} y1={PART_Y + 3} x2={60} y2={PLATE_Y} />
  </svg>
)

export const IconLineContact = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {/* Row of 5 tips along the part edge */}
    {[32, 44, 56, 68, 80].map((tx, i) => (
      <React.Fragment key={i}>
        <Tip x={tx} y={PART_Y} />
        <Pillar x1={tx} y1={PART_Y + 3} x2={tx} y2={PLATE_Y} />
      </React.Fragment>
    ))}
    {/* Highlight the edge line */}
    <line x1={30} y1={PART_Y - 1} x2={82} y2={PART_Y - 1} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
  </svg>
)

export const IconFaceContact = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate />
    {/* Wider slab to show face */}
    <rect x={25} y={PART_Y - 8} width={70} height={16} rx={3} className="fill-gray-300 dark:fill-gray-600 stroke-gray-400 dark:stroke-gray-500" strokeWidth={1.5} />
    {/* 3x3 grid of tips */}
    {[38, 56, 74].map((tx, ti) =>
      [PART_Y + 2].map((ty, tj) => (
        <React.Fragment key={`${ti}-${tj}`}>
          <Tip x={tx} y={ty} />
          <Pillar x1={tx} y1={ty + 3} x2={tx} y2={PLATE_Y} />
        </React.Fragment>
      ))
    )}
    {/* Grid highlight dots */}
    {[38, 56, 74].map((tx) => (
      <circle key={tx} cx={tx} cy={PART_Y - 2} r={1.5} className="fill-amber-400 dark:fill-amber-300" />
    ))}
  </svg>
)

// ═══ GROUP 3: REINFORCEMENT ═══

const ThreePillars = ({ connected = false, triangulated = false, global: isGlobal = false }) => {
  const xs = isGlobal ? [28, 48, 68, 82, 96] : [35, 60, 85]
  return (
    <>
      <Plate /><PartBar />
      {xs.map((x, i) => (
        <React.Fragment key={i}>
          <Tip x={x} y={PART_Y} />
          <Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} />
        </React.Fragment>
      ))}
      {connected && !triangulated && !isGlobal && (
        <>
          <line x1={35} y1={50} x2={60} y2={50} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
          <line x1={60} y1={65} x2={85} y2={65} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
          <line x1={35} y1={75} x2={60} y2={75} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
        </>
      )}
      {triangulated && (
        <>
          <line x1={35} y1={50} x2={60} y2={50} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
          <line x1={60} y1={50} x2={85} y2={65} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
          <line x1={85} y1={65} x2={35} y2={75} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
          <line x1={35} y1={75} x2={60} y2={75} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={2} strokeLinecap={LC} />
          {/* Triangle highlight */}
          <polygon points="35,50 60,50 35,75" className="fill-amber-400/10 dark:fill-amber-300/10 stroke-none" />
          <polygon points="60,50 85,65 35,75" className="fill-amber-400/10 dark:fill-amber-300/10 stroke-none" />
        </>
      )}
      {isGlobal && (
        <>
          {/* Dense triangulated network */}
          {[[28,48],[48,68],[68,82],[82,96],[28,68],[48,82],[68,96]].map(([a,b],i) => (
            <line key={i} x1={a} y1={55 + (i%2)*15} x2={b} y2={60 + ((i+1)%2)*10} className="stroke-amber-400/70 dark:stroke-amber-300/60" strokeWidth={1.5} strokeLinecap={LC} />
          ))}
        </>
      )}
    </>
  )
}

export const IconReinfNone = () => <svg viewBox={VB} className="w-full h-full"><ThreePillars /></svg>
export const IconReinfPairwise = () => <svg viewBox={VB} className="w-full h-full"><ThreePillars connected /></svg>
export const IconReinfTriangular = () => <svg viewBox={VB} className="w-full h-full"><ThreePillars triangulated /></svg>
export const IconReinfGlobal = () => <svg viewBox={VB} className="w-full h-full"><ThreePillars global /></svg>

export const IconBranchAttach = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    <Tip x={45} y={PART_Y} /><Tip x={75} y={PART_Y} />
    <Pillar x1={45} y1={PART_Y + 3} x2={45} y2={PLATE_Y} />
    <Pillar x1={75} y1={PART_Y + 3} x2={75} y2={60} />
    {/* Branch attachment joint */}
    <line x1={75} y1={60} x2={45} y2={70} className="stroke-amber-400 dark:stroke-amber-300" strokeWidth={SW} strokeLinecap={LC} />
    <circle cx={45} cy={70} r={3.5} className="fill-amber-400 dark:fill-amber-300" />
    <circle cx={75} cy={60} r={2.5} className="fill-amber-400 dark:fill-amber-300" />
  </svg>
)

// ═══ GROUP 4: BASE / RAFT ═══

export const IconBaseNone = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[40, 60, 80].map((x, i) => <React.Fragment key={i}><Tip x={x} y={PART_Y} /><Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} /></React.Fragment>)}
  </svg>
)

export const IconBaseMiniRaft = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[40, 60, 80].map((x, i) => (
      <React.Fragment key={i}>
        <Tip x={x} y={PART_Y} />
        <Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y - 4} />
        <rect x={x - 6} y={PLATE_Y - 4} width={12} height={4} rx={1.5} className="fill-amber-400/40 dark:fill-amber-300/30 stroke-amber-400 dark:stroke-amber-300" strokeWidth={1} />
      </React.Fragment>
    ))}
  </svg>
)

export const IconBaseFullGrid = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[40, 60, 80].map((x, i) => <React.Fragment key={i}><Tip x={x} y={PART_Y} /><Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y - 5} /></React.Fragment>)}
    {/* Grid raft mat */}
    <rect x={28} y={PLATE_Y - 5} width={64} height={5} rx={1} className="fill-amber-400/20 dark:fill-amber-300/15" />
    {/* Grid lines */}
    {[35, 45, 55, 65, 75, 85].map((gx, i) => (
      <line key={i} x1={gx} y1={PLATE_Y - 5} x2={gx} y2={PLATE_Y} className="stroke-amber-400/50 dark:stroke-amber-300/40" strokeWidth={0.8} />
    ))}
    {[PLATE_Y - 3].map((gy, i) => (
      <line key={i} x1={28} y1={gy} x2={92} y2={gy} className="stroke-amber-400/50 dark:stroke-amber-300/40" strokeWidth={0.8} />
    ))}
  </svg>
)

export const IconBaseFullHex = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[40, 60, 80].map((x, i) => <React.Fragment key={i}><Tip x={x} y={PART_Y} /><Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y - 5} /></React.Fragment>)}
    {/* Hex raft mat */}
    <rect x={28} y={PLATE_Y - 5} width={64} height={5} rx={1} className="fill-violet-400/20 dark:fill-violet-300/15" />
    {/* Hex pattern (simplified) */}
    {[36, 48, 60, 72, 84].map((hx, i) => (
      <React.Fragment key={i}>
        <circle cx={hx} cy={PLATE_Y - 2.5} r={4} className="stroke-violet-400/60 dark:stroke-violet-300/50 fill-none" strokeWidth={0.6} />
      </React.Fragment>
    ))}
  </svg>
)

// ═══ GROUP 5: SMART OPTIONS ═══

export const IconDrainage = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate />
    {/* Cup cross-section (hollow) */}
    <path d="M30,30 L30,75 L90,75 L90,30" fill="none" className="stroke-gray-400 dark:stroke-gray-500" strokeWidth={3} strokeLinecap={LC} />
    <line x1={34} y1={30} x2={34} y2={75} className="stroke-gray-300 dark:stroke-gray-600" strokeWidth={3} />
    <line x1={86} y1={30} x2={86} y2={75} className="stroke-gray-300 dark:stroke-gray-600" strokeWidth={3} />
    {/* Drain hole in the wall */}
    <circle cx={90} cy={65} r={4} className="fill-amber-400 dark:fill-amber-300" />
    {/* Droplet escaping */}
    <path d="M98,65 Q102,63 100,58 Q98,63 98,65Z" className="fill-sky-400 dark:fill-sky-300" />
    <circle cx={104} cy={70} r={1.5} className="fill-sky-400/60 dark:fill-sky-300/50" />
  </svg>
)

export const IconForceDriven = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate />
    <PartBar x1={20} x2={100} />
    {/* Dense tips on left (high force), sparse on right (low force) */}
    {[25, 30, 35, 40, 45].map((x, i) => <React.Fragment key={`d${i}`}><Tip x={x} y={PART_Y} /><Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} /></React.Fragment>)}
    {[65, 85].map((x, i) => <React.Fragment key={`s${i}`}><Tip x={x} y={PART_Y} /><Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} /></React.Fragment>)}
    {/* Force gradient indicator */}
    <text x={32} y={PLATE_Y + 12} className="fill-amber-400 dark:fill-amber-300" fontSize={8} fontWeight="bold" textAnchor="middle">HIGH</text>
    <text x={78} y={PLATE_Y + 12} className="fill-gray-500 dark:fill-gray-400" fontSize={8} textAnchor="middle">low</text>
  </svg>
)

// ═══ GROUP 6: DENSITY ═══

export const IconDensityLight = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[40, 80].map((x, i) => (
      <React.Fragment key={i}>
        <Tip x={x} y={PART_Y} />
        <line x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} className="stroke-teal-500/60 dark:stroke-teal-400/50" strokeWidth={1.5} strokeLinecap={LC} />
      </React.Fragment>
    ))}
  </svg>
)

export const IconDensityMedium = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[35, 55, 75].map((x, i) => (
      <React.Fragment key={i}>
        <Tip x={x} y={PART_Y} />
        <Pillar x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} />
      </React.Fragment>
    ))}
  </svg>
)

export const IconDensityHeavy = () => (
  <svg viewBox={VB} className="w-full h-full">
    <Plate /><PartBar />
    {[30, 42, 54, 66, 78, 90].map((x, i) => (
      <React.Fragment key={i}>
        <Tip x={x} y={PART_Y} />
        <line x1={x} y1={PART_Y + 3} x2={x} y2={PLATE_Y} className="stroke-teal-500 dark:stroke-teal-400" strokeWidth={3} strokeLinecap={LC} />
      </React.Fragment>
    ))}
  </svg>
)
