---
name: release
description: 依照這個專案實際的流程準備一次新版本發布——建置、測試、更新雙語 Release Notes、commit、打 tag，並提示需要在這個 repo 之外手動完成的安裝程式打包步驟。
---

# Release（PasswordVault）

照抄自 FileLocker repo 的 `/release` skill，換成 PasswordVault 這邊的實際路徑與慣例。跟那邊一樣：

- Release Notes 是**單一檔案內雙語**（`docs/releases/vX.Y.Z.md`，先 `## 繁體中文` 後 `## English`），
  比照 `docs/releases/v0.1.0.md` 的格式延續。
- commit 訊息慣例：`feat:`／`fix:`／`docs:`／`refactor:` 開頭 + 完整中文句子說明「為什麼」，不是嚴格的
  Conventional Commits 格式。
- 版本號從 `0.1.0` 起算（見
  [`PasswordVault_獨立化_規劃.md`](https://github.com/lx-kvn/FileLocker/blob/main/docs/specs/features/PasswordVault_獨立化_規劃.md)
  第 13 節「版本號策略」的既有決策），不是 `1.0.0`。`v0.1.0` 已於 2026-09-04 發布。
- 打包透過 `mswi-cli pack --config installer/passwordvault_installer.json`，設定檔已經檢查進這個
  repo（`no_admin_install` 模式，見第 11 節）。
- `gh` CLI 已安裝並登入（`lx-kvn` 帳號）。**發布前一定要先把要執行的指令列給使用者確認過，取得明確
  同意才能真的執行**——跟 FileLocker 那邊同一條規則。執行前先用 `gh release list --repo lx-kvn/PasswordVault`
  確認這個版本還沒被手動發布過。

## 額外要注意：這個 repo 的發布同時牽動 FileLocker 那邊

PasswordVault 编譯出來的 `PasswordVault.Core.dll` 是 FileLocker.App 消費的可選配部件（見規劃文件
第 17 節「FileLocker 本體切換消費來源」）。FileLocker.App 那邊的切換**已經完成**——
`PasswordLockerModuleInstaller` 會去 `lx-kvn/PasswordVault` 的 `releases/latest` 找符合命名規則的
資產。因此除了 GUI 安裝程式之外，每次發布還要附上一份給 FileLocker 消費的 zip：

```
PasswordVault_v{這次版本}_for-FileLocker-{相容最小版本}-to-{相容最大版本}.zip
```

（見規劃文件「資產命名規則」小節）——相容區間需要對照 `vendor/README.md` 記錄的
`FileLocker.PluginContracts.dll` 最後一次更新時間點手動決定，不是自動算出來的。

**但這一步目前還沒有真的跑通過**：從來沒有產出過符合命名規則的 zip，所以整條「FileLocker 自動
下載並載入部件」的路徑只驗證到程式碼層級。在跑通之前，這個 skill 的步驟 8 只涵蓋 GUI 安裝程式。
真正要做這件事時，照
`docs/specs/features/PasswordVault_Release打包_待辦.md` 走，並在跑通之後把實際步驟（含踩到的坑）
補進這份 SKILL.md 的步驟 8——**寫進去的必須是實際做過、驗證過的那條路徑，不是想像中的流程**。

## 步驟

1. **確認工作目錄乾淨**：`git status --short`，如果有非預期的未追蹤/未提交檔案，先跟使用者確認要不要
   一併處理，不要悄悄略過。
2. **跑完整測試套件**：`dotnet test PasswordVault.slnx`。任何一個測試沒過就停下來，不要 continue——
   回報給使用者，不要自己決定要不要跳過。三個測試專案（`PasswordVault.Core.Tests`／
   `PasswordVault.App.Tests`／`PasswordVault.Cli.Tests`）都要過，抓「失敗:0」為準，不要死記總數。
3. **Release 組態建置**：`dotnet build PasswordVault.slnx -c Release`，確認 0 錯誤 0 警告。
4. **決定版本號**：讀 `git tag` 列出目前最新版本，跟使用者確認這次是 patch／minor／major，不要自己猜。
5. **產生 Release Notes 草稿**：`git log <上一個tag>..HEAD --oneline` 整理這次的變更，依照
   `docs/releases/v0.1.0.md` 的雙語段落結構寫成 `docs/releases/vX.Y.Z.md`（亮點／已知限制，中文在前、
   英文在後）。同時檢查 README.md「已完成」／「尚未完成」段落需不需要跟著更新。
6. **Commit**：訊息比照這個 repo 現有風格，文件變更可以跟程式碼變更分開兩個 commit。
7. **打 tag**：跟使用者確認要不要打這個 tag、要不要 push——兩個都要先問，不要自動打／自動推。
8. **打包安裝程式**：這一步不用跟使用者確認，執行 release skill 時直接自動打包。開始之前先 Read
   `d:\Github\mac-style-windows-installer_專案\mac-style-windows-installer\CLI_USAGE.md`——mswi-cli
   用法隨時可能改版（FileLocker repo 這輪發版就實際踩過欄位格式改變的坑），不要憑記憶假設。

   `dotnet publish src/PasswordVault.App/PasswordVault.App.csproj -c Release` 先確保 publish 輸出是
   最新的，再跑：
   ```
   mswi-cli pack --config installer/passwordvault_installer.json --version X.Y.Z --exe-name PasswordVault_vX.Y.Z_setup
   ```
   `installer/passwordvault_installer.json` 裡的 `app_dir`／`png_icon`／`ico_icon` 等路徑欄位，在不同
   機器上執行前要先確認仍然正確（跟 FileLocker 那邊同樣的絕對路徑限制）。
   `no_admin_install` 模式打包出來的安裝檔裝到 `%LOCALAPPDATA%\Programs\PasswordVault\`，不需要系統
   管理員權限。
9. **建立 GitHub Release**：先 `gh release list --repo lx-kvn/PasswordVault` 確認這個版本還沒發布過。
   沒發布過的話，把要跑的指令（大致是 `gh release create vX.Y.Z <安裝檔路徑> --title "PasswordVault vX.Y.Z" --notes-file docs/releases/vX.Y.Z.md`）
   列出來給使用者看過、明確同意後才執行——不能自己直接發布。

## 不做的事

- 不自動打 tag、不自動 push——一律先問。
- 不用 `gh release create` 未經確認就直接發布——一律先列出指令給使用者看過同意。
- 不把 Release Notes 拆成分開的中英文檔案。
- 不打包給 FileLocker 消費的相容性 zip——那條流程還沒跑通過，見上方「額外要注意」段落。跑通並把
  步驟補進這份 SKILL.md 之後，這一條要刪掉。

需要確認的只有這三件事：打 tag、push、建立 GitHub Release。打包安裝程式不用問，直接執行。
