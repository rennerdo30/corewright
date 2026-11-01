import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

// Dev only: the proxy reads the service's endpoint.json on every request (the token changes per service start)
// and adds the bearer token, so the UI also works in a normal browser at http://localhost:5173.
function endpoint(): { url: string; token: string } | null {
  // COREWRIGHT_ENDPOINT_FILE points the dev UI at a specific service (for example a test instance).
  if (process.env.COREWRIGHT_ENDPOINT_FILE) {
    try {
      return JSON.parse(readFileSync(process.env.COREWRIGHT_ENDPOINT_FILE, 'utf8'))
    } catch {
      return null
    }
  }
  for (const root of [process.env.LOCALAPPDATA, process.env.ProgramData]) {
    if (!root) continue
    try {
      return JSON.parse(readFileSync(join(root, 'Corewright', 'endpoint.json'), 'utf8'))
    } catch {
      /* try the next location */
    }
  }
  return null
}

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/Corewright.Service/wwwroot',
    emptyOutDir: true,
    target: 'es2022',
    chunkSizeWarningLimit: 400,
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: endpoint()?.url ?? 'http://127.0.0.1:47830',
        changeOrigin: true,
        configure: (proxy) => {
          proxy.on('proxyReq', (req) => {
            const ep = endpoint()
            if (ep) req.setHeader('Authorization', `Bearer ${ep.token}`)
          })
        },
      },
    },
  },
})
