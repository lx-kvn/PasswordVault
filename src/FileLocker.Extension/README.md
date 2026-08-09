# FileLocker 密碼庫瀏覽器擴充功能

跟 `FileLocker.PasswordLockerNativeHost`（純轉接層）、`FileLocker.App`（實際驗證/加解密）搭配使用，見 `FileLocker_密碼庫_功能規劃.md` 第 5 節。這個目錄本身不是一個 npm/build 專案——純 JS + 靜態 `manifest.json`，不需要任何建置步驟。

## 本機測試（開發人員模式，尚未上架 Chrome 線上應用程式商店）

`manifest.json` 已經固定了 `"key"` 欄位（開發用的簽署金鑰，私鑰放在這個 repo 之外，見下方「簽署金鑰」一節），所以這個擴充功能不管用哪個路徑載入、重新載入幾次，Chrome 指派的 ID 永遠是同一個（`ihhcdgkacinknnbaibnjpaamfhbebpdj`）——`extension-id.txt` 已經預先填好這個值，並且會隨 `FileLocker.PasswordLockerNativeHost` 一起建置、複製到 `plugins/PasswordLocker/`（見 `FileLocker.PasswordLockerNativeHost.csproj`），**不需要每次手動去 `chrome://extensions` 複製貼上**。

1. Chrome 網址列輸入 `chrome://extensions`，右上角開啟「開發人員模式」。
2. 「載入未封裝項目」，選這個資料夾（`src/FileLocker.Extension`）。
3. 重新啟動 FileLocker（或觸發一次密碼庫部件重新載入），確認 `%LocalAppData%\FileLocker\NativeMessagingHost\com.filelocker.passwordlocker.json` 有被建立/更新，且 `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.filelocker.passwordlocker` 指向這個檔案——`PasswordLockerNativeHostRegistrar.EnsureRegistered` 找不到 `plugins/PasswordLocker/extension-id.txt` 或 `FileLocker.PasswordLockerNativeHost.exe` 就會安靜略過不註冊，這兩個檔案有沒有同步過去（CLAUDE.md「已知的坑」）是最常見的漏連線原因。
4. 找一個有登入表單、且密碼庫裡已經存過對應網域憑證的網站測試自動填入；用擴充功能圖示的「選擇密碼」測試沒有已存憑證時挑一筆重複使用的流程。

**連不上時怎麼排查**：擴充功能 popup／content script 連不上 Native Messaging Host 時，`background.js` 會把 `chrome.runtime.lastError` 轉成 `{ type: 'error', message }` 回應——popup 會在畫面上顯示「連線失敗：...」而不是誤導成「密碼庫裡還沒有網站帳密」的空清單；content script 的自動偵測提示條本身是錦上添花、查不到憑證就安靜不出聲，但連線失敗時會在該分頁的 DevTools 主控台留一行 `[FileLocker] 查詢密碼庫失敗：...`，可以用這行判斷是「真的沒有比對到憑證」還是「Host 根本連不上」。常見成因：FileLocker 沒開（Host 會嘗試用 `--startup` 安靜背景啟動，但需要幾秒）、`plugins/PasswordLocker/` 裡的 Host exe／`extension-id.txt` 沒同步、或這個 `manifest.json` 的 `"key"` 欄位被改掉導致 ID 跟登錄機碼裡登記的不一致。

## 圖示

`icons/16.png`、`icons/48.png`、`icons/128.png` 已經從 `PasswordLocker_icon_2.png`（規劃文件第 10 節）產生並在 `manifest.json` 裡登記，開發階段跟正式上架用同一組。

## 簽署金鑰

`manifest.json` 的 `"key"` 欄位是這組開發用金鑰的公鑰（DER，base64），對應的私鑰放在 `d:\Github\FileLocker_專案\FileLocker_extension_signing_key.pem`——**刻意放在這個 git repo 之外**，因為這個 repo 是公開的，即使這把私鑰的實際危害有限（只影響擴充功能 ID 的計算跟本機打包 `.crx` 簽章，不是傳輸層或儲存加密用的金鑰），還是不用公開曝露。這把私鑰目前只用來讓開發階段的擴充功能 ID 固定；上架 Chrome 線上應用程式商店時，商店本身會另外指派/管理正式的正式 ID 與簽章，屆時視情況決定要不要沿用這把金鑰。

## 上架

Chrome 線上應用程式商店需要開發者帳號、一次性 5 美元註冊費、人工審核（時間不可控，見 `docs/adr/0002-password-locker-native-messaging-over-userscript-bridge.md`）——這些需要人工完成，不在這次自動化範圍內。
