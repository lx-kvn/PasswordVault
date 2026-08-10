import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath } from 'node:url'

// Library mode——輸出給 FileLocker.Web／PasswordVault.Web 兩邊當一般 npm 相依套件安裝，
// 不是一個可以直接開起來跑的網站，所以沒有 dev server 設定，只有 build。
export default defineConfig({
  plugins: [vue()],
  build: {
    lib: {
      entry: fileURLToPath(new URL('./src/index.js', import.meta.url)),
      name: 'PasswordLockerUi',
      fileName: 'password-locker-ui',
      formats: ['es']
    },
    rollupOptions: {
      // vue 不打包進來，由消費端（FileLocker.Web／PasswordVault.Web）自己的 vue 版本提供——
      // 避免同一個頁面載入兩份 Vue runtime。
      external: ['vue'],
      output: {
        globals: { vue: 'Vue' }
      }
    }
  }
})
