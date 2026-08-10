# CLAUDE.md

密碼庫功能從 FileLocker repo 獨立出來的產品。架構決策與理由記錄在 FileLocker repo 的
[`PasswordVault_獨立化_規劃.md`](https://github.com/lx-kvn/FileLocker/blob/main/PasswordVault_獨立化_規劃.md)
與 [ADR-0003](https://github.com/lx-kvn/FileLocker/blob/main/docs/adr/0003-passwordvault-separate-repo.md)。

## 建置與驗證

Commit 前一律先跑過完整測試套件：`dotnet test PasswordVault.slnx`。

**不要自己判斷「差不多了」就直接下 `git commit`。** 當你認為工作已經到一個可以或應該 commit 的段落時，先跟使用者說一聲，等對方明確要求才執行。

**`git push` 是獨立於 `git commit` 的另一個確認步驟，commit 的同意不等於 push 的同意。** 每一次要 push（不管是第一次還是後續），都要另外明確問過、等使用者同意才執行，不要因為前一次 push 被同意過就假設這次也自動有同意。

## 已知的坑

單一執行個體的 Mutex 處理路徑：新增任何啟動路徑時，要處理「Mutex 已經被別的執行個體持有」的情況，並呼叫 `SetForegroundWindow`（或等效的前景焦點搶奪機制）把既有視窗搶到最前面，而不是直接結束或讓例外把行程弄崩潰（FileLocker.App 這邊曾經在這裡出過當機事故）。

`vendor/FileLocker.PluginContracts.dll` 是手動 vendor 的組件檔，不是這個 repo 建置產出的——原因見 `vendor/README.md`。只有 FileLocker repo 的 `FileLocker.PluginContracts` 介面本身變動時才需要重新編譯、重新複製過來。
