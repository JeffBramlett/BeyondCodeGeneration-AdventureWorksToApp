import react from '@vitejs/plugin-react'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// https://vite.dev/config/
const clientDirectory = dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: resolve(clientDirectory, '../server/AdventureWorks.Api/wwwroot'),
    emptyOutDir: true,
  },
  server: {
    host: '127.0.0.1',
    port: 5184,
    strictPort: true,
  },
})
