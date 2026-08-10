# vendor/

`FileLocker.PluginContracts.dll` 是從 [FileLocker repo](https://github.com/lx-kvn/FileLocker) 的
`src/FileLocker.PluginContracts/` 編譯出來、手動複製進來的二進位檔，不是這個 repo 建置產出的。

## 為什麼要 vendor 這份 dll

`FileLocker.App` 用 `AssemblyLoadContext` 動態載入密碼庫外掛時，會強制要求外掛跟宿主共用同一份
`FileLocker.PluginContracts` 組件（見 FileLocker repo 的
`src/FileLocker.App/PasswordLockerPluginLoader.cs` 裡 `PasswordLockerLoadContext.Load` 的實作與註解）——
`IPasswordLockerPlugin` 這個介面型別如果在兩個組件檔裡各自定義一份，即使程式碼逐字相同，CLR 也會
判定成兩個不同的 Type，強制轉型會直接失敗。所以 `PasswordVault.Core` 供 `FileLocker.App` 下載使用的
那份 build，必須編譯期真的參照到這份組件檔本身，不能用「重新定義同名介面」的方式取代。

這個介面本質上是穩定的 ABI 合約（單一檔案、預期極少變動），所以選擇手動 vendor 編譯好的 dll，而不是
架一條 NuGet 發布管線——後者對目前的維護規模不成比例。

## 更新時機

只有 FileLocker repo 的 `FileLocker.PluginContracts` 介面本身變動時，才需要重新編譯、重新複製這份
dll 過來（`dotnet build src/FileLocker.PluginContracts/FileLocker.PluginContracts.csproj -c Release`）。
