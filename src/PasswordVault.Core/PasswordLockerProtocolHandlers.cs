using PasswordVault.Core.Models;

namespace PasswordVault.Core;

/// <summary>
/// 密碼庫的「解析前端請求 → 呼叫 Core 業務邏輯 → 組裝回應」中介層，比照 VaultProtocolHandlers
/// 的既有模式（不依賴任何 WPF／WebView2 型別，可以直接單元測試，見
/// PasswordLockerProtocolHandlersTests）——Phase 1 規劃時就記下密碼庫接 IPC 要走這個較新的
/// 模式，不要像資料夾防護那樣把解析/呼叫/組裝直接寫死在 MainWindow.xaml.cs 裡。
///
/// 這一層額外負責一件 Vault 沒有的事：Locker 主金鑰的 app 分頁 session 管理。Verify 類方法
/// 成功後呼叫 PasswordLockerService.RecordAppSessionVerified，之後 reveal/新增編輯/刪除等
/// 需要主金鑰的方法改用 TryGetAppSessionMasterKey 取得——拿不到就統一回傳
/// ErrorCodes.PasswordLockerNotVerified，前端收到這個錯誤碼才彈驗證 modal。金鑰本身完全
/// 不會出現在任何回傳給前端的回應物件裡。
/// </summary>
public sealed class PasswordLockerProtocolHandlers
{
    private const string NotVerifiedMessage = "尚未驗證身份";

    private readonly PasswordLockerService _service;
    private readonly Func<string, bool> _vaultItemExists;

    public PasswordLockerProtocolHandlers(PasswordLockerService service, Func<string, bool> vaultItemExists)
    {
        _service = service;
        _vaultItemExists = vaultItemExists;
    }

    public bool IsConfigured => _service.IsConfigured;
    public bool IsPasskeyEnabled => _service.IsPasskeyEnabled;
    public bool IsRecoveryKeyEnabled => _service.IsRecoveryKeyEnabled;
    public int SessionTimeoutMinutes => _service.SessionTimeoutMinutes;

    public Task<PasswordLockerResult> SetupCredentialAsync(string password)
        => _service.SetupCredentialAsync(password);

    /// <summary>tryPasskeyFirst 讓前端明確表示這次要不要先試 Passkey——前端已經先試過一次
    /// 靜默 Passkey、使用者改成手動輸入密碼的 fallback 流程要傳 false，不然即使密碼欄位有值，
    /// 這裡預設還是會先跳一次 Passkey 提示，變成使用者要連續應付兩次驗證。</summary>
    public async Task<PasswordLockerVerifyResponse> VerifyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
    {
        var result = await _service.VerifyAsync(password, ownerWindowHandle, tryPasskeyFirst);
        if (result.Success && result.MasterKey is not null)
        {
            _service.RecordAppSessionVerified(result.MasterKey);
        }
        return new PasswordLockerVerifyResponse(result.Success, result.ErrorMessage, result.ErrorCode, result.ErrorDetail);
    }

    public async Task<PasswordLockerVerifyResponse> VerifyByRecoveryKeyAsync(string recoveryKeyInput)
    {
        var result = await _service.VerifyByRecoveryKeyAsync(recoveryKeyInput);
        if (result.Success && result.MasterKey is not null)
        {
            _service.RecordAppSessionVerified(result.MasterKey);
        }
        return new PasswordLockerVerifyResponse(result.Success, result.ErrorMessage, result.ErrorCode, result.ErrorDetail);
    }

    public async Task<PasswordLockerResult> SetupPasskeyAsync(IntPtr ownerWindowHandle)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerResult(false, NotVerifiedMessage, ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.SetupPasskeyAsync(ownerWindowHandle, masterKey);
    }

    public Task<PasswordLockerResult> DisablePasskeyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
        => _service.DisablePasskeyAsync(password, ownerWindowHandle, tryPasskeyFirst);

    /// <summary>改密碼：需要目前已驗證過（拿得到 app session 主金鑰）——主金鑰本身不變，只是
    /// 重新包一次（見 PasswordLockerService.ChangePasswordAsync），改完後回應本身不含任何金鑰。</summary>
    public async Task<PasswordLockerResult> ChangePasswordAsync(string newPassword)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerResult(false, NotVerifiedMessage, ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.ChangePasswordAsync(newPassword, masterKey);
    }

