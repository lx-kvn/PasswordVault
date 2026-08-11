---
name: run
description: 啟動 PasswordVault.exe（WPF）跟／或 PasswordVault.Web（Vite）並用背景截圖驗證畫面實際長什麼樣子，不用手動開視窗、不會搶走使用者的前景焦點。用在「這個 UI 改動有沒有生效」「畫面看起來對不對」這類需要眼見為憑的情境。
---

# 啟動並截圖 PasswordVault

`dotnet test` 只驗證 `PasswordVault.Core` 的邏輯層，不驗證 UI 有沒有正常渲染。這個 skill
提供一套可重複執行的啟動＋截圖流程，取代「手動開視窗、手動截圖」。

**這個 skill 只能證明「有沒有崩潰、內容有沒有出現」，不能取代人眼判斷排版好不好看、間距對不對這種
細節主觀判斷**——複雜的排版變更截完圖之後還是要仔細看，必要時請使用者也看一眼。

## 現況：PasswordVault.exe 跟 PasswordVault.Web 還沒接在一起

跟 FileLocker（`MainWindow` 的 WebView2 在 Debug 建置會連到 Vite dev server）不同，
`PasswordVault.App` 目前是骨架階段——`MainWindow.xaml` 只是一個靜態占位視窗，還沒接 WebView2、
還沒連到 `PasswordVault.Web`。這兩塊現在是**兩個各自獨立要截圖的東西**，不是同一個畫面的兩種
看法：

### A. `PasswordVault.Web` 前端（Vue，密碼庫共用元件 `@lx-kvn/password-locker-ui` 的實際畫面）

```bash
cd src/PasswordVault.Web
npm run dev -- --port 5183 &   # 用 5183 避開 FileLocker.Web 常用的 5173，兩邊能同時跑
```

```bash
npx playwright screenshot --viewport-size=1100,750 "http://localhost:5183/" /path/to/out.png
```

第一次在這台機器上跑需要先下載瀏覽器執行檔（一次性，之後都是本機快取）：
```bash
npx playwright install chromium
```

改了 `packages/password-locker-ui` 裡的程式碼，要先重新建置套件、且 dev server 要重開才會吃到
最新版本（Vite 的依賴預先打包快取不會自動偵測 workspace 連結套件的 `dist/` 變動）：
```bash
npm run build -w @lx-kvn/password-locker-ui
rm -rf src/PasswordVault.Web/node_modules/.vite
# 然後重新啟動 npm run dev
```

### B. `PasswordVault.exe`（WPF 骨架視窗——目前只有占位文字，沒有真正的密碼庫畫面）

1. **檢查有沒有已經在跑的實體**（單一執行個體 Mutex，見 CLAUDE.md「已知的坑」）：
   ```powershell
   Get-Process PasswordVault -ErrorAction SilentlyContinue
   ```
   有的話、且要驗證新編譯的程式碼：先關掉再重新建置/啟動，不然編譯可能被鎖住的 DLL 擋下來
   （跟 FileLocker 那份 skill 記錄的坑同一類）。只是想看現在畫面長怎樣，不用重啟，直接跳到步驟 3。

2. **啟動**：
   ```bash
   dotnet run --project src/PasswordVault.App &
   ```
   啟動當下**會自動把自己的視窗搶到前景**（`WindowActivation.ForceToForeground`，App.xaml.cs
   既有行為，仿照 FileLocker.App，不是這個 skill 的問題）——只有第一次啟動這一下會打斷使用者，
   啟動完成後的截圖動作本身不會再搶焦點。

3. **背景截圖**（不搶前景、不打斷使用者）：
   ```powershell
   pwsh .claude/skills/run/screenshot-window.ps1 -ProcessName PasswordVault -OutputPath C:\path\to\out.png
   ```
   用 Read 工具打開看。這支腳本用 `PrintWindow` API，前提是視窗真的開著（不是縮到系統匣）。

## 下一輪 MainWindow 接上 WebView2 之後

到時候這個 skill 要更新成跟 FileLocker 那份一樣的單一啟動流程（先起 Vite dev server，
`dotnet run` 的 WPF 視窗會顯示 dev server 內容，只截 B 就能看到 A 的畫面）——這份文件先誠實
記錄目前兩者還沒接上的現況，不要假裝已經做完。

## 收尾

```powershell
Get-Process PasswordVault -ErrorAction SilentlyContinue | Stop-Process -Force
```
```bash
pkill -f "vite" 2>/dev/null
```
