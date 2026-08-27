# PasswordVault

獨立的密碼管理應用程式，不依賴其他軟體單獨運作。核心邏輯（`PasswordVault.Core`）與桌面應用程式（`PasswordVault.exe`，含內建 CLI）皆遷出自 [FileLocker](https://github.com/lx-kvn/FileLocker) 的密碼庫（Password Locker）功能。

**目前狀態：程式碼遷移、品牌改名、測試補齊、FileLocker 消費端切換（程式碼層級）皆已完成；尚缺 PasswordVault 自己的 Release 打包與瀏覽器整合的登錄機制。** 架構決策與理由記錄在 FileLocker repo 的以下文件：

- [`PasswordVault_獨立化_規劃.md`](https://github.com/lx-kvn/FileLocker/blob/main/docs/specs/features/PasswordVault_獨立化_規劃.md)——完整規劃文件
- [ADR-0003](https://github.com/lx-kvn/FileLocker/blob/main/docs/adr/0003-passwordvault-separate-repo.md)——拆分成獨立 repo 的決策紀錄

## 已完成

- `src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/`、對應測試專案的原始碼與 commit 歷史，已用 `git filter-repo` 從 FileLocker repo 遷移過來。
- 品牌改名：`FileLocker.PasswordLocker` → `PasswordVault.Core`，瀏覽器擴充功能改名為 `PasswordVault.Extension`，圖示、使用者可見文字皆已更新。
- 六個專案骨架與基本功能：`PasswordVault.Core`／`PasswordVault.App`（WPF 宿主，單一執行個體、系統匣、瀏覽器整合 Pipe Server）／`PasswordVault.Cli`（`--list`／`--get`）／`PasswordVault.Extension`／`PasswordVault.NativeHost`／`PasswordVault.Web`（含共用套件 `packages/password-locker-ui`，`PasswordVault.exe` 已接上 WebView2 顯示真實畫面）。
- 舊版使用者資料自動遷移（複製、不刪舊檔，新舊路徑都有資料時新路徑優先），且已改成跟 FileLocker.App 共用同一份密碼庫資料（`%LocalAppData%\PasswordVault\PasswordLocker\`）。
- 瀏覽器擴充功能的 Native Messaging Host 轉接程式改成跟 FileLocker.App 共用同一個實體檔案（`%LocalAppData%\PasswordVault\NativeHost\`），解決兩邊各自帶一份副本時登錄檔／Named Pipe 兩套贏家判斷邏輯不一致導致的「Pipe is broken」問題。
- 測試覆蓋補齊：`PasswordVault.App.Tests`（8 個，移植自 FileLocker 的 Pipe Server 資安回歸測試）、`PasswordVault.Cli.Tests`（5 個）。
- FileLocker 本體消費端切換（程式碼層級）：`PasswordLockerModuleInstaller`／`PasswordLockerAssetSelector`／`PasswordLockerPluginLoader`／`PasswordLockerNativeHostRegistrar` 皆已改指向本 repo 的 Release 與新命名規則。
- 安裝程式打包：`installer/passwordvault_installer.json`，`no_admin_install` 模式、雙語 EULA，已實測打包成功。
- MIT License。

## 尚未完成

- **PasswordVault 自己的 Native Messaging Host 登錄機制還沒實作**：目前只有 `FileLocker.App` 那一側會寫登錄檔／manifest，只裝 `PasswordVault.exe`、不裝 `FileLocker.App` 的使用者，瀏覽器擴充功能實際上連不上任何東西（見 `PasswordVault_獨立化_規劃.md` 第 8.1 節「待辦事項」）。
- **PasswordVault 的 Release 打包流程還沒能真正產出符合資產命名規則的 zip**：FileLocker.App 從 `lx-kvn/PasswordVault` Release 自動下載、切換部件生效這條路徑，目前只驗證到程式碼層級，還沒有機會人工實測。

## 建置與測試

```bash
dotnet test PasswordVault.slnx
```

## 授權

[MIT License](LICENSE)
