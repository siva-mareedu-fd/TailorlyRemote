import path from 'node:path';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Dev server proxies the API to `dotnet run` (https://localhost:7080) so the SPA stays same-origin,
// exactly as Caddy does in Docker Compose.
const api = process.env.TAILORLY_API ?? 'https://localhost:7080';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, 'src') },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: api, changeOrigin: true, secure: false },
      '/healthz': { target: api, changeOrigin: true, secure: false },
      '/readyz': { target: api, changeOrigin: true, secure: false },
      '/admin/jobs': { target: api, changeOrigin: true, secure: false },
    },
  },
  build: {
    sourcemap: false,
    // No inline scripts or styles: the CSP is `script-src 'self'; style-src 'self'`.
    assetsInlineLimit: 0,
  },
});
