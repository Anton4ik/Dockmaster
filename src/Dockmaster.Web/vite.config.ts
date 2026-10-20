import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev: `npm run dev` proxies /api and /hub to the .NET server on :8080.
// Build: output lands in the server's wwwroot so `dotnet run` / the Docker image serve it.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Dockmaster.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8080', changeOrigin: false },
      '/hub': { target: 'http://localhost:8080', ws: true },
    },
  },
})
