# PasswordVault：給 FileLocker 消費的 Release 打包

**狀態：已跑通（2026-09-05）。** 這份文件記錄定案的內容與理由；實際操作步驟寫在
`.claude/skills/release/SKILL.md` 步驟 9，不在這裡重複。

## 目錄

- [1. 這件事是什麼](#1-這件事是什麼)
- [2. 定案的要件](#2-定案的要件)
  - [2.1 檔名規則](#21-檔名規則)
  - [2.2 zip 內容必須是攤平的](#22-zip-內容必須是攤平的)
  - [2.3 放哪些檔案](#23-放哪些檔案)
  - [2.4 相容區間由人工決定](#24-相容區間由人工決定)
- [3. deps.json 不需要：驗證過程與結論](#3-depsjson-不需要驗證過程與結論)
- [4. 端對端驗證怎麼做的](#4-端對端驗證怎麼做的)
- [5. 迴歸防護](#5-迴歸防護)
- [已知限制](#已知限制)

---

## 1. 這件事是什麼

FileLocker 的密碼庫是可選配部件：使用者在 App 裡點「安裝密碼庫」時，`FileLocker.App` 的
`PasswordLockerModuleInstaller` 會去
`https://api.github.com/repos/lx-kvn/PasswordVault/releases/latest` 找一個符合命名規則的 zip，
下載後解壓縮到自己的 `plugins/PasswordLocker/`。

在 2026-09-05 之前從來沒有真的產出過那個 zip，因此這條路徑只驗證到程式碼層級。2026-09-04 在
虛擬機上驗證瀏覽器整合時實際被這件事擋到：正式安裝的 FileLocker 拿不到部件，只能手動把檔案組進
`plugins/PasswordLocker/` 才驗得下去。

現在 `PasswordVault_v0.1.0_for-FileLocker-2.1.0-to-2.1.1.zip` 已掛在 v0.1.0 Release 上，這條路徑
也已用 FileLocker 自己的程式碼實測過（見第 4 節）。

## 2. 定案的要件

### 2.1 檔名規則

```
PasswordVault_v{PasswordVault 版本}_for-FileLocker-{相容最小版本}-to-{相容最大版本}.zip
```

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

### 2.3 放哪些檔案

八個檔案，全部從 **`src/PasswordVault.App` 的建置輸出**抓：

| 檔案 | 為什麼 |
|---|---|
| `PasswordVault.Core.dll` | 部件本體，載入器 `PasswordLockerPluginLoader` 找的進入點 |
| `Konscious.Security.Cryptography.Argon2.dll` | 密碼雜湊 |
| `Konscious.Security.Cryptography.Blake2.dll` | 同上 |
| `PasswordVault.NativeHost.exe`／`.dll`／`.deps.json`／`.runtimeconfig.json` | 瀏覽器轉接程式，四個檔案缺任何一個都會啟動失敗 |
| `extension-id.txt` | 缺少時 `PasswordLockerNativeHostRegistrar` 會安靜跳過註冊，瀏覽器整合等於沒裝 |

**來源不能改成 `src/PasswordVault.Core` 的建置輸出**：Library 專案的輸出不含攤平的相依集合，
那裡沒有 Konscious 兩個檔，只有可執行專案才有。

**不放 `PasswordVault.Core.deps.json`**，理由見第 3 節。

**不放 `FileLocker.PluginContracts.dll`**：`PasswordLockerLoadContext.Load` 對這個組件名直接回傳
null，強制改用宿主行程那一份。兩份同名介面在 CLR 眼中是不同型別，轉型會失敗，所以這個組件不能
由部件自己帶（理由見 `vendor/README.md`）。放進去不會壞，但會讓後續維護者誤以為它有作用。

### 2.4 相容區間由人工決定

不自動推算，每次發布都要跟使用者確認。

- **下限**目前是 `2.1.0`：這是第一個會去 `lx-kvn/PasswordVault` 找、而且認得現行命名規則的
  FileLocker 版本。`2.0.0` 雖然已經有部件載入機制，但它比對的還是舊的 `PasswordLocker_v...`
  命名，填進區間也永遠抓不到，只會誤導。
- **上限**要對照 `vendor/README.md` 記錄的 `FileLocker.PluginContracts.dll` 最後一次更新時間點
  判斷。v0.1.0 這次填的是當時最新的正式版 `2.1.1`——只標下限無法表達「太新也不相容」的情況。

## 3. deps.json 不需要：驗證過程與結論

**結論：`PasswordVault.Core.deps.json` 不放進 zip，而且放了也起不了作用。**

原本的疑慮是：載入器用的是 `new AssemblyDependencyResolver(pluginDllPath)`，這個機制會讀
`<組件名>.deps.json` 來解析相依組件的實際路徑，少了它 Konscious 兩個組件可能就解析不到。
2026-09-04 那輪虛擬機驗證看起來證明了「不用放」，但那個結論不可信——當時的密碼庫是空的也沒解鎖，
整條路徑根本沒碰到 Argon2，那兩個組件從頭到尾沒被載入過，等於沒驗到。

驗法是把載入器的邏輯原樣複製出來、跑一個**真的會用到密碼雜湊**的操作（建立主密碼），比對四種
組合。結果：

- 部件資料夾裡有 Konscious 兩個檔、**沒有** deps.json → 成功，而且解析器回報的路徑就在**部件
  資料夾本身**。沒有 deps.json 時它會直接掃元件所在的資料夾，這正是我們要的行為。
- 把 Konscious 兩個檔拿掉 → 解析器回報找不到（`ResolveAssemblyToPath` 回傳 null），證明上面那個
  結果不是假陽性。
- 放進建置產出的那份 deps.json → 沒有任何差別。它的內容指向 NuGet 快取裡的 `lib/net8.0/...`
  路徑，使用者機器上不存在那個目錄，所以它既沒幫上忙也沒幫倒忙。

**另一個一併發現、比 deps.json 更值得記的事**：FileLocker 本體自己也帶著同名的 Konscious 組件
（本體的加密也用同一套）。所以萬一打包時漏掉那兩個檔，載入器解析不到時會安靜退回宿主那一份，
當下不會有任何錯誤訊息，直到哪天兩邊版本不一致才出事。**因此「裝起來能用」不足以證明沒漏檔**，
必須驗到「相依組件確實是從部件資料夾解析出來的」——第 5 節的測試就是照這個寫的。

## 4. 端對端驗證怎麼做的

`PasswordLockerModuleInstaller` 打的是 `releases/latest`，而 GitHub 的 `latest` 不包含草稿與
預發行版本，所以嚴格的端對端驗證需要一個真的已發布的 Release。實際採取的作法是**把 zip 掛到
既有的 v0.1.0 Release 上**（不發新版本、不動安裝檔），然後：

1. 用 FileLocker 自己的 `PasswordLockerModuleInstaller.FindCompatibleReleaseAsync`（內含
   `PasswordLockerAssetSelector`）打真實的 GitHub API 挑資產；
2. `DownloadAndStageAsync` 下載並解壓到 `plugins/PasswordLocker.pending/`；
3. `SwapPendingInstallIfPresent` 換成生效目錄；
4. `PasswordLockerPluginLoader.Load` 載入，狀態回傳 `Ok`；
5. 送一次 `setupPasswordLockerCredential` 建立主密碼——這步會真的跑 Argon2id 金鑰衍生，成功。

驗證用的檔案跑完就刪掉了，沒有留在 FileLocker repo 裡——它會打真實的網路請求，不適合放進測試
套件。要重跑的話照上面五個步驟重寫一份即可。

**不用虛擬機**：虛擬機驗的是「用安裝程式裝出來的正式版」，而這條路徑跟安裝方式無關，兩台虛擬機
目前又都沒有網路也沒有 .NET 執行環境，成本遠高於它多驗到的東西。

## 5. 迴歸防護

`tests/PasswordVault.App.Tests/PasswordLockerModulePackagingTests.cs` 固定住四件事：八個檔案的
清單、相依組件不在 Core 的建置輸出裡、不含 deps.json 也能完成 Argon2 金鑰衍生（且相依組件確實
解析自部件資料夾）、少了 Konscious 時解析器就找不到。

同一組測試認得 `PASSWORDVAULT_PUBLISHED_MODULE_DIR` 這個環境變數：設了就改用那個資料夾當來源。
CI（`.github/workflows/ci.yml`）的「驗證已發布的部件 zip」工作會去抓 `releases/latest` 的資產、
解壓、把路徑設進這個變數再跑同一組測試，所以「線上那包真的能用」這件事是自動守著的。刻意不為
CI 另寫一套驗證邏輯——本機建置輸出跟線上那包要滿足的條件完全一樣，寫成兩份遲早會分歧。

## 已知限制

- 相容區間永遠是人工判斷，沒有自動化的檢查會在填錯時攔下來。填錯的後果是使用者的 FileLocker
  抓不到部件（區間過窄）或抓到不相容的版本（區間過寬），兩者都不會有明確的錯誤訊息指向命名。
- `releases/latest` 的語意決定了「預發行版本無法用來測這條路徑」，這是 GitHub 的行為，不是這邊
  能調整的設計。
- CI 那個下載驗證的工作依賴外部服務（GitHub API 與資產下載），網路或服務異常時會紅，但紅的原因
  跟程式碼無關。看到它紅時先確認 `releases/latest` 的資產還在。
