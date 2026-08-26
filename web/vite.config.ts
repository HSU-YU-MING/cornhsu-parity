import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 建置產物直接落進 Parity.Server 的 wwwroot(gitignore),伺服器偵測到就改served 它;
// 開發時 vite dev server 把 /api 代理到本機伺服器。
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/Parity.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: { '/api': 'http://127.0.0.1:4322' },
  },
})
