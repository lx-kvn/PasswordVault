# PasswordVault

獨立的密碼管理應用程式，不依賴其他軟體單獨運作。核心邏輯（`PasswordVault.Core`）與桌面應用程式（`PasswordVault.exe`，含內建 CLI）皆遷出自 [FileLocker](https://github.com/lx-kvn/FileLocker) 的密碼庫（Password Locker）功能。

**目前狀態：程式碼遷移、品牌改名、測試補齊、FileLocker 消費端切換（含從真實 Release 下載安裝的實機驗證）、瀏覽器整合的登錄機制、Release 打包流程皆已完成；桌面宿主本身仍在骨架階段（見下方「尚未完成」）。** 架構決策與理由記錄在 FileLocker repo 的以下文件：

- [`PasswordVault_獨立化_規劃.md`](https://github.com/lx-kvn/FileLocker/blob/main/docs/specs/features/PasswordVault_獨立化_規劃.md)——完整規劃文件
- [ADR-0003](https://github.com/lx-kvn/FileLocker/blob/main/docs/adr/0003-passwordvault-separate-repo.md)——拆分成獨立 repo 的決策紀錄

## 已完成

- `src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/`、對應測試專案的原始碼與 commit 歷史，已用 `git filter-repo` 從 FileLocker repo 遷移過來。
- 品牌改名：`FileLocker.PasswordLocker` → `PasswordVault.Core`，瀏覽器擴充功能改名為 `PasswordVault.Extension`，圖示、使用者可見文字皆已更新。
- 六個專案骨架與基本功能：`PasswordVault.Core`／`PasswordVault.App`（WPF 宿主，單一執行個體、系統匣、瀏覽器整合 Pipe Server）／`PasswordVault.Cli`（`--list`／`--get`）／`PasswordVault.Extension`／`PasswordVault.NativeHost`／`PasswordVault.Web`（含共用套件 `packages/password-locker-ui`，`PasswordVault.exe` 已接上 WebView2 顯示真實畫面）。
- 舊版使用者資料自動遷移（複製、不刪舊檔，新舊路徑都有資料時新路徑優先），且已改成跟 FileLocker.App 共用同一份密碼庫資料（`%LocalAppData%\PasswordVault\PasswordLocker\`）。
- 瀏覽器擴充功能的 Native Messaging Host 轉接程式改成跟 FileLocker.App 共用同一個實體檔案（`%LocalAppData%\PasswordVault\NativeHost\`），解決兩邊各自帶一份副本時登錄檔／Named Pipe 兩套贏家判斷邏輯不一致導致的「Pipe is broken」問題。
- 測試覆蓋補齊：`PasswordVault.App.Tests`（8 個，移植自 FileLocker 的 Pipe Server 資安回歸測試）、`PasswordVault.Cli.Tests`（5 個）。
- FileLocker 本體消費端切換：`PasswordLockerModuleInstaller`／`PasswordLockerAssetSelector`／`PasswordLockerPluginLoader`／`PasswordLockerNativeHostRegistrar` 皆已改指向本 repo 的 Release 與新命名規則，並用 FileLocker 自己的這幾支程式碼打真實的 GitHub API 實測過整條「挑資產→下載→解壓→載入→建立主密碼」路徑。
- 給 FileLocker 消費的相容性 zip 打包流程：`PasswordVault_v0.1.0_for-FileLocker-2.1.0-to-2.1.1.zip` 已掛在 v0.1.0 Release 上，步驟寫在 `.claude/skills/release/SKILL.md` 步驟 9，定案理由見 `docs/specs/features/PasswordVault_Release打包.md`。
- CI（GitHub Actions）：push／PR 會建置並跑完整測試套件，另有一個工作真的去抓 `releases/latest` 的部件 zip、解壓、載入並建立主密碼，守住「線上那包真的能用」。
- 安裝程式打包：`installer/passwordvault_installer.json`，`no_admin_install` 模式、雙語 EULA，已實測打包成功。
- MIT License。

## 尚未完成

- **桌面宿主仍是骨架階段**（見 `PasswordVault_獨立化_規劃.md` 第 14 節），以下幾塊尚未接上：
  - 瀏覽器擴充功能觸發、但密碼庫尚未通過驗證時的**驗證彈窗**（比照 FileLocker.App 的 `PasswordLockerBrowserVerifyWindow`）——目前固定回傳「無法驗證」，因此擴充功能在密碼庫未解鎖的狀態下實際上用不了。
  - **「已加密檔案」憑證的關聯顯示**：需要偵測 FileLocker 主體是否也裝在同一台機器上（見規劃文件第 6 節），該偵測邏輯尚未實作，目前固定視為不存在。不影響一般網站帳密憑證的任何功能。
  - **系統匣右鍵選單**僅有最基本的項目。

## 建置與測試

全新 clone 之後要先備妥前端：共用元件套件 `@lx-kvn/password-locker-ui` 編出來的 `dist/` 沒有進版控，而 `PasswordVault.Web` 是靠 npm workspace 連到本地那份，少了它 Release 建置會停在「解析不到 @lx-kvn/password-locker-ui」。

```bash
npm ci
npm run build --workspace @lx-kvn/password-locker-ui
dotnet test PasswordVault.slnx
```

## 授權

[MIT License](LICENSE)
