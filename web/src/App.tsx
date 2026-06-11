import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import Layout from './components/layout/Layout'
import Dashboard from './pages/Dashboard'
import StlImport from './pages/StlImport'
import PrinterConfig from './pages/PrinterConfig'
import NotFound from './pages/NotFound'
import DesignReview from './pages/DesignReview'
import { useBranding } from './hooks/useBranding'

function BrandingProvider({ children }: { children: React.ReactNode }) {
  useBranding()
  return <>{children}</>
}

export default function App() {
  return (
    <BrowserRouter>
      <BrandingProvider>
        <Routes>
          <Route path="/" element={<Layout />}>
            <Route index element={<Navigate to="/dashboard" replace />} />
            <Route path="dashboard" element={<Dashboard />} />
            <Route path="import"         element={<StlImport />} />
            <Route path="printer-config" element={<PrinterConfig />} />
            <Route path="design-review"  element={<DesignReview />} />
            <Route path="*"              element={<NotFound />} />
          </Route>
        </Routes>
      </BrandingProvider>
    </BrowserRouter>
  )
}
