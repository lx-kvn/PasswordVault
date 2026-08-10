using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileLocker.Core.Crypto;
using FileLocker.Core.Models;
using FileLocker.Core.Security;

namespace FileLocker.PasswordLocker;

public enum PasswordStrength
{
    Weak,
    Medium,
    Strong
}

/// <summary>
/// 對外門面，整合密碼庫子系統：PasswordLockerStore（憑證持久化）、PasskeyProtector／
/// RecoveryKeyProtector（跟 Vault 一樣的完整 wrap/unwrap 流程，密碼庫存的是真的要加密的內容，
/// 不是資料夾防護那種純驗證用法）、Argon2KeyDerivation（衍生 Locker 主金鑰）、LockoutTracker
/// （暴力猜測防護，鍵值固定用 <see cref="LockoutKey"/>，只套用在密碼路徑——Passkey 是 TPM 硬體
/// 簽章，沒有能被暴力猜測的「猜」的環節；恢復金鑰嚴格說仍是可猜測的秘密，只是 keyspace
/// 大到暴力猜測不可行，兩者都不需要 LockoutTracker 這種節流機制，跟資料夾防護的既有邏輯
/// 一致）。
///
/// 跟 LockService、FolderGuardService 都是平行、互不依賴的獨立子系統——「已加密檔案」類別要
/// 檢查對應 Vault 項目是否還存在時，透過委派（見 CheckLinkedVaultItemsAsync）而不是直接依賴
/// VaultIndexCache，避免 Core 內部子系統互相硬依賴。
/// </summary>
public class PasswordLockerService
{
    private const string LockoutKey = "password-locker-unlock";

    private readonly PasswordLockerStore _store;
    private readonly LockoutTracker _lockoutTracker;
    private readonly Dictionary<string, DateTime> _siteSessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App 分頁內共用的驗證 session（規劃文件第 11.2 節）——主金鑰只留在這裡、不送前端
    /// JS，避免 WebView2 執行環境（例如 XSS）能直接撈走解密金鑰。是每網站 <see cref="_siteSessions"/>
    /// 的平行概念，資料形狀類似但作用範圍不同：這裡整個分頁共用一個計時器，不分網站。</summary>
    private byte[]? _appSessionMasterKey;
    private DateTime? _appSessionExpiresUtc;

    /// <summary>TOTP 動態碼的揭露要求比密碼更嚴格——即使 app session／每網站 session 都還有效，
    /// 距離「上一次真的完成一次完整驗證」超過 <see cref="TotpRevealFreshnessWindow"/> 就要重新
    /// 驗證，不能沿用一般 1-60 分鐘的 session。見 <see cref="RecordAppSessionVerified"/> 的更新
    /// 時機、<see cref="IsWithinTotpRevealFreshnessWindow"/> 的檢查邏輯。</summary>
    private DateTime? _lastFullVerificationUtc;
    private static readonly TimeSpan TotpRevealFreshnessWindow = TimeSpan.FromSeconds(30);

    public PasswordLockerService(PasswordLockerStore store, LockoutTracker lockoutTracker)
    {
        _store = store;
        _lockoutTracker = lockoutTracker;
    }

    public bool IsConfigured => _store.Load().PasswordVerificationHashBase64 is not null;
    public bool IsPasskeyEnabled => _store.Load().PasskeyEnabled;
    public bool IsRecoveryKeyEnabled => _store.Load().RecoveryKeyEnabled;
    public int SessionTimeoutMinutes => _store.Load().SessionTimeoutMinutes;

    // ---- 設定 ----

    public async Task<PasswordLockerResult> SetupCredentialAsync(string password)
    {
        return await Task.Run(() =>
        {
            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(password, salt);
            var masterKey = RandomNumberGenerator.GetBytes(KeyDerivationDefaults.MasterKeySizeBytes);

            _store.Mutate(data =>
            {
                data.PasswordSaltBase64 = Convert.ToBase64String(salt);
                data.PasswordVerificationHashBase64 = Convert.ToBase64String(derived.VerificationHash);
                data.PasswordWrappedMasterKeyBase64 = EncryptField(derived.EncryptionKey, Convert.ToBase64String(masterKey));
            });

            CryptographicOperations.ZeroMemory(derived.EncryptionKey);
            CryptographicOperations.ZeroMemory(derived.VerificationHash);
            CryptographicOperations.ZeroMemory(masterKey);

            return new PasswordLockerResult(true);
        });
    }

    /// <summary>改密碼：呼叫端（PasswordLockerProtocolHandlers）用目前的 app session 主金鑰
    /// 證明「這個人已經驗證過身份」，這裡只需要重新產生密碼相關的 salt／驗證雜湊，並把「同一把」
    /// 主金鑰用新密碼衍生出來的包裝金鑰重新包一次——不動任何一筆憑證的加密內容，Passkey／恢復
    /// 金鑰包的也還是同一把主金鑰，全部維持有效。</summary>
    public async Task<PasswordLockerResult> ChangePasswordAsync(string newPassword, byte[] currentMasterKey)
    {
        return await Task.Run(() =>
        {
            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(newPassword, salt);

            _store.Mutate(data =>
            {
                data.PasswordSaltBase64 = Convert.ToBase64String(salt);
                data.PasswordVerificationHashBase64 = Convert.ToBase64String(derived.VerificationHash);
                data.PasswordWrappedMasterKeyBase64 = EncryptField(derived.EncryptionKey, Convert.ToBase64String(currentMasterKey));
            });

            CryptographicOperations.ZeroMemory(derived.EncryptionKey);
            CryptographicOperations.ZeroMemory(derived.VerificationHash);

            return new PasswordLockerResult(true);
        });
    }