    public async Task<PasswordLockerRecoveryKeyResult> SetupRecoveryKeyAsync()
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerRecoveryKeyResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }

        var (recoveryKey, result) = await _service.SetupRecoveryKeyAsync(masterKey);
        return new PasswordLockerRecoveryKeyResult(result.Success, recoveryKey, result.ErrorMessage, result.ErrorCode, result.ErrorDetail);
    }

    public Task<PasswordLockerResult> DisableRecoveryKeyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
        => _service.DisableRecoveryKeyAsync(password, ownerWindowHandle, tryPasskeyFirst);

    /// <summary>每次列清單前先跑一次「已加密檔案」的自我修復檢查（CheckLinkedVaultItemsAsync
    /// 本身之前只有方法本身寫好、從沒被任何呼叫端接上，導致 SourceDeleted 永遠停在建立當下的
    /// 值，檔案解密後密碼庫清單裡的那筆密碼不會顯示刪除線——見規劃文件第 4 節「自我修復」的
    /// 既定設計，這裡才是真正接上去的地方）。清單本來就是使用者最常觸發的操作（開分頁、
    /// 每次驗證後刷新），不需要另外設計獨立的排程或訊息去觸發這個檢查。</summary>
    public async Task<IReadOnlyList<PasswordCredentialMetadata>> ListCredentialsAsync()
    {
        await _service.CheckLinkedVaultItemsAsync(_vaultItemExists);
        return await _service.ListCredentialsMetadataAsync();
    }

    public async Task<PasswordLockerDecryptedPasswordResult> RevealPasswordAsync(string id)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerDecryptedPasswordResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.GetDecryptedPasswordAsync(id, masterKey);
    }

    public async Task<PasswordLockerEntryResult> AddOrUpdateCredentialAsync(
        string? id, CredentialCategory category, string title, IReadOnlyList<string> domains,
        string username, string password, string? notes, string? linkedVaultItemUuid, bool usernameHidden = false,
        bool updateTotp = false, string? totpSecret = null, string? totpAlgorithm = null,
        int? totpDigits = null, int? totpPeriodSeconds = null)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerEntryResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.AddOrUpdateCredentialAsync(id, category, title, domains, username, password, notes, linkedVaultItemUuid, masterKey,
            usernameHidden, updateTotp, totpSecret, totpAlgorithm, totpDigits, totpPeriodSeconds);
    }

    /// <summary>只有 UsernameHidden 的憑證才需要走這條路徑解密——沒隱藏的帳號前端清單本來就
    /// 拿得到明文，不需要另外呼叫。跟 RevealPasswordAsync 分開是刻意的：帳號欄位的顯示/隱藏
    /// 現在是獨立於密碼眼睛圖示的另一組互動（見規劃討論），不應該共用同一個回應形狀。</summary>
    public async Task<PasswordLockerDecryptedUsernameResult> RevealUsernameAsync(string id)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerDecryptedUsernameResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.GetDecryptedUsernameAsync(id, masterKey);
    }

    /// <summary>編輯表單開啟時用——之前這個管道不存在，編輯表單的備註欄位一律顯示空字串，
    /// 使用者看不出這筆紀錄其實已經有備註（2026-08-09 這輪對話發現的缺口：擴充功能能寫入
    /// 備註，但 App 端完全沒有對應的讀取路徑）。</summary>
    public async Task<PasswordLockerDecryptedNotesResult> RevealNotesAsync(string id)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerDecryptedNotesResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.GetDecryptedNotesAsync(id, masterKey);
    }

    /// <summary>TOTP 揭露比密碼／備註更嚴格——除了 app session 主金鑰，還要求
    /// IsWithinTotpRevealFreshnessWindow（見 PasswordLockerService 上的說明：距離上一次真的
    /// 完成一次完整驗證要在 30 秒內），前端一律先強制跳一次驗證彈窗（不像密碼那樣沿用既有
    /// session），這裡是後端獨立於前端行為的第二道防線。逾期回傳的錯誤碼刻意跟一般
    /// PasswordLockerNotVerified 共用同一個，前端不需要新的錯誤處理分支，重新跳一次驗證彈窗
    /// 就會自然滿足新鮮度視窗。</summary>
    public async Task<PasswordLockerDecryptedTotpResult> RevealTotpAsync(string id)
    {
        if (!_service.IsWithinTotpRevealFreshnessWindow())
        {
            return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.GetDecryptedTotpAsync(id, masterKey);
    }

    /// <summary>批次刪除：逐一呼叫，遇到第一筆失敗就回傳該筆的錯誤，不繼續處理剩下的——
    /// 這輪的批次刪除本來就是「先驗證＋最終確認」把使用者的誤刪風險擋在前面，中途失敗
    /// 通常代表資料被其他地方動過，直接停下讓使用者重新整理清單比較安全，不做部分成功的
    /// 複雜回報。</summary>
    public async Task<PasswordLockerResult> DeleteCredentialsAsync(IReadOnlyList<string> ids)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerResult(false, NotVerifiedMessage, ErrorCodes.PasswordLockerNotVerified);
        }

        foreach (var id in ids)
        {
            var result = await _service.DeleteCredentialAsync(id);
            if (!result.Success)
            {
                return result;
            }
        }
        return new PasswordLockerResult(true);
    }

    public Task<IReadOnlyList<string>> CheckLinkedVaultItemsAsync()
        => _service.CheckLinkedVaultItemsAsync(_vaultItemExists);

    public static string GeneratePassword(int length, bool includeSymbols)
        => PasswordLockerService.GeneratePassword(length, includeSymbols);

    public static PasswordStrength EstimateStrength(string password)
        => PasswordLockerService.EstimateStrength(password);

    public async Task<IReadOnlyList<string>> FindEntriesReusingPasswordAsync(string password)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        return masterKey is null ? [] : await _service.FindEntriesReusingPasswordAsync(password, masterKey);
    }

    /// <summary>沒有有效的 app session 時直接回傳空清單，不回錯誤——清單搜尋框每次打字都可能呼叫
    /// 這個方法，還沒驗證身份是預期內的常態情況，不是例外狀況，讓前端能安靜地退回只搜尋明文欄位。</summary>
    public async Task<IReadOnlyList<string>> FindEntriesWithNotesContainingAsync(string query)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        return masterKey is null ? [] : await _service.FindEntriesWithNotesContainingAsync(query, masterKey);
    }

    public async Task<PasswordLockerCsvExportResult> ExportCsvAsync()
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerCsvExportResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        var csv = await _service.ExportToCsvAsync(masterKey);
        return new PasswordLockerCsvExportResult(true, csv);
    }

    public async Task<PasswordLockerCsvImportResult> ImportCsvAsync(string csv)
    {
        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerCsvImportResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        return await _service.ImportFromCsvAsync(csv, masterKey);
    }

    // ---- 瀏覽器擴充功能專用（規劃文件第 5 節）：Native Messaging Host 轉接的請求最終會走到這裡 ----

    /// <summary>不需要驗證身份就能查——回傳的是 metadata（不含密碼），跟 App 內清單「不用解鎖
    /// 就能查」是同一個設計理由，讓瀏覽器分頁載入時能安靜比對網域，不用先跳驗證才知道要不要
    /// 顯示自動填入提示。</summary>
    public Task<IReadOnlyList<PasswordCredentialMetadata>> FindCredentialsForDomainAsync(string domain)
        => _service.FindCredentialsForDomainAsync(domain);

    /// <summary>每網站獨立計時、滑動視窗（規劃文件第 3 節）——跟 App 分頁共用的
    /// TryGetAppSessionMasterKey 是分開的兩份執行期狀態，這裡只負責「這個網站要不要跳過再驗證」
    /// 這個 UX 節流判斷，不代表拿得到金鑰，見 RevealCredentialForSiteAsync 兩者都要過的說明。</summary>
    public void RecordSiteVerified(string domain)
        => _service.RecordSiteVerified(domain);

    public bool IsSiteSessionValid(string domain)
        => _service.IsSiteSessionValid(domain);

    /// <summary>瀏覽器自動填入實際要密碼時走這裡——網站的獨立 session 跟 App 分頁的 session
    /// 兩個都要有效才給密碼：前者代表「這個網站最近驗證過，UX 上不用再煩使用者」，後者才是真的
    /// 拿得到主金鑰的那一份。兩個 session 各自獨立逾時，其中一個先過期就要重新走一次驗證流程，
    /// 不能因為「網站 session 顯示還沒過期」就跳過主金鑰檢查。</summary>
    public async Task<PasswordLockerDecryptedPasswordResult> RevealCredentialForSiteAsync(string id, string domain)
    {
        if (!_service.IsSiteSessionValid(domain))
        {
            return new PasswordLockerDecryptedPasswordResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }

        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerDecryptedPasswordResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }

        // GetDecryptedPasswordForDomainAsync（不是 GetDecryptedPasswordAsync）：多一道「這筆
        // 憑證真的關聯這個網域」的檢查，見該方法上的稽核說明——這裡是唯一只驗證了「網站 session」
        // 就能拿到密碼明文的路徑，不能只靠上面兩個 session 計時器擋。
        return await _service.GetDecryptedPasswordForDomainAsync(id, domain, masterKey);
    }

    /// <summary>瀏覽器版的 RevealTotpAsync——三道檢查都要過：網站 session、app session 主金鑰、
    /// 新鮮度視窗。PasswordLockerNativePipeServer 的 confused-deputy 重試機制（收到
    /// NOT_VERIFIED 就跳驗證視窗、通過後重打一次）剛好對應「每次都要重新驗證」的要求——
    /// 瀏覽器那端每次要看 TOTP 都會因為新鮮度視窗過期而重新觸發整個驗證流程，不需要額外
    /// 設計一個「強制忽略 site session」的機制。</summary>
    public async Task<PasswordLockerDecryptedTotpResult> RevealTotpForSiteAsync(string id, string domain)
    {
        if (!_service.IsSiteSessionValid(domain))
        {
            return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }
        if (!_service.IsWithinTotpRevealFreshnessWindow())
        {
            return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }

        var masterKey = _service.TryGetAppSessionMasterKey();
        if (masterKey is null)
        {
            return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: NotVerifiedMessage, ErrorCode: ErrorCodes.PasswordLockerNotVerified);
        }

        return await _service.GetDecryptedTotpForDomainAsync(id, domain, masterKey);
    }
}
