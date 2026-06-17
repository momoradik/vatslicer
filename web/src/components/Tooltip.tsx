import { useState, useRef, type ReactNode } from 'react'

interface TooltipProps {
  content: string
  children: ReactNode
  position?: 'top' | 'bottom' | 'left' | 'right'
  delay?: number
}

export default function Tooltip({ content, children, position = 'top', delay = 300 }: TooltipProps) {
  const [show, setShow] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout>>()

  const enter = () => {
    timer.current = setTimeout(() => setShow(true), delay)
  }
  const leave = () => {
    clearTimeout(timer.current)
    setShow(false)
  }

  const posClass = {
    top: 'bottom-full left-1/2 -translate-x-1/2 mb-1.5',
    bottom: 'top-full left-1/2 -translate-x-1/2 mt-1.5',
    left: 'right-full top-1/2 -translate-y-1/2 mr-1.5',
    right: 'left-full top-1/2 -translate-y-1/2 ml-1.5',
  }[position]

  return (
    <div className="relative inline-flex" onMouseEnter={enter} onMouseLeave={leave}>
      {children}
      {show && (
        <div className={`absolute z-50 ${posClass} px-2 py-1 text-[10px] text-gray-200 bg-gray-800 border border-gray-700 rounded-md shadow-lg whitespace-nowrap pointer-events-none animate-in fade-in duration-150`}>
          {content}
        </div>
      )}
    </div>
  )
}