    public async Task<PasswordLockerResult> SetupPasskeyAsync(IntPtr ownerWindowHandle, byte[] lockerMasterKey)
    {
        var credentialName = PasskeyProtector.GenerateCredentialName();
        var created = await PasskeyProtector.CreateCredentialAsync(credentialName, ownerWindowHandle);
        if (!created)
        {
            return new PasswordLockerResult(false, "Passkey 設定失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
        }

        var challenge = PasskeyProtector.GenerateChallenge();
        var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);
        if (signature is null)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
            return new PasswordLockerResult(false, "Passkey 設定失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
        }

        string wrapped;
        var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
        try
        {
            wrapped = PasskeyProtector.WrapContentKey(wrappingKey, lockerMasterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(signature);
        }

        _store.Mutate(data =>
        {
            data.PasskeyCredentialName = credentialName;
            data.PasskeyChallengeBase64 = Convert.ToBase64String(challenge);
            data.PasskeyWrappedMasterKeyBase64 = wrapped;
            data.PasskeyEnabled = true;
        });

        return new PasswordLockerResult(true);
    }

    /// <summary>停用前一樣先驗證身份（密碼/Passkey，Passkey 優先），跟資料夾防護既有慣例一致。
    /// tryPasskeyFirst 讓呼叫端明確表示「這次就是要用密碼驗證」（例如第一次嘗試 Passkey 被取消、
    /// 使用者改成輸入密碼的 fallback 流程）時可以關掉——不然即使呼叫端已經附上密碼，這裡預設
    /// 還是會先跳一次 Passkey 提示，變成使用者要連續應付兩次驗證才能完成一次停用。</summary>
    public async Task<PasswordLockerResult> DisablePasskeyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
    {
        var verify = await VerifyAsync(password, ownerWindowHandle, tryPasskeyFirst);
        if (!verify.Success)
        {
            return new PasswordLockerResult(false, verify.ErrorMessage, verify.ErrorCode, verify.ErrorDetail);
        }
        if (verify.MasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(verify.MasterKey);
        }

        // 刪除 OS 端的 Passkey 認證是非同步呼叫，不能包在 Mutate 的同步委派裡（那把鎖只保護
        // 檔案讀寫本身，長時間持有會擋住其他等待這把鎖的呼叫）——先讀一次目前的認證名稱
        // （這裡即使跟另一次設定變更有極短暫的競態也無妨，最差情況是刪除了一個已經被換掉的
        // 舊認證名稱，不影響資料正確性），刪除完成後才用 Mutate 清空欄位。
        var credentialName = _store.Load().PasskeyCredentialName;
        if (credentialName is not null)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
        }

        _store.Mutate(data =>
        {
            data.PasskeyCredentialName = null;
            data.PasskeyWrappedMasterKeyBase64 = null;
            data.PasskeyEnabled = false;
        });

        return new PasswordLockerResult(true);
    }

