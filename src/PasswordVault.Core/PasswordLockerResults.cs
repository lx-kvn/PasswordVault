namespace PasswordVault.Core;

/// <summary>
/// 對應「密碼庫」（Password Locker）一般操作的結果，形狀比照 Core 的 FolderGuardResult
/// （這幾個記錄型別原本跟 Vault／資料夾防護的結果型別放在同一個檔案，密碼庫獨立成可選配部件
/// 後搬到這裡，形狀本身沒有變動）。
/// </summary>
public record PasswordLockerResult(bool Success, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 對應密碼庫的身份驗證：跟資料夾防護的 FolderGuardUnlockResult 不同——密碼庫存的是真的要加密的
/// 內容，驗證成功時要把 Locker 主金鑰一併回傳給呼叫端繼續做 CRUD 操作（形狀比照 LockService
/// 內部的 PasswordVerification），不是純粹的通過/沒通過。呼叫端用完 MasterKey 後要自行
/// CryptographicOperations.ZeroMemory 清掉，跟 LockService.DecryptAndRestore 的既有慣例一致。
/// </summary>
public record PasswordLockerVerifyResult(bool Success, byte[]? MasterKey = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>對應「設定恢復金鑰」：跟 LockResult.RecoveryKey 的顯示慣例一致——只在這次呼叫回傳看得到，
/// FileLocker 不會留下任何副本，呼叫端收到後要立刻顯示給使用者做「已抄下」的確認。</summary>
public record PasswordLockerRecoveryKeyResult(bool Success, string? RecoveryKey = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>新增/更新一筆密碼庫憑證的結果，附帶這筆紀錄的 Id 方便呼叫端後續操作。</summary>
public record PasswordLockerEntryResult(bool Success, string? EntryId = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>取得單筆憑證解密後密碼的結果。</summary>
public record PasswordLockerDecryptedPasswordResult(bool Success, string? Password = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>取得單筆憑證解密後帳號的結果——只有 UsernameHidden 的憑證才需要走這條路徑，
/// 沒隱藏的憑證帳號本來就是明文，直接回傳 entry.Username（見 PasswordLockerService
/// .GetDecryptedUsernameAsync），形狀跟 PasswordLockerDecryptedPasswordResult 對稱但分開，
/// 避免呼叫端誤把兩種完全不同的密文欄位混在一起處理。</summary>
public record PasswordLockerDecryptedUsernameResult(bool Success, string? Username = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>取得單筆憑證解密後備註的結果——沒存過備註（EncryptedNotesBase64 為 null）
/// 直接回傳空字串，不需要走解密（見 PasswordLockerService.GetDecryptedNotesAsync）。</summary>
public record PasswordLockerDecryptedNotesResult(bool Success, string? Notes = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 密碼庫清單頁用的憑證中繼資料——刻意不含解密後的密碼欄位，只有真的需要密碼本身（自動填入、
/// 編輯畫面顯示、CSV 匯出）才呼叫 GetDecryptedPasswordAsync／ExportToCsv 額外解密，清單本身
/// 不需要驗證身份就能查（AssociatedDomains／Username／Title 都是明文，見 PasswordCredentialEntry
/// 的說明）。SourceDeleted 只有 EncryptedFile 類別會是 true，代表對應的 Vault 項目已經消失。
/// </summary>
/// <summary>CSV 匯出（規劃文件第 7 節）的結果，內容欄位就是完整的明文 CSV 字串，交給呼叫端
/// （MainWindow）跳原生存檔對話框寫入磁碟——密碼庫這個部件本身不碰檔案系統存檔 UI。</summary>
public record PasswordLockerCsvExportResult(bool Success, string? Csv = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>CSV 匯入（規劃文件第 7 節）的結果。ImportedCount／SkippedCount 讓前端能回報
/// 「成功匯入 N 筆，略過 M 筆（缺密碼欄位）」，不做逐筆錯誤明細——匯入的資料來源通常是
/// 瀏覽器匯出的既有 CSV，逐筆列出略過原因對這個情境的價值有限，且會讓回應形狀複雜化。</summary>
public record PasswordLockerCsvImportResult(bool Success, int ImportedCount = 0, int SkippedCount = 0, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

public record PasswordCredentialMetadata(
    string Id,
    CredentialCategory Category,
    string Title,
    IReadOnlyList<string> AssociatedDomains,
    string Username,
    bool UsernameHidden,
    string? LinkedVaultItemUuid,
    bool SourceDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool HasTotp = false);

/// <summary>解密後的 TOTP 設定，給前端本地開始算動態碼用——見
/// PasswordLockerService.RevealTotpAsync 上的新鮮度視窗說明，這個方法本身不受一般 session
/// 保護，要求近期內剛完成過一次完整驗證。</summary>
public record PasswordLockerDecryptedTotpResult(
    bool Success, string? Secret = null, string? Algorithm = null, int? Digits = null, int? PeriodSeconds = null,
    string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);
