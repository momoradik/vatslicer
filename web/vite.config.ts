import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    host: 'localhost',
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:4444',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:4444',
        ws: true,
        changeOrigin: true,
      },
    },
  },
  optimizeDeps: {
    include: [
      'three',
      'three/examples/jsm/exporters/STLExporter.js',
      'three/examples/jsm/loaders/STLLoader.js',
    ],
  },
  build: {
    outDir: '../src/HybridSlicer.Api/wwwroot',
    emptyOutDir: true,
  },
})
