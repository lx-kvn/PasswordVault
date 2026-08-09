using System.Text.Json;
using FileLocker.Core.Security;
using FileLocker.PluginContracts;

namespace FileLocker.PasswordLocker;

/// <summary>
/// 可選配部件對外的唯一入口，實作 <see cref="IPasswordLockerPlugin"/>（見
/// FileLocker_密碼庫_功能規劃.md 第 2.1 節）。<see cref="HandleRequestAsync"/> 裡的 switch
/// 是從 FileLocker.App/MainWindow.xaml.cs 搬過來的——原本主體用一整排 case 直接呼叫
/// PasswordLockerProtocolHandlers 的方法、組裝回應，現在整段邏輯搬進部件自己內部，主體只認得
/// 「訊息名稱裡有 PasswordLocker 這個子字串就轉發過來」，不需要知道底下有哪些方法、參數長什麼樣。
/// </summary>
public sealed class PasswordLockerPlugin : IPasswordLockerPlugin
{
    private PasswordLockerProtocolHandlers? _handlers;

    public void Initialize(PasswordLockerPluginContext context)
    {
        Directory.CreateDirectory(context.DataDirectory);
        var store = new PasswordLockerStore(Path.Combine(context.DataDirectory, "credentials.json"));
        var lockoutTracker = new LockoutTracker(Path.Combine(context.DataDirectory, "lockout.json"));
        var service = new PasswordLockerService(store, lockoutTracker);
        _handlers = new PasswordLockerProtocolHandlers(service, context.VaultItemExists);
    }

    public async Task<object?> HandleRequestAsync(string messageType, JsonElement request, IntPtr ownerWindowHandle)
    {
        if (_handlers is null)
        {
            // Initialize 沒被呼叫過就收到請求，代表主體那邊的載入/初始化流程有 bug——
            // 不安靜吞掉，讓例外往上炸，主體的 try/catch 會轉成前端看得到的 error 訊息，
            // 比起回傳一個看起來正常、其實資料是空的回應更容易被發現。
            throw new InvalidOperationException("PasswordLockerPlugin.Initialize 尚未被呼叫。");
        }

        var handlers = _handlers;

        return messageType switch
        {
            "listPasswordLocker" => await HandleListAsync(handlers),
            "setupPasswordLockerCredential" => await HandleSetupCredentialAsync(handlers, request),
            "verifyPasswordLocker" => await HandleVerifyAsync(handlers, request, ownerWindowHandle),
            "verifyPasswordLockerByRecoveryKey" => await HandleVerifyByRecoveryKeyAsync(handlers, request),
            "setupPasswordLockerPasskey" => await HandleSetupPasskeyAsync(handlers, ownerWindowHandle),
            "disablePasswordLockerPasskey" => await HandleDisablePasskeyAsync(handlers, request, ownerWindowHandle),
            "setupPasswordLockerRecoveryKey" => await HandleSetupRecoveryKeyAsync(handlers),
            "disablePasswordLockerRecoveryKey" => await HandleDisableRecoveryKeyAsync(handlers, request, ownerWindowHandle),
            "addOrUpdatePasswordLockerCredential" => await HandleAddOrUpdateCredentialAsync(handlers, request),
            "revealPasswordLockerPassword" => await HandleRevealPasswordAsync(handlers, request),
            "revealPasswordLockerUsername" => await HandleRevealUsernameAsync(handlers, request),
            "revealPasswordLockerNotes" => await HandleRevealNotesAsync(handlers, request),
            "revealPasswordLockerTotp" => await HandleRevealTotpAsync(handlers, request),
            "revealPasswordLockerTotpForSite" => await HandleRevealTotpForSiteAsync(handlers, request),
            "deletePasswordLockerCredentials" => await HandleDeleteCredentialsAsync(handlers, request),
            "generatePasswordLockerPassword" => HandleGeneratePassword(request),
            "searchPasswordLockerNotes" => await HandleSearchNotesAsync(handlers, request),
            "changePasswordLockerPassword" => await HandleChangePasswordAsync(handlers, request),
            "exportPasswordLockerCsv" => await HandleExportCsvAsync(handlers),
            "importPasswordLockerCsv" => await HandleImportCsvAsync(handlers, request),
            "checkPasswordLockerPasswordReuse" => await HandleCheckPasswordReuseAsync(handlers, request),
            "findPasswordLockerCredentialsForDomain" => await HandleFindCredentialsForDomainAsync(handlers, request),
            "recordPasswordLockerSiteVerified" => HandleRecordSiteVerified(handlers, request),
            "isPasswordLockerSiteSessionValid" => HandleIsSiteSessionValid(handlers, request),
            "revealPasswordLockerCredentialForSite" => await HandleRevealCredentialForSiteAsync(handlers, request),
            // 未知的訊息名稱：回傳 null，不丟例外——訊息名稱含 "PasswordLocker" 但不是這個
            // 部件認得的動作，最常見的成因是部件版本比主體舊（主體已經會送新訊息、這份部件
            // 還不認得），不該讓整個 IPC 迴圈中斷。呼叫端（MainWindow
            // .HandlePasswordLockerModuleRequestAsync）收到 null 一定要送出一個看得見的錯誤
            // 回應給前端，不能安靜略過——安靜略過會讓前端的 requestMessage() 永遠等不到回應、
            // 畫面完全沒反應，那個坑實際發生過，見該方法裡的說明。
            _ => null
        };
    }

