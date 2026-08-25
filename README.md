# PasswordVault

獨立的密碼管理應用程式，不依賴其他軟體單獨運作。核心邏輯（`PasswordVault.Core`）與桌面應用程式（`PasswordVault.exe`，含內建 CLI）皆遷出自 [FileLocker](https://github.com/lx-kvn/FileLocker) 的密碼庫（Password Locker）功能。

**目前狀態：程式碼遷移與品牌改名已完成，可獨立建置執行；FileLocker 本體尚未切換成消費本專案編譯產出的部件。** 架構決策與理由記錄在 FileLocker repo 的以下文件：

- [`PasswordVault_獨立化_規劃.md`](https://github.com/lx-kvn/FileLocker/blob/main/docs/specs/features/PasswordVault_獨立化_規劃.md)——完整規劃文件
- [ADR-0003](https://github.com/lx-kvn/FileLocker/blob/main/docs/adr/0003-passwordvault-separate-repo.md)——拆分成獨立 repo 的決策紀錄

## 已完成

- `src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/`、對應測試專案的原始碼與 commit 歷史，已用 `git filter-repo` 從 FileLocker repo 遷移過來。
- 品牌改名：`FileLocker.PasswordLocker` → `PasswordVault.Core`，瀏覽器擴充功能改名為 `PasswordVault.Extension`，圖示、使用者可見文字皆已更新。
- 六個專案骨架與基本功能：`PasswordVault.Core`／`PasswordVault.App`（WPF 宿主，單一執行個體、系統匣、瀏覽器整合 Pipe Server）／`PasswordVault.Cli`（`--list`／`--get`）／`PasswordVault.Extension`／`PasswordVault.NativeHost`／`PasswordVault.Web`（含共用套件 `packages/password-locker-ui`，`PasswordVault.exe` 已接上 WebView2 顯示真實畫面）。
- 舊版使用者資料自動遷移（複製、不刪舊檔，新舊路徑都有資料時新路徑優先）。
- 安裝程式打包：`installer/passwordvault_installer.json`，`no_admin_install` 模式、雙語 EULA，已實測打包成功。
- MIT License。

## 尚未完成

- **測試覆蓋不完整**：目前只有 `tests/PasswordVault.Core.Tests`，`PasswordVault.App`／`PasswordVault.Cli` 兩層邏輯還沒有對應的測試專案。
- **FileLocker 本體尚未切換消費來源**：FileLocker.App 的密碼庫部件安裝流程（`PasswordLockerModuleInstaller`）目前仍向自己（`lx-kvn/FileLocker`）的 GitHub Release 尋找 `FileLocker.PasswordLocker.dll`，還沒改成向本 repo 的 Release 尋找 `PasswordVault.Core.dll`——這一步完成前，FileLocker 使用者實際上還是裝到舊版部件，跟這個 repo 目前的開發進度脫節。

## 建置與測試

```bash
dotnet test PasswordVault.slnx
```

## 授權

[MIT License](LICENSE)
