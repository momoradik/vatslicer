import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    host: 'localhost',
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5555',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5555',
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
      'three/examples/jsm/utils/BufferGeometryUtils.js',
      'three-mesh-bvh',
    ],
  },
  build: {
    outDir: '../src/HybridSlicer.Api/wwwroot',
    emptyOutDir: true,
  },
})
