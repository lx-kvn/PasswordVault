# PasswordVault 瀏覽器擴充功能

跟 Native Messaging Host（純轉接層，轉發到 FileLocker.App 或 PasswordVault.exe 任一邊持有的
Named Pipe——見 PasswordVault_獨立化_規劃.md 第 8 節「共存」）、實際驗證/加解密的宿主程式搭配
使用。這個目錄本身不是一個 npm/build 專案——純 JS + 靜態 `manifest.json`，不需要任何建置步驟。

**目前狀態**：這份文件是從 FileLocker repo 遷移過來時原樣保留技術細節、只換掉品牌文字的版本，
以下步驟裡提到的 Native Messaging Host 註冊流程（`PasswordLockerNativeHostRegistrar`、
`plugins/PasswordLocker/` 路徑）目前仍然只存在於 FileLocker.App 那一側——**PasswordVault.exe
自己的 Native Messaging Host 相對應實作還沒開始寫**（見 PasswordVault_獨立化_規劃.md 第 8 節，
這是明確排除在這次 `git filter-repo` 遷移範圍之外、需要另外從零開始的一塊）。在那塊完成之前，
只裝 PasswordVault.exe、不裝 FileLocker.App 的使用者，這個擴充功能實際上連不上任何東西。

## 本機測試（開發人員模式，尚未上架 Chrome 線上應用程式商店）

`manifest.json` 已經固定了 `"key"` 欄位（開發用的簽署金鑰，私鑰放在這個 repo 之外，見下方「簽署
金鑰」一節），所以這個擴充功能不管用哪個路徑載入、重新載入幾次，Chrome 指派的 ID 永遠是同一個
（`ihhcdgkacinknnbaibnjpaamfhbebpdj`）——這把金鑰刻意沿用 FileLocker.Extension 時期的舊值沒有換掉，
因為 FileLocker.App 既有安裝的 Native Messaging Host 註冊已經 allowlist 這個 ID，換一把新金鑰會讓
所有現有使用者的瀏覽器整合直接失效。

1. Chrome 網址列輸入 `chrome://extensions`，右上角開啟「開發人員模式」。
2. 「載入未封裝項目」，選這個資料夾（`src/PasswordVault.Extension`）。
3. 重新啟動 FileLocker（或觸發一次密碼庫部件重新載入），確認
   `%LocalAppData%\FileLocker\NativeMessagingHost\com.filelocker.passwordlocker.json` 有被建立/
   更新，且 `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.filelocker.passwordlocker`
   指向這個檔案。
4. 找一個有登入表單、且密碼庫裡已經存過對應網域憑證的網站測試自動填入；用擴充功能圖示的「選擇密碼」
   測試沒有已存憑證時挑一筆重複使用的流程。

**連不上時怎麼排查**：擴充功能 popup／content script 連不上 Native Messaging Host 時，
`background.js` 會把 `chrome.runtime.lastError` 轉成 `{ type: 'error', message }` 回應——popup
會在畫面上顯示「連線失敗：...」而不是誤導成「密碼庫裡還沒有網站帳密」的空清單；content script
的自動偵測提示條本身是錦上添花，查不到憑證就安靜不出聲，但連線失敗時會在該分頁的 DevTools 主控台
留一行 console.warn，可以用這行判斷是「真的沒有比對到憑證」還是「Host 根本連不上」。常見成因：
兩邊宿主程式都沒開、Host exe／`extension-id.txt` 沒同步、或這個 `manifest.json` 的 `"key"` 欄位
被改掉導致 ID 跟登錄機碼裡登記的不一致。

## 圖示

`icons/16.png`、`icons/48.png`、`icons/128.png` 開發階段跟正式上架用同一組。

## 簽署金鑰

`manifest.json` 的 `"key"` 欄位是這組開發用金鑰的公鑰（DER，base64），對應的私鑰放在
`d:\Github\FileLocker_專案\FileLocker_extension_signing_key.pem`——**刻意放在這個 git repo
之外**，因為這個 repo 是公開的，即使這把私鑰的實際危害有限（只影響擴充功能 ID 的計算跟本機打包
`.crx` 簽章，不是傳輸層或儲存加密用的金鑰），還是不用公開曝露。這把私鑰目前只用來讓開發階段的
擴充功能 ID 固定；上架 Chrome 線上應用程式商店時，商店本身會另外指派/管理正式的正式 ID 與簽章，
屆時視情況決定要不要沿用這把金鑰。

## 上架

Chrome 線上應用程式商店需要開發者帳號、一次性 5 美元註冊費、人工審核（時間不可控）——這些需要
人工完成，不在自動化範圍內。
