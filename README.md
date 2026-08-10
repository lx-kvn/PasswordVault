# PasswordVault

獨立的密碼管理應用程式，不依賴其他軟體單獨運作。核心邏輯（`PasswordVault.Core`）與桌面應用程式（`PasswordVault.exe`，含內建 CLI）皆遷出自 [FileLocker](https://github.com/lx-kvn/FileLocker) 的密碼庫（Password Locker）功能——FileLocker 之後改成消費本專案編譯產出的可選配部件。

**目前狀態：規劃階段，尚未開始遷移程式碼。** 架構決策與理由記錄在 FileLocker repo 的以下文件：

- [`PasswordVault_獨立化_規劃.md`](https://github.com/lx-kvn/FileLocker/blob/main/PasswordVault_獨立化_規劃.md)——完整規劃文件
- [ADR-0003](https://github.com/lx-kvn/FileLocker/blob/main/docs/adr/0003-passwordvault-separate-repo.md)——拆分成獨立 repo 的決策紀錄

`src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/`、對應測試專案的原始碼與 commit 歷史，會用 `git filter-repo` 從 FileLocker repo 遷移過來，這份 README 會在遷移動工時一併更新。
