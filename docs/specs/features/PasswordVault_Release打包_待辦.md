# PasswordVault：給 FileLocker 消費的 Release 打包

**狀態：尚未動工。** 這份文件記錄的是動工前已經確定的要件、必須先釐清的疑問，以及驗證方式；
不是實作步驟。跑通之後，實際步驟（含踩到的坑）要補進 `.claude/skills/release/SKILL.md` 的步驟 8，
這份文件則改為記錄定案理由。

## 目錄

- [1. 這件事是什麼、為什麼卡著](#1-這件事是什麼為什麼卡著)
- [2. 已確定的要件](#2-已確定的要件)
  - [2.1 檔名規則](#21-檔名規則)
  - [2.2 zip 內容必須是攤平的](#22-zip-內容必須是攤平的)
  - [2.3 要放哪些檔案](#23-要放哪些檔案)
  - [2.4 相容區間由人工決定](#24-相容區間由人工決定)
- [3. 動工前必須先釐清：deps.json 到底需不需要](#3-動工前必須先釐清depsjson-到底需不需要)
- [4. 怎麼驗證到底](#4-怎麼驗證到底)
- [5. 完成後要一併更新的文件](#5-完成後要一併更新的文件)
- [已知限制](#已知限制)
- [待辦事項](#待辦事項)

---

## 1. 這件事是什麼、為什麼卡著

FileLocker 的密碼庫是可選配部件：使用者在 App 裡點「安裝密碼庫」時，`FileLocker.App` 的
`PasswordLockerModuleInstaller` 會去
`https://api.github.com/repos/lx-kvn/PasswordVault/releases/latest` 找一個符合命名規則的 zip，
下載後解壓縮到自己的 `plugins/PasswordLocker/`。

兩邊的程式碼都已完成，但**從來沒有真的產出過那個 zip**，因此這條路徑只驗證到程式碼層級。
2026-09-04 在虛擬機上驗證瀏覽器整合時實際被這件事擋到：正式安裝的 FileLocker 拿不到部件，
只能照 `CLAUDE.md` 記載的手動步驟把檔案組進 `plugins/PasswordLocker/` 才驗得下去。

做完這件事會同時解掉兩邊的待辦：這個 repo README「尚未完成」的第一條，以及 FileLocker 技術規格
文件第 24.1 節「PasswordVault 消費端切換的實機驗證」。

## 2. 已確定的要件

### 2.1 檔名規則

```
PasswordVault_v{PasswordVault 版本}_for-FileLocker-{相容最小版本}-to-{相容最大版本}.zip
```

例：`PasswordVault_v0.1.1_for-FileLocker-1.3.0-to-2.1.1.zip`

實際解析它的是 FileLocker repo 的 `src/FileLocker.Core/UpdateCheck/PasswordLockerAssetSelector.cs`：

```
^PasswordVault_v(?<pv>\d+\.\d+\.\d+)_for-FileLocker-(?<min>\d+\.\d+\.\d+)-to-(?<max>\d+\.\d+\.\d+)\.zip$
```

三組都必須是三段式版本號，多一段少一段都不會被認得。同一個 Release 裡有多筆符合時，挑
PasswordVault 版本最新的那一筆；目前執行中的 FileLocker 版本不落在區間內的一律略過。

命名規則的設計理由（為何用區間而不是「最低版本」、為何插入 `for-FileLocker-`／`-to-` 當視覺分隔）
見 FileLocker repo `PasswordVault_獨立化_規劃.md` 的「資產命名規則」小節。

### 2.2 zip 內容必須是攤平的

`PasswordLockerModuleInstaller` 的作法是
`ZipFile.ExtractToDirectory(zip, plugins/PasswordLocker.pending/)` 之後直接改名成
`plugins/PasswordLocker/`。因此 **zip 的根目錄就是部件資料夾的內容**，多包一層
`PasswordVault/` 進去會讓載入器找不到任何東西。

### 2.3 要放哪些檔案

載入器 `PasswordLockerPluginLoader` 找的進入點是 `PasswordVault.Core.dll`。

| 檔案 | 從哪裡拿 | 為什麼 |
|---|---|---|
| `PasswordVault.Core.dll` | `src/PasswordVault.App/bin/Release/<TFM>/` | 部件本體 |
| `Konscious.Security.Cryptography.Argon2.dll` | 同上 | 密碼雜湊。`PasswordVault.Core` 自己的 `bin/` **不會有**——Library 專案的輸出不含攤平的相依集合，只有可執行專案才有 |
| `Konscious.Security.Cryptography.Blake2.dll` | 同上 | 同上 |
| `PasswordVault.NativeHost.exe`／`.dll`／`.deps.json`／`.runtimeconfig.json` | 同上 | 瀏覽器轉接程式。四個檔案缺任何一個都會啟動失敗 |
| `extension-id.txt` | 同上 | 缺少時 `PasswordLockerNativeHostRegistrar` 會安靜跳過註冊，瀏覽器整合等於沒裝 |
| `PasswordVault.Core.deps.json` | `src/PasswordVault.Core/bin/Release/<TFM>/` | **見第 3 節，需不需要尚未確認** |

`FileLocker.PluginContracts.dll` **不放進去**：`PasswordLockerLoadContext.Load` 對這個組件名直接
回傳 null，強制改用宿主行程那一份。兩份同名介面在 CLR 眼中是不同型別，轉型會失敗，所以這個
組件不能由部件自己帶（理由見 `vendor/README.md`）。放進去不會壞，但會讓後續維護者誤以為它有作用。

### 2.4 相容區間由人工決定

不自動推算。要對照 `vendor/README.md` 記錄的 `FileLocker.PluginContracts.dll` 最後一次更新時間點，
判斷這個部件版本相容哪些 FileLocker 版本。上限要不要填到當時最新的 FileLocker 版本，是每次發布時
需要跟使用者確認的決定——只標最低版本無法表達「太新也不相容」的情況。

## 3. 動工前必須先釐清：deps.json 到底需不需要

**這是決定 zip 內容的關鍵，目前是未知的，而且 2026-09-04 那輪虛擬機驗證沒有涵蓋到。**

載入器用的是 `new AssemblyDependencyResolver(pluginDllPath)`，這個機制是讀 `<組件名>.deps.json`
來解析相依組件的實際路徑。照這個機制推論，少了 `PasswordVault.Core.deps.json`，兩個 Konscious
組件就解析不到。

但那輪實測手動組出來的部件資料夾**沒有**放 deps.json，`FileLocker.App` 卻成功載入部件並回應了
憑證查詢。兩者對不起來，最可能的解釋是：**該次查詢的密碼庫是空的、也未解鎖，整條路徑沒有觸及
Argon2 金鑰衍生**，因此 Konscious 從頭到尾沒被載入，等於沒驗到。

**先做這個驗證再決定 zip 內容**：

1. 組一個不含 `PasswordVault.Core.deps.json` 的部件資料夾。
2. 執行一個**真的會用到密碼雜湊**的操作——建立密碼庫主密碼，或以主密碼解鎖。
3. 觀察是否拋出組件載入失敗。`PasswordLockerPluginLoader` 的 catch 會把細節寫進主控台，前端只會
   看到「壞了」這個狀態，因此**必須去看主控台輸出**，不能只看畫面。
4. 依結果決定 zip 要不要含 deps.json，並把結論與理由寫回這份文件。

## 4. 怎麼驗證到底

`PasswordLockerModuleInstaller` 打的是 `releases/latest`，而 GitHub 的 `latest` **不包含草稿與
預發行版本**，所以嚴格的端對端驗證需要一個真的已發布的 Release。這件事要先跟使用者確認採哪一種，
不要自己決定：

- **選項 A（完整）**：發一個新版本，把 GUI 安裝檔與這個相容性 zip 一起掛上去，然後在虛擬機裡用
  正式安裝的 FileLocker 走一次「安裝密碼庫」。最真實，但會在 Release 頁面留下紀錄。
- **選項 B（不動 Release）**：只驗「資產挑選邏輯 → 解壓縮 → 載入 → 觸及密碼雜湊的操作」，把 zip
  手動放進虛擬機模擬下載完成的狀態。不會動到 Release 頁面，但沒驗到下載那一段。

虛擬機環境的用法見 FileLocker repo 的 `.claude/skills/run-test-vm/SKILL.md`；**動虛擬機之前先走
`use-vm-lease`**（同一批機器有別的工作階段在用，還原快照會無聲抹掉對方的工作）。

已知的環境限制：**兩台虛擬機目前都沒有網路，也沒有預先安裝 .NET 執行環境**，安裝時需要的東西都得
自己從主機端送進去。選項 A 需要客體能連上 GitHub，以目前的環境做不到——採選項 A 的話要先跟使用者
確認能不能把虛擬機的網路接上。

## 5. 完成後要一併更新的文件

- `.claude/skills/release/SKILL.md` 步驟 8：補上實際的打包步驟。**寫進去的必須是實際做過、驗證過
  的那條路徑，包含踩到的坑**，不是想像中的流程。同時刪掉「不做的事」裡「不打包給 FileLocker 消費的
  相容性 zip」那一條。
- `README.md`「尚未完成」：移除 Release 打包那一條。
- FileLocker repo 的技術規格文件第 24.1 節：把「PasswordVault 消費端切換的實機驗證」移到已完成。
- FileLocker repo 的 `PasswordVault_獨立化_規劃.md`：第 8.2 節末段記錄了 deps.json 這個未解問題，
  結論出來後要回頭更新。
- 這份文件：改為記錄定案理由，不再是待辦。

## 已知限制

- 相容區間永遠是人工判斷，沒有自動化的檢查會在填錯時攔下來。填錯的後果是使用者的 FileLocker 抓不到
  部件（區間過窄）或抓到不相容的版本（區間過寬），兩者都不會有明確的錯誤訊息指向命名。
- `releases/latest` 的語意決定了「預發行版本無法用來測這條路徑」，這是 GitHub 的行為，不是這邊能
  調整的設計。

## 待辦事項

- 第 3 節的 deps.json 驗證。
- 選項 A／B 的取捨，以及選項 A 所需的虛擬機網路。
- 首次實際打包並跑通端對端流程。
