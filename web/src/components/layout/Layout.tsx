import { Outlet, useLocation } from 'react-router-dom'
import { useRef, useEffect, useState } from 'react'
import Sidebar from './Sidebar'
import Header from './Header'
import KeyboardShortcuts from '../KeyboardShortcuts'

export default function Layout() {
  const location = useLocation()
  const [transitioning, setTransitioning] = useState(false)
  const prevPath = useRef(location.pathname)

  useEffect(() => {
    if (prevPath.current !== location.pathname) {
      setTransitioning(true)
      prevPath.current = location.pathname
      const t = setTimeout(() => setTransitioning(false), 150)
      return () => clearTimeout(t)
    }
  }, [location.pathname])

  return (
    <div className="flex h-screen overflow-hidden bg-gray-950">
      <Sidebar />
      <div className="flex flex-col flex-1 overflow-hidden">
        <Header />
        <main className={`flex-1 overflow-y-auto p-6 transition-opacity duration-150 ${transitioning ? 'opacity-0' : 'opacity-100'}`}>
          <Outlet />
        </main>
      </div>
      <KeyboardShortcuts />
    </div>
  )
}
