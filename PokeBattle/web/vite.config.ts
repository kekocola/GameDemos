import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5080',
      '/health': 'http://localhost:5080',
      '/ws': { target: 'ws://localhost:5080', ws: true }
    }
  },
  build: {
    outDir: '../src/PokeBattle.Server/wwwroot',
    emptyOutDir: true,
    sourcemap: true
  }
});
