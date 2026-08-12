import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  // 固定用 5183，不用 Vite 預設的 5173——FileLocker.Web 常態占用 5173，這台機器上兩個 repo
  // 常常同時開著，固定成不同埠避免兩邊悄悄連錯埠（PasswordVault.App/MainWindow.xaml.cs 的
  // Debug 導覽目標也對應這個埠）。
  server: {
    port: 5183,
  },
})