    private static async Task<object?> HandleListAsync(PasswordLockerProtocolHandlers handlers)
    {
        var items = await handlers.ListCredentialsAsync();

        return new
        {
            type = "passwordLockerListResult",
            configured = handlers.IsConfigured,
            passkeyEnabled = handlers.IsPasskeyEnabled,
            recoveryKeyEnabled = handlers.IsRecoveryKeyEnabled,
            sessionTimeoutMinutes = handlers.SessionTimeoutMinutes,
            items = items.Select(item => new
            {
                item.Id,
                category = item.Category.ToString(),
                item.Title,
                item.AssociatedDomains,
                item.Username,
                item.UsernameHidden,
                item.LinkedVaultItemUuid,
                item.SourceDeleted,
                item.CreatedAtUtc,
                item.UpdatedAtUtc,
                item.HasTotp
            })
        };
    }

    private static async Task<object?> HandleSetupCredentialAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var password = request.GetProperty("password").GetString() ?? "";
        var result = await handlers.SetupCredentialAsync(password);

        return new
        {
            type = "setupPasswordLockerCredentialResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleVerifyAsync(PasswordLockerProtocolHandlers handlers, JsonElement request, IntPtr ownerWindowHandle)
    {
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var tryPasskeyFirst = !request.TryGetProperty("tryPasskeyFirst", out var tryPasskeyProp) || tryPasskeyProp.GetBoolean();

        var result = await handlers.VerifyAsync(password, ownerWindowHandle, tryPasskeyFirst);

        return new
        {
            type = "verifyPasswordLockerResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleVerifyByRecoveryKeyAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var recoveryKey = request.GetProperty("recoveryKey").GetString() ?? "";
        var result = await handlers.VerifyByRecoveryKeyAsync(recoveryKey);

        return new
        {
            type = "verifyPasswordLockerByRecoveryKeyResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleSetupPasskeyAsync(PasswordLockerProtocolHandlers handlers, IntPtr ownerWindowHandle)
    {
        var result = await handlers.SetupPasskeyAsync(ownerWindowHandle);

        return new
        {
            type = "setupPasswordLockerPasskeyResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleDisablePasskeyAsync(PasswordLockerProtocolHandlers handlers, JsonElement request, IntPtr ownerWindowHandle)
    {
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var tryPasskeyFirst = !request.TryGetProperty("tryPasskeyFirst", out var tryPasskeyProp) || tryPasskeyProp.GetBoolean();

        var result = await handlers.DisablePasskeyAsync(password, ownerWindowHandle, tryPasskeyFirst);

        return new
        {
            type = "disablePasswordLockerPasskeyResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleSetupRecoveryKeyAsync(PasswordLockerProtocolHandlers handlers)
    {
        var result = await handlers.SetupRecoveryKeyAsync();

        return new
        {
            type = "setupPasswordLockerRecoveryKeyResult",
            result.Success,
            result.RecoveryKey,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleDisableRecoveryKeyAsync(PasswordLockerProtocolHandlers handlers, JsonElement request, IntPtr ownerWindowHandle)
    {
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var tryPasskeyFirst = !request.TryGetProperty("tryPasskeyFirst", out var tryPasskeyProp) || tryPasskeyProp.GetBoolean();

        var result = await handlers.DisableRecoveryKeyAsync(password, ownerWindowHandle, tryPasskeyFirst);

        return new
        {
            type = "disablePasswordLockerRecoveryKeyResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleAddOrUpdateCredentialAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var categoryRaw = request.GetProperty("category").GetString() ?? "Website";
        var category = Enum.Parse<CredentialCategory>(categoryRaw);
        var title = request.GetProperty("title").GetString() ?? "";
        var domains = request.GetProperty("domains").EnumerateArray()
            .Select(d => d.GetString() ?? "")
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();
        var username = request.TryGetProperty("username", out var usernameProp) ? usernameProp.GetString() ?? "" : "";
        var usernameHidden = request.TryGetProperty("usernameHidden", out var usernameHiddenProp) && usernameHiddenProp.GetBoolean();
        var password = request.GetProperty("password").GetString() ?? "";
        var notes = request.TryGetProperty("notes", out var notesProp) ? notesProp.GetString() : null;
        var linkedVaultItemUuid = request.TryGetProperty("linkedVaultItemUuid", out var linkedProp) ? linkedProp.GetString() : null;

        // "totp" 屬性有沒有出現在請求裡，決定要不要動這筆紀錄的 TOTP 設定——不出現＝這次存檔
        // 跟 TOTP 無關（例如只是改密碼），維持原樣；出現但是 JSON null＝使用者按了「移除
        // TOTP」；出現且是物件＝設定新密鑰。跟 PasswordLockerService.AddOrUpdateCredentialAsync
        // 的 updateTotp 旗標語意一致，見該方法上的說明。
        var updateTotp = request.TryGetProperty("totp", out var totpProp);
        string? totpSecret = null;
        string? totpAlgorithm = null;
        int? totpDigits = null;
        int? totpPeriodSeconds = null;
        if (updateTotp && totpProp.ValueKind == JsonValueKind.Object)
        {
            totpSecret = totpProp.TryGetProperty("secret", out var secretProp) ? secretProp.GetString() : null;
            totpAlgorithm = totpProp.TryGetProperty("algorithm", out var algorithmProp) ? algorithmProp.GetString() : null;
            totpDigits = totpProp.TryGetProperty("digits", out var digitsProp) && digitsProp.TryGetInt32(out var digitsValue) ? digitsValue : null;
            totpPeriodSeconds = totpProp.TryGetProperty("period", out var periodProp) && periodProp.TryGetInt32(out var periodValue) ? periodValue : null;
        }

        var result = await handlers.AddOrUpdateCredentialAsync(
            id, category, title, domains, username, password, notes, linkedVaultItemUuid, usernameHidden,
            updateTotp, totpSecret, totpAlgorithm, totpDigits, totpPeriodSeconds);

        return new
        {
            type = "addOrUpdatePasswordLockerCredentialResult",
            result.Success,
            result.EntryId,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleRevealPasswordAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.GetProperty("id").GetString() ?? "";
        var result = await handlers.RevealPasswordAsync(id);

        return new
        {
            type = "revealPasswordLockerPasswordResult",
            id,
            result.Success,
            result.Password,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleRevealUsernameAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.GetProperty("id").GetString() ?? "";
        var result = await handlers.RevealUsernameAsync(id);

        return new
        {
            type = "revealPasswordLockerUsernameResult",
            id,
            result.Success,
            result.Username,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleRevealNotesAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.GetProperty("id").GetString() ?? "";
        var result = await handlers.RevealNotesAsync(id);

        return new
        {
            type = "revealPasswordLockerNotesResult",
            id,
            result.Success,
            result.Notes,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleRevealTotpAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.GetProperty("id").GetString() ?? "";
        var result = await handlers.RevealTotpAsync(id);

        return new
        {
            type = "revealPasswordLockerTotpResult",
            id,
            result.Success,
            result.Secret,
            result.Algorithm,
            result.Digits,
            result.PeriodSeconds,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleDeleteCredentialsAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var ids = request.GetProperty("ids").EnumerateArray()
            .Select(i => i.GetString() ?? "")
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();

        var result = await handlers.DeleteCredentialsAsync(ids);

        return new
        {
            type = "deletePasswordLockerCredentialsResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleChangePasswordAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var newPassword = request.GetProperty("newPassword").GetString() ?? "";
        var result = await handlers.ChangePasswordAsync(newPassword);

        return new
        {
            type = "changePasswordLockerPasswordResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleSearchNotesAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var query = request.GetProperty("query").GetString() ?? "";
        var ids = await handlers.FindEntriesWithNotesContainingAsync(query);

        return new { type = "searchPasswordLockerNotesResult", ids };
    }

    /// <summary>新增/編輯表單即時顯示「這組密碼在密碼庫裡還有幾筆紀錄也在使用」（規劃文件第 6 節）
    /// ——純資訊性，不阻擋儲存，沒有驗證過 session 時 FindEntriesReusingPasswordAsync 本來就
    /// 安靜回傳空清單（見該方法說明），這裡不用另外處理「未驗證」的錯誤情況。excludeId 是
    /// 編輯既有紀錄時排除自己本身，不然「這筆密碼跟誰重複」永遠至少會算到自己。</summary>
    private static async Task<object?> HandleCheckPasswordReuseAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var password = request.GetProperty("password").GetString() ?? "";
        var excludeId = request.TryGetProperty("excludeId", out var excludeIdProp) ? excludeIdProp.GetString() : null;

        var reuseCount = 0;
        if (!string.IsNullOrEmpty(password))
        {
            var matches = await handlers.FindEntriesReusingPasswordAsync(password);
            reuseCount = matches.Count(id => id != excludeId);
        }

        return new { type = "checkPasswordLockerPasswordReuseResult", reuseCount };
    }

    // ---- 瀏覽器擴充功能專用（規劃文件第 5 節）：Native Messaging Host 轉接的請求 ----

    private static async Task<object?> HandleFindCredentialsForDomainAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var domain = request.GetProperty("domain").GetString() ?? "";
        var items = await handlers.FindCredentialsForDomainAsync(domain);

        return new
        {
            type = "findPasswordLockerCredentialsForDomainResult",
            items = items.Select(item => new
            {
                item.Id,
                category = item.Category.ToString(),
                item.Title,
                item.AssociatedDomains,
                item.Username,
                item.UsernameHidden,
                item.HasTotp
            })
        };
    }

    private static object? HandleRecordSiteVerified(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var domain = request.GetProperty("domain").GetString() ?? "";
        handlers.RecordSiteVerified(domain);
        return new { type = "recordPasswordLockerSiteVerifiedResult", success = true };
    }

    private static object? HandleIsSiteSessionValid(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var domain = request.GetProperty("domain").GetString() ?? "";
        var valid = handlers.IsSiteSessionValid(domain);
        return new { type = "isPasswordLockerSiteSessionValidResult", valid };
    }

    private static async Task<object?> HandleRevealCredentialForSiteAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.GetProperty("id").GetString() ?? "";
        var domain = request.GetProperty("domain").GetString() ?? "";
        var result = await handlers.RevealCredentialForSiteAsync(id, domain);

        return new
        {
            type = "revealPasswordLockerCredentialForSiteResult",
            id,
            result.Success,
            result.Password,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleRevealTotpForSiteAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var id = request.GetProperty("id").GetString() ?? "";
        var domain = request.GetProperty("domain").GetString() ?? "";
        var result = await handlers.RevealTotpForSiteAsync(id, domain);

        return new
        {
            type = "revealPasswordLockerTotpForSiteResult",
            id,
            result.Success,
            result.Secret,
            result.Algorithm,
            result.Digits,
            result.PeriodSeconds,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleExportCsvAsync(PasswordLockerProtocolHandlers handlers)
    {
        var result = await handlers.ExportCsvAsync();

        return new
        {
            type = "exportPasswordLockerCsvResult",
            result.Success,
            result.Csv,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static async Task<object?> HandleImportCsvAsync(PasswordLockerProtocolHandlers handlers, JsonElement request)
    {
        var csv = request.GetProperty("csv").GetString() ?? "";
        var result = await handlers.ImportCsvAsync(csv);

        return new
        {
            type = "importPasswordLockerCsvResult",
            result.Success,
            result.ImportedCount,
            result.SkippedCount,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        };
    }

    private static object? HandleGeneratePassword(JsonElement request)
    {
        var length = request.TryGetProperty("length", out var lengthProp) ? lengthProp.GetInt32() : 20;
        var includeSymbols = request.TryGetProperty("includeSymbols", out var symbolsProp) && symbolsProp.GetBoolean();

        var password = PasswordLockerProtocolHandlers.GeneratePassword(length, includeSymbols);
        var strength = PasswordLockerProtocolHandlers.EstimateStrength(password);

        return new
        {
            type = "generatePasswordLockerPasswordResult",
            password,
            strength = strength.ToString()
        };
    }
}