    /// <summary>恢復金鑰只在這次呼叫回傳看得到，FileLocker 不留任何副本——呼叫端收到後要立刻
    /// 顯示給使用者，強制做出「已抄下」的確認（跟 LockResult.RecoveryKey 的既有慣例一致）。</summary>
    public async Task<(string? RecoveryKey, PasswordLockerResult Result)> SetupRecoveryKeyAsync(byte[] lockerMasterKey)
    {
        return await Task.Run(() =>
        {
            var recoveryKeyBytes = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
            var display = RecoveryKeyProtector.FormatForDisplay(recoveryKeyBytes);

            string wrapped;
            var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
            try
            {
                wrapped = RecoveryKeyProtector.WrapContentKey(wrappingKey, lockerMasterKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
                CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            }

            _store.Mutate(data =>
            {
                data.RecoveryKeyWrappedMasterKeyBase64 = wrapped;
                data.RecoveryKeyEnabled = true;
            });

            return ((string?)display, new PasswordLockerResult(true));
        });
    }

    /// <summary>tryPasskeyFirst 的理由跟 <see cref="DisablePasskeyAsync"/> 一樣。</summary>
    public async Task<PasswordLockerResult> DisableRecoveryKeyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
    {
        var verify = await VerifyAsync(password, ownerWindowHandle, tryPasskeyFirst);
        if (!verify.Success)
        {
            return new PasswordLockerResult(false, verify.ErrorMessage, verify.ErrorCode, verify.ErrorDetail);
        }
        if (verify.MasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(verify.MasterKey);
        }

        _store.Mutate(data =>
        {
            data.RecoveryKeyWrappedMasterKeyBase64 = null;
            data.RecoveryKeyEnabled = false;
        });

        return new PasswordLockerResult(true);
    }

    // ---- 驗證 ----

    /// <summary>Passkey 已設定時優先嘗試；沒設定、或呼叫端明確不想用時走密碼路徑。密碼路徑受
    /// LockoutTracker 保護，Passkey 路徑略過鎖定機制（TPM 硬體驗證沒有「猜」的環節）。成功時附帶
    /// Locker 主金鑰，呼叫端用完後要自行 CryptographicOperations.ZeroMemory 清掉。</summary>
    public async Task<PasswordLockerVerifyResult> VerifyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
    {
        var data = _store.Load();

        if (tryPasskeyFirst && data.PasskeyEnabled && data.PasskeyCredentialName is { } credentialName
            && data.PasskeyChallengeBase64 is { } challengeBase64)
        {
            // 一定要重複使用設定當下存起來的那份挑戰資料，不能每次驗證都重新產生亂數——
            // 簽章結果是挑戰資料的函式，挑戰換了簽章就換，包裝金鑰也跟著換，UnwrapContentKey
            // 一定會失敗（見 PasswordLockerData.PasskeyChallengeBase64 的說明、對照
            // LockService.DecryptByPasskeyAsync 的既有寫法）。
            var challenge = Convert.FromBase64String(challengeBase64);
            var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);
            if (signature is not null)
            {
                try
                {
                    byte[] masterKey;
                    var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
                    try
                    {
                        masterKey = PasskeyProtector.UnwrapContentKey(wrappingKey, data.PasskeyWrappedMasterKeyBase64!);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(wrappingKey);
                        CryptographicOperations.ZeroMemory(signature);
                    }
                    return new PasswordLockerVerifyResult(true, masterKey);
                }
                catch (CryptographicException)
                {
                    // 不能讓例外往上炸——呼叫端（IPC 協定層）會因此永遠等不到回應，前端的請求
                    // 就這樣卡死，使用者只會看到「按了完全沒反應」，比明確回報「Passkey 驗證失敗」
                    // 更難排查。統一當作 Passkey 這條路徑沒通過，讓呼叫端retry或退回密碼。
                    if (password is null)
                    {
                        return new PasswordLockerVerifyResult(false, null, "Passkey 驗證失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
                    }
                }
            }
            else if (password is null)
            {
                // Passkey 已設定但驗證失敗/被取消，且呼叫端沒有同時附上密碼——「Passkey 已設定就只走
                // Passkey」，不退回密碼，見資料夾防護既有的同一個設計理由。
                return new PasswordLockerVerifyResult(false, null, "Passkey 驗證失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
            }
        }

        if (data.PasswordSaltBase64 is null || data.PasswordVerificationHashBase64 is null)
        {
            return new PasswordLockerVerifyResult(false, null, "尚未設定密碼庫", ErrorCode: ErrorCodes.PasswordLockerNotConfigured);
        }

        if (password is null)
        {
            return new PasswordLockerVerifyResult(false, null, "密碼錯誤", ErrorCode: ErrorCodes.PasswordLockerPasswordIncorrect);
        }

        var lockoutStatus = _lockoutTracker.CheckStatus(LockoutKey);
        if (lockoutStatus.IsLockedOut)
        {
            var remainingSeconds = (int)Math.Ceiling(lockoutStatus.RemainingLockout!.Value.TotalSeconds);
            return new PasswordLockerVerifyResult(false, null, "嘗試次數過多，請稍後再試",
                ErrorCode: ErrorCodes.PasswordLockerLockedOut, ErrorDetail: remainingSeconds.ToString());
        }

        var salt = Convert.FromBase64String(data.PasswordSaltBase64);
        var storedHash = Convert.FromBase64String(data.PasswordVerificationHashBase64);
        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(password, salt, storedHash);

        if (!isValid)
        {
            _lockoutTracker.RecordFailedAttempt(LockoutKey);
            return new PasswordLockerVerifyResult(false, null, "密碼錯誤", ErrorCode: ErrorCodes.PasswordLockerPasswordIncorrect);
        }

        _lockoutTracker.RecordSuccess(LockoutKey);

        byte[] unwrappedMasterKey;
        if (data.PasswordWrappedMasterKeyBase64 is null)
        {
            // 相容改版前（沒有主金鑰包裝這一層）建立的舊格式資料：那時候的「主金鑰」就是
            // Argon2 算出來的 encryptionKey 本身，直接沿用，不強迫使用者重新設定——下次呼叫
            // ChangePasswordAsync 就會自然升級成新格式（見 PasswordLockerData 的說明）。
            unwrappedMasterKey = encryptionKey!;
        }
        else
        {
            // encryptionKey 這裡只是包裝金鑰，不是 Locker 主金鑰本身——要解開才拿得到真正
            // 用來加解密憑證的主金鑰。
            unwrappedMasterKey = Convert.FromBase64String(Encoding.UTF8.GetString(DecryptField(encryptionKey!, data.PasswordWrappedMasterKeyBase64)));
            CryptographicOperations.ZeroMemory(encryptionKey!);
        }
        return new PasswordLockerVerifyResult(true, unwrappedMasterKey);
    }

    public async Task<PasswordLockerVerifyResult> VerifyByRecoveryKeyAsync(string recoveryKeyInput)
    {
        return await Task.Run(() =>
        {
            var data = _store.Load();
            if (!data.RecoveryKeyEnabled || data.RecoveryKeyWrappedMasterKeyBase64 is null)
            {
                return new PasswordLockerVerifyResult(false, null, "尚未設定密碼庫恢復金鑰", ErrorCode: ErrorCodes.PasswordLockerRecoveryKeyNotEnabled);
            }

            var recoveryKeyBytes = RecoveryKeyProtector.ParseUserInput(recoveryKeyInput);
            if (recoveryKeyBytes is null)
            {
                return new PasswordLockerVerifyResult(false, null, "恢復金鑰格式不正確", ErrorCode: ErrorCodes.PasswordLockerRecoveryKeyInvalidFormat);
            }

            var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
            try
            {
                var masterKey = RecoveryKeyProtector.UnwrapContentKey(wrappingKey, data.RecoveryKeyWrappedMasterKeyBase64);
                return new PasswordLockerVerifyResult(true, masterKey);
            }
            catch (CryptographicException)
            {
                return new PasswordLockerVerifyResult(false, null, "恢復金鑰不正確", ErrorCode: ErrorCodes.PasswordLockerRecoveryKeyIncorrect);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
                CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            }
        });
    }

    // ---- CRUD ----

    /// <summary>updateTotp 是「這次請求要不要動 TOTP 欄位」的旗標——不能單靠 totpSecret 是否為
    /// null 判斷，因為「不提供 TOTP 相關參數（維持現狀不動）」跟「明確清空 TOTP」都會讓
    /// totpSecret 是 null／空字串，兩者語意完全不同（前者是這次存檔跟 TOTP 無關，例如只是改
    /// 密碼；後者是使用者在表單裡按了「移除 TOTP」）。updateTotp=false 時完全不碰既有紀錄的
    /// EncryptedTotpSecretBase64；updateTotp=true 時，totpSecret 是空字串／null 代表清空，
    /// 非空字串代表設定新密鑰（連同 algorithm／digits／period 一起，沒有給的話用標準預設值
    /// SHA1/6/30）。</summary>
    public async Task<PasswordLockerEntryResult> AddOrUpdateCredentialAsync(
        string? id, CredentialCategory category, string title, IReadOnlyList<string> domains,
        string username, string password, string? notes, string? linkedVaultItemUuid, byte[] masterKey,
        bool usernameHidden = false, bool updateTotp = false, string? totpSecret = null,
        string? totpAlgorithm = null, int? totpDigits = null, int? totpPeriodSeconds = null)
    {
        return await Task.Run(() =>
        {
            var encryptedPassword = EncryptField(masterKey, password);
            var encryptedNotes = string.IsNullOrEmpty(notes) ? null : EncryptField(masterKey, notes);
            // 隱藏時明文欄位清空、只留密文；取消隱藏則反過來，密文欄位不留舊值——切換方向
            // 由呼叫端每次都帶完整的 username 決定，不用另外判斷「有沒有變更」。
            var plaintextUsername = usernameHidden ? "" : username;
            var encryptedUsername = usernameHidden ? EncryptField(masterKey, username) : null;
            var encryptedTotp = updateTotp && !string.IsNullOrEmpty(totpSecret)
                ? EncryptField(masterKey, JsonSerializer.Serialize(new
                {
                    secret = totpSecret,
                    algorithm = totpAlgorithm ?? "SHA1",
                    digits = totpDigits ?? 6,
                    period = totpPeriodSeconds ?? 30
                }))
                : null;
            var now = DateTime.UtcNow;
            string resultId = id ?? "";

            // Mutate（不是 Load()／Save() 各自呼叫）：整段「找既有紀錄→改欄位/新增」都要在
            // 同一把鎖底下完成，不然 WebView2 主視窗跟瀏覽器擴充功能兩條 IPC 通道可能各自
            // Load 到同一份舊資料、各自算完新值才 Save，後寫的會整份覆蓋掉先寫的那筆變更
            // （見 PasswordLockerStore.Mutate 上的說明）。
            _store.Mutate(data =>
            {
                var existing = id is not null ? data.Entries.FirstOrDefault(e => e.Id == id) : null;
                if (existing is not null)
                {
                    existing.Category = category;
                    existing.Title = title;
                    existing.AssociatedDomains = domains.ToList();
                    existing.UsernameHidden = usernameHidden;
                    existing.Username = plaintextUsername;
                    existing.EncryptedUsernameBase64 = encryptedUsername;
                    existing.EncryptedPasswordBase64 = encryptedPassword;
                    existing.EncryptedNotesBase64 = encryptedNotes;
                    existing.LinkedVaultItemUuid = linkedVaultItemUuid;
                    if (updateTotp)
                    {
                        existing.EncryptedTotpSecretBase64 = encryptedTotp;
                    }
                    existing.UpdatedAtUtc = now;
                    resultId = existing.Id;
                    return;
                }

                var entry = new PasswordCredentialEntry
                {
                    Category = category,
                    Title = title,
                    AssociatedDomains = domains.ToList(),
                    UsernameHidden = usernameHidden,
                    Username = plaintextUsername,
                    EncryptedUsernameBase64 = encryptedUsername,
                    EncryptedPasswordBase64 = encryptedPassword,
                    EncryptedNotesBase64 = encryptedNotes,
                    LinkedVaultItemUuid = linkedVaultItemUuid,
                    EncryptedTotpSecretBase64 = updateTotp ? encryptedTotp : null,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                data.Entries.Add(entry);
                resultId = entry.Id;
            });

            return new PasswordLockerEntryResult(true, resultId);
        });
    }

    public async Task<PasswordLockerDecryptedPasswordResult> GetDecryptedPasswordAsync(string id, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return new PasswordLockerDecryptedPasswordResult(false, null, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }

            var plaintext = DecryptField(masterKey, entry.EncryptedPasswordBase64);
            try
            {
                return new PasswordLockerDecryptedPasswordResult(true, Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        });
    }

    /// <summary>瀏覽器擴充功能專用（規劃文件第 5 節）：跟 <see cref="GetDecryptedPasswordAsync"/>
    /// 的差異只有多一道「這筆憑證真的關聯這個網域」的檢查——2026-08-09 這輪稽核發現
    /// RevealCredentialForSiteAsync 原本只檢查兩個 session 計時器有沒有過期，從沒比對過
    /// entry.AssociatedDomains 有沒有包含呼叫端宣稱的 domain，等於「對任何一個網站驗證過一次，
    /// 就能用那個網站的 session 讀出密碼庫裡任何一筆密碼」。回傳的錯誤碼統一用
    /// EntryNotFound（不是另外開一個「網域不符」的錯誤碼），避免變成可以拿來判斷「這個 id
    /// 存不存在」的探測 oracle——不管是 id 打錯、還是 id 存在但網域對不上，前端看到的都是
    /// 同一種「查無這筆」。</summary>
    public async Task<PasswordLockerDecryptedPasswordResult> GetDecryptedPasswordForDomainAsync(string id, string domain, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null || !entry.AssociatedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            {
                return new PasswordLockerDecryptedPasswordResult(false, null, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }

            var plaintext = DecryptField(masterKey, entry.EncryptedPasswordBase64);
            try
            {
                return new PasswordLockerDecryptedPasswordResult(true, Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        });
    }

    /// <summary>距離上一次真的完成一次完整驗證（見 RecordAppSessionVerified）是否還在 TOTP
    /// 揭露要求的新鮮度視窗內——跟 app session／site session 的逾時分鐘數完全獨立，是刻意
    /// 更嚴格的一道額外檢查，見類別開頭 TotpRevealFreshnessWindow 宣告處的說明。</summary>
    public bool IsWithinTotpRevealFreshnessWindow(DateTime? now = null)
        => _lastFullVerificationUtc is not null
            && (now ?? DateTime.UtcNow) - _lastFullVerificationUtc.Value <= TotpRevealFreshnessWindow;

    /// <summary>解密後的內容是 JSON（見 PasswordCredentialEntry.EncryptedTotpSecretBase64 上的
    /// 說明），這裡負責解出 secret／algorithm／digits／period 四個欄位——不在這裡做新鮮度視窗
    /// 檢查（那是授權層級的事，見 PasswordLockerProtocolHandlers.RevealTotpAsync 呼叫
    /// IsWithinTotpRevealFreshnessWindow 的地方），這個方法只管「有沒有 TOTP、解不解得開」。</summary>
    public async Task<PasswordLockerDecryptedTotpResult> GetDecryptedTotpAsync(string id, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry?.EncryptedTotpSecretBase64 is null)
            {
                return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: "這筆紀錄沒有設定 TOTP", ErrorCode: ErrorCodes.PasswordLockerTotpNotConfigured);
            }

            var plaintext = DecryptField(masterKey, entry.EncryptedTotpSecretBase64);
            try
            {
                using var doc = JsonDocument.Parse(plaintext);
                var root = doc.RootElement;
                return new PasswordLockerDecryptedTotpResult(
                    true,
                    root.GetProperty("secret").GetString(),
                    root.GetProperty("algorithm").GetString(),
                    root.GetProperty("digits").GetInt32(),
                    root.GetProperty("period").GetInt32());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        });
    }

    /// <summary>瀏覽器擴充功能專用，跟 GetDecryptedPasswordForDomainAsync 同一個道理——多一道
    /// 「這筆憑證真的關聯這個網域」的檢查，錯誤碼統一用 TotpNotConfigured／EntryNotFound
    /// 而不是另開一個「網域不符」的錯誤碼，避免變成探測 oracle。</summary>
    public async Task<PasswordLockerDecryptedTotpResult> GetDecryptedTotpForDomainAsync(string id, string domain, byte[] masterKey)
    {
        return await Task.Run(async () =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null || !entry.AssociatedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            {
                return new PasswordLockerDecryptedTotpResult(false, ErrorMessage: "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }
            return await GetDecryptedTotpAsync(id, masterKey);
        });
    }

    /// <summary>沒隱藏的帳號直接回傳 entry.Username（本來就是明文，不需要解密），只有
    /// UsernameHidden 的憑證才真的走 DecryptField——呼叫端（點擊帳號欄位的顯示手勢、編輯表單）
    /// 不用自己先判斷是否隱藏，統一呼叫這個方法就能拿到目前應該顯示的帳號值。</summary>
    public async Task<PasswordLockerDecryptedUsernameResult> GetDecryptedUsernameAsync(string id, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return new PasswordLockerDecryptedUsernameResult(false, null, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }

            if (!entry.UsernameHidden || entry.EncryptedUsernameBase64 is null)
            {
                return new PasswordLockerDecryptedUsernameResult(true, entry.Username);
            }

            var plaintext = DecryptField(masterKey, entry.EncryptedUsernameBase64);
            try
            {
                return new PasswordLockerDecryptedUsernameResult(true, Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        });
    }

    /// <summary>沒存過備註（EncryptedNotesBase64 為 null）直接回傳空字串，不需要走解密——
    /// 呼叫端（編輯表單開啟時）不用自己先判斷有沒有備註，統一呼叫這個方法就能拿到目前
    /// 應該顯示的備註值，跟 GetDecryptedUsernameAsync 的既有慣例一致。</summary>
    public async Task<PasswordLockerDecryptedNotesResult> GetDecryptedNotesAsync(string id, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return new PasswordLockerDecryptedNotesResult(false, null, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }

            if (entry.EncryptedNotesBase64 is null)
            {
                return new PasswordLockerDecryptedNotesResult(true, "");
            }

            var plaintext = DecryptField(masterKey, entry.EncryptedNotesBase64);
            try
            {
                return new PasswordLockerDecryptedNotesResult(true, Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        });
    }

    public async Task<IReadOnlyList<PasswordCredentialMetadata>> ListCredentialsMetadataAsync()
        => await Task.Run(() => _store.Load().Entries.Select(ToMetadata).ToList());

    public async Task<PasswordLockerResult> DeleteCredentialAsync(string id)
    {
        return await Task.Run(() =>
        {
            var removed = 0;
            _store.Mutate(data => removed = data.Entries.RemoveAll(e => e.Id == id));
            return removed == 0
                ? new PasswordLockerResult(false, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound)
                : new PasswordLockerResult(true);
        });
    }

    /// <summary>供瀏覽器擴充功能查詢用：不需要解鎖就能查，只比對明文的 AssociatedDomains
    /// （見規劃文件第 5 節「必須在使用者驗證身份之前就能比對」）。</summary>
    public async Task<IReadOnlyList<PasswordCredentialMetadata>> FindCredentialsForDomainAsync(string domain)
        => await Task.Run(() => _store.Load().Entries
            .Where(e => e.AssociatedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            .Select(ToMetadata)
            .ToList());

    /// <summary>「已加密檔案」類別的自我修復：對應項目消失時標題加刪除線＋標示來源消失
    /// （規劃文件第 4 節），不刪除這筆憑證。用委派而非直接依賴 VaultIndexCache，避免 Core
    /// 內部子系統互相硬依賴（比照 LockService 建構子接收 getGuardedFolderPaths 委派的既有模式）。</summary>
    public async Task<IReadOnlyList<string>> CheckLinkedVaultItemsAsync(Func<string, bool> vaultItemExists)
    {
        return await Task.Run(() =>
        {
            var flagged = new List<string>();
            var anyChanged = false;

            // 這個方法在「每次列清單前」都會被呼叫（見方法開頭說明），大部分時候沒有任何項目
            // 的 SourceDeleted 狀態真的改變——先算出 anyChanged，沒有變化就不呼叫 Mutate，
            // 避免每次開清單頁都對磁碟寫入一次。Mutate 內部會重新從磁碟讀一次最新資料才套用
            // 變更，這裡外層算出的 flagged／anyChanged 只是「要不要寫」的預先判斷，不是最終
            // 寫入的資料來源。
            foreach (var entry in _store.Load().Entries)
            {
                if (entry.Category != CredentialCategory.EncryptedFile || entry.LinkedVaultItemUuid is null)
                {
                    continue;
                }

                var sourceDeleted = !vaultItemExists(entry.LinkedVaultItemUuid);
                if (sourceDeleted != entry.SourceDeleted)
                {
                    anyChanged = true;
                }
                if (sourceDeleted)
                {
                    flagged.Add(entry.Id);
                }
            }

            if (anyChanged)
            {
                _store.Mutate(data =>
                {
                    foreach (var entry in data.Entries)
                    {
                        if (entry.Category != CredentialCategory.EncryptedFile || entry.LinkedVaultItemUuid is null)
                        {
                            continue;
                        }
                        entry.SourceDeleted = !vaultItemExists(entry.LinkedVaultItemUuid);
                    }
                });
            }

            return flagged;
        });
    }

    // ---- 自動填入 session（每網站獨立、固定到期時間，見規劃文件第 3 節）----
    // 這裡的「每網站獨立」是真的：每個網域各自一個計時起點。但不是嚴格意義上的滑動視窗
    // （sliding window，每次成功存取都會順便延長到期時間）——RecordSiteVerified 只在
    // 驗證通過那一刻設定一次到期基準，之後單純查詢（IsSiteSessionValid／
    // RevealCredentialForSiteAsync）不會延後它，逾時就要重新走一次驗證。2026-08-09 這輪
    // 稽核發現先前文件用「滑動視窗」描述這裡，跟實作不符，容易誤導以為頻繁使用就不會過期。

    /// <summary>now 參數只給測試用來注入固定時間，正式呼叫端不用帶，預設用目前時間。
    /// 順便清掉已經過期的舊網域記錄——不這樣做的話 <see cref="_siteSessions"/> 只會隨著
    /// 造訪過的網站數量無限成長，同一個長時間執行的行程（App 常駐系統匣時常見）逐漸累積
    /// 一堆再也用不到的過期網域字串（2026-08-09 這輪稽核發現的缺口，量體很小但確實無界）。</summary>
    public void RecordSiteVerified(string domain, DateTime? now = null)
    {
        var current = now ?? DateTime.UtcNow;
        _siteSessions[domain] = current;

        var timeoutMinutes = _store.Load().SessionTimeoutMinutes;
        var expiredDomains = _siteSessions
            .Where(kv => current - kv.Value > TimeSpan.FromMinutes(timeoutMinutes))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var expired in expiredDomains)
        {
            _siteSessions.Remove(expired);
        }
    }

    public bool IsSiteSessionValid(string domain, DateTime? now = null)
    {
        if (!_siteSessions.TryGetValue(domain, out var lastVerified))
        {
            return false;
        }

        var current = now ?? DateTime.UtcNow;
        var timeoutMinutes = _store.Load().SessionTimeoutMinutes;
        return current - lastVerified <= TimeSpan.FromMinutes(timeoutMinutes);
    }

    // ---- App 分頁內驗證 session ----

    /// <summary>now 參數只給測試用來注入固定時間，正式呼叫端不用帶。呼叫端驗證成功後呼叫，
    /// 之後 reveal/新增編輯/刪除等需要主金鑰的操作改用 <see cref="TryGetAppSessionMasterKey"/>
    /// 取得，不用每次都重新要求輸入密碼。</summary>
    public void RecordAppSessionVerified(byte[] masterKey, DateTime? now = null)
    {
        if (_appSessionMasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(_appSessionMasterKey);
        }
        _appSessionMasterKey = masterKey;
        var current = now ?? DateTime.UtcNow;
        _appSessionExpiresUtc = current.AddMinutes(_store.Load().SessionTimeoutMinutes);
        // TOTP 揭露要求的新鮮度視窗跟一般 app session 是分開的兩件事——這裡是「剛剛真的完成
        // 一次完整驗證」的時間戳，不會因為 app session 續期（例如密碼欄位在逾時前又被存取一次）
        // 而更新，只會在真正重新走一次密碼／Passkey／恢復金鑰驗證流程時更新。VerifyAsync／
        // VerifyByRecoveryKeyAsync 成功時（見 PasswordLockerProtocolHandlers）都會呼叫到這裡，
        // 是 App 分頁跟瀏覽器驗證視窗共用的同一個入口，兩邊都涵蓋到。
        _lastFullVerificationUtc = current;
    }

    /// <summary>沒驗證過或已逾時回傳 null。回傳值是 <see cref="_appSessionMasterKey"/> 的參考，
    /// 呼叫端不應該自行清零這份資料——生命週期由這個 service 統一管理。</summary>
    public byte[]? TryGetAppSessionMasterKey(DateTime? now = null)
    {
        if (_appSessionMasterKey is null || _appSessionExpiresUtc is null)
        {
            return null;
        }

        var current = now ?? DateTime.UtcNow;
        if (current > _appSessionExpiresUtc.Value)
        {
            ClearAppSession();
            return null;
        }

        return _appSessionMasterKey;
    }

    /// <summary>主動登出或 App 關閉時呼叫。</summary>
    public void ClearAppSession()
    {
        if (_appSessionMasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(_appSessionMasterKey);
        }
        _appSessionMasterKey = null;
        _appSessionExpiresUtc = null;
    }

    // ---- 密碼強度／重複使用提示（規劃文件第 6 節，純資訊性、不阻擋儲存）----

    // 只涵蓋最常見、猜測成本極低的密碼——不是完整的外洩密碼資料庫比對（那需要連網查詢或內建
    // 一份龐大字典，超出這個純資訊性提示的目的）。比對前先正規化（見 NormalizeForCommonPasswordCheck），
    // 所以「P@ssw0rd」這種常見符號替代寫法也抓得到，不會因為湊到多種字元類型就被誤判成安全。
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "password", "password1", "123456", "12345678", "123456789", "1234567890",
        "qwerty", "qwerty123", "letmein", "admin", "iloveyou", "welcome", "monkey",
        "dragon", "abc123", "111111", "000000", "football", "baseball", "sunshine",
        "princess", "trustno1", "master", "superman", "batman"
    };

    /// <summary>常見密碼比對用的正規化：轉小寫＋還原常見的「看起來比較安全」符號替代寫法
    /// （0→o、1→l、3→e、4→a、5→s、7→t、@→a、$→s），只用來跟 <see cref="CommonPasswords"/>
    /// 比對，不影響使用者實際儲存的密碼內容本身。</summary>
    private static string NormalizeForCommonPasswordCheck(string password)
    {
        var sb = new StringBuilder(password.Length);
        foreach (var c in password)
        {
            sb.Append(char.ToLowerInvariant(c) switch
            {
                '0' => 'o',
                '1' => 'l',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                '$' => 's',
                var lower => lower
            });
        }
        return sb.ToString();
    }

    /// <summary>連續遞增/遞減的字元（字母或數字皆可，例如 "abcd"、"4321"）達到門檻長度，
    /// 或同一個字元重複達到門檻次數——這兩種都是「有規律性、好猜」的典型模式，單純看字元
    /// 類型數量抓不出來。</summary>
    private static bool HasWeakPattern(string password, int runThreshold = 4)
    {
        var ascendingRun = 1;
        var descendingRun = 1;
        var repeatRun = 1;

        for (var i = 1; i < password.Length; i++)
        {
            var diff = password[i] - password[i - 1];

            ascendingRun = diff == 1 ? ascendingRun + 1 : 1;
            descendingRun = diff == -1 ? descendingRun + 1 : 1;
            repeatRun = diff == 0 ? repeatRun + 1 : 1;

            if (ascendingRun >= runThreshold || descendingRun >= runThreshold || repeatRun >= runThreshold)
            {
                return true;
            }
        }

        return false;
    }

    public static PasswordStrength EstimateStrength(string password)
    {
        if (CommonPasswords.Contains(NormalizeForCommonPasswordCheck(password)) || HasWeakPattern(password))
        {
            return PasswordStrength.Weak;
        }

        var hasLower = password.Any(char.IsLower);
        var hasUpper = password.Any(char.IsUpper);
        var hasDigit = password.Any(char.IsDigit);
        var hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));
        var variety = new[] { hasLower, hasUpper, hasDigit, hasSymbol }.Count(x => x);

        if (password.Length < 8 || variety < 3)
        {
            return PasswordStrength.Weak;
        }

        return password.Length >= 16 ? PasswordStrength.Strong : PasswordStrength.Medium;
    }

    /// <summary>只比對使用者自己密碼庫裡的資料，不涉及任何連網查詢或外部外洩資料庫比對。</summary>
    public async Task<IReadOnlyList<string>> FindEntriesReusingPasswordAsync(string password, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var matches = new List<string>();
            foreach (var entry in _store.Load().Entries)
            {
                var plaintext = DecryptField(masterKey, entry.EncryptedPasswordBase64);
                try
                {
                    if (Encoding.UTF8.GetString(plaintext) == password)
                    {
                        matches.Add(entry.Id);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            return matches;
        });
    }

    /// <summary>清單頁搜尋框比對備註內容用——標題/帳號/網域是明文，前端可以直接比對，備註是
    /// 加密欄位，只能在拿得到主金鑰時由這裡解密比對，不區分大小寫。</summary>
    public async Task<IReadOnlyList<string>> FindEntriesWithNotesContainingAsync(string query, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var matches = new List<string>();
            foreach (var entry in _store.Load().Entries)
            {
                if (entry.EncryptedNotesBase64 is null)
                {
                    continue;
                }

                var plaintext = DecryptField(masterKey, entry.EncryptedNotesBase64);
                try
                {
                    if (Encoding.UTF8.GetString(plaintext).Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(entry.Id);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            return matches;
        });
    }

    // ---- 密碼產生器 ----

    private const string AlphanumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{}";

    public static string GeneratePassword(int length, bool includeSymbols)
    {
        var choices = includeSymbols ? AlphanumericChars + SymbolChars : AlphanumericChars;
        return RandomNumberGenerator.GetString(choices, length);
    }

    // ---- CSV 匯出（規劃文件第 7 節：密碼忘記＋Passkey／恢復金鑰都用不了時的最後自救手段）----

    public async Task<string> ExportToCsvAsync(byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("title,domains,username,password,notes");

            foreach (var entry in _store.Load().Entries)
            {
                var passwordBytes = DecryptField(masterKey, entry.EncryptedPasswordBase64);
                var notesBytes = entry.EncryptedNotesBase64 is not null
                    ? DecryptField(masterKey, entry.EncryptedNotesBase64)
                    : null;
                // CSV 匯出是「密碼／Passkey／恢復金鑰都用不了時的最後自救手段」（見規劃文件第 7 節），
                // 拿到主金鑰就代表已經通過完整驗證，這裡不再區分帳號有沒有勾選隱藏，一律解密到明文。
                var usernameBytes = entry is { UsernameHidden: true, EncryptedUsernameBase64: not null }
                    ? DecryptField(masterKey, entry.EncryptedUsernameBase64)
                    : null;
                try
                {
                    var password = Encoding.UTF8.GetString(passwordBytes);
                    var notes = notesBytes is not null ? Encoding.UTF8.GetString(notesBytes) : "";
                    var username = usernameBytes is not null ? Encoding.UTF8.GetString(usernameBytes) : entry.Username;

                    sb.AppendLine(string.Join(",",
                        CsvEscape(entry.Title),
                        CsvEscape(string.Join(";", entry.AssociatedDomains)),
                        CsvEscape(username),
                        CsvEscape(password),
                        CsvEscape(notes)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(passwordBytes);
                    if (notesBytes is not null) CryptographicOperations.ZeroMemory(notesBytes);
                    if (usernameBytes is not null) CryptographicOperations.ZeroMemory(usernameBytes);
                }
            }

            return sb.ToString();
        });
    }

    // Excel／Google 試算表開啟 CSV 時，儲存格內容以 =、+、-、@ 開頭會被當成公式執行
    // （CSV/公式注入，OWASP 已知的一類問題）——密碼庫的標題／帳號／密碼／備註都是使用者
    // 自己輸入的內容，攻擊者可以刻意把惡意公式存成密碼欄位，等使用者哪天匯出 CSV 拿去
    // Excel 開啟時觸發。在這類開頭字元前面補一個單引號，讓試算表軟體當成純文字處理，
    // 不影響匯入回密碼庫時的內容（ImportFromCsvAsync／ParseCsvLine 不會特別去掉這個字元，
    // 但這本來就是使用者自己存進去的原始輸入，不算是額外遺失資訊）。
    private static readonly char[] FormulaInjectionPrefixes = ['=', '+', '-', '@'];

    private static string CsvEscape(string value)
    {
        if (value.Length > 0 && FormulaInjectionPrefixes.Contains(value[0]))
        {
            value = "'" + value;
        }
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    // ---- CSV 匯入（規劃文件第 7 節：支援自己的匯出格式，也支援 Chrome／Edge 匯出格式，
    // 降低「一開始要手動輸入幾十組密碼才能享受到自動填入好處」的採用門檻）----

    /// <summary>逐行載入、批次寫入一次（跟 AddOrUpdateCredentialAsync 每次呼叫各自 Load/Save
    /// 不同）——匯入情境本來就是一次處理幾十筆，批次寫入避免重複 IO。全部建成 Website 分類，
    /// 「已加密檔案」憑證不會出現在瀏覽器匯出的密碼 CSV 裡，第一版匯入不處理那個分類。
    /// 標題欄位（"title"／"name"）留空時交給前端既有的「從關聯網站即時組字」邏輯顯示，
    /// 這裡不用另外補預設標題。</summary>
    public async Task<PasswordLockerCsvImportResult> ImportFromCsvAsync(string csv, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var lines = csv.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            {
                return new PasswordLockerCsvImportResult(false, ErrorMessage: "CSV 內容是空的", ErrorCode: ErrorCodes.PasswordLockerCsvInvalidFormat);
            }

            var header = ParseCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
            var titleIdx = FindColumnIndex(header, "title", "name");
            var domainIdx = FindColumnIndex(header, "domains", "domain");
            var urlIdx = FindColumnIndex(header, "url", "login_uri");
            var usernameIdx = FindColumnIndex(header, "username", "user");
            var passwordIdx = FindColumnIndex(header, "password");
            var notesIdx = FindColumnIndex(header, "notes", "note");

            if (passwordIdx < 0 || (domainIdx < 0 && urlIdx < 0))
            {
                return new PasswordLockerCsvImportResult(false, ErrorMessage: "CSV 格式不正確，找不到密碼或網址／關聯網站欄位", ErrorCode: ErrorCodes.PasswordLockerCsvInvalidFormat);
            }

            var now = DateTime.UtcNow;
            var newEntries = new List<PasswordCredentialEntry>();
            var skippedCount = 0;

            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                var fields = ParseCsvLine(lines[i]);
                var password = GetField(fields, passwordIdx);
                if (string.IsNullOrEmpty(password))
                {
                    skippedCount++;
                    continue;
                }

                var title = titleIdx >= 0 ? GetField(fields, titleIdx) : "";
                var domains = domainIdx >= 0
                    ? GetField(fields, domainIdx).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : ExtractDomainsFromUrlField(GetField(fields, urlIdx));
                var username = usernameIdx >= 0 ? GetField(fields, usernameIdx) : "";
                var notes = notesIdx >= 0 ? GetField(fields, notesIdx) : "";

                newEntries.Add(new PasswordCredentialEntry
                {
                    Category = CredentialCategory.Website,
                    Title = title,
                    AssociatedDomains = domains,
                    Username = username,
                    EncryptedPasswordBase64 = EncryptField(masterKey, password),
                    EncryptedNotesBase64 = string.IsNullOrEmpty(notes) ? null : EncryptField(masterKey, notes),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            // 每一筆新項目都已經算好、不依賴目前資料庫的既有內容（只是要 append 進去），
            // Mutate 把「重新讀最新一份 → 附加 → 寫回」整段包在同一把鎖裡，避免匯入途中
            // 剛好有其他來源（另一個 IPC 通道）也在寫入而互相覆蓋掉。
            if (newEntries.Count > 0)
            {
                _store.Mutate(data => data.Entries.AddRange(newEntries));
            }

            return new PasswordLockerCsvImportResult(true, newEntries.Count, skippedCount);
        });
    }

    private static int FindColumnIndex(IReadOnlyList<string> header, params string[] candidateNames)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (candidateNames.Contains(header[i]))
            {
                return i;
            }
        }
        return -1;
    }

    private static string GetField(IReadOnlyList<string> fields, int index)
        => index >= 0 && index < fields.Count ? fields[index] : "";

    private static List<string> ExtractDomainsFromUrlField(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return [];
        }
        return [Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url];
    }

    /// <summary>簡單的 RFC 4180 風格解析：支援雙引號包住的欄位（可含逗號／換行），雙引號本身
    /// 用連續兩個雙引號跳脫——跟 CsvEscape 的匯出格式對稱，也涵蓋 Chrome／Edge 匯出格式常見的
    /// 這種寫法。不支援欄位內夾雜未跳脫換行符號橫跨多行的極端情況，匯入來源是正常的密碼管理器
    /// 匯出檔，這種情況不預期出現。</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    // ---- 內部輔助 ----

    private static PasswordCredentialMetadata ToMetadata(PasswordCredentialEntry entry)
        => new(entry.Id, entry.Category, entry.Title, entry.AssociatedDomains, entry.Username, entry.UsernameHidden,
            entry.LinkedVaultItemUuid, entry.SourceDeleted, entry.CreatedAtUtc, entry.UpdatedAtUtc,
            entry.EncryptedTotpSecretBase64 is not null);

    /// <summary>跟 RecoveryKeyProtector.WrapContentKey 內部用的 nonce+tag+ciphertext 串接格式一致。</summary>
    private static string EncryptField(byte[] masterKey, string plaintext)
    {
        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(masterKey, Encoding.UTF8.GetBytes(plaintext));
        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(combined);
    }

    private static byte[] DecryptField(byte[] masterKey, string base64)
    {
        var combined = Convert.FromBase64String(base64);
        var nonce = combined.AsSpan(0, AesGcmCipher.NonceSizeBytes);
        var tag = combined.AsSpan(AesGcmCipher.NonceSizeBytes, AesGcmCipher.TagSizeBytes);
        var ciphertext = combined.AsSpan(AesGcmCipher.NonceSizeBytes + AesGcmCipher.TagSizeBytes);
        return AesGcmCipher.Decrypt(masterKey, nonce, ciphertext, tag);
    }
}
