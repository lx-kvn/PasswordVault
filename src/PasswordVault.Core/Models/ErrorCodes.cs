namespace PasswordVault.Core.Models;

/// <summary>
/// 固定的錯誤代碼字串常數，前端依這個代碼查對應語言的翻譯句子範本。
///
/// 從 FileLocker repo 的 src/FileLocker.Core/Models/ErrorCodes.cs 複製過來的獨立副本，只挑出
/// PasswordVault.Core 實際用得到的「PasswordLocker*」那組——原始檔案還混了 Vault 加密、資料夾防護
/// 等其他功能的錯誤碼，PasswordVault 是獨立產品不會用到那些，不整份複製過來避免混淆。
/// 常數名稱／字串值都維持原樣不改，跟 KeyDerivation.cs 的 HKDF info 字串是同樣的相容性考量。
/// </summary>
public static class ErrorCodes
{
    public const string PasswordLockerNotConfigured = "PASSWORD_LOCKER_NOT_CONFIGURED";
    public const string PasswordLockerPasswordIncorrect = "PASSWORD_LOCKER_PASSWORD_INCORRECT";
    public const string PasswordLockerPasskeyNotEnabled = "PASSWORD_LOCKER_PASSKEY_NOT_ENABLED";
    public const string PasswordLockerPasskeyFailed = "PASSWORD_LOCKER_PASSKEY_FAILED";
    public const string PasswordLockerRecoveryKeyNotEnabled = "PASSWORD_LOCKER_RECOVERY_KEY_NOT_ENABLED";
    public const string PasswordLockerRecoveryKeyInvalidFormat = "PASSWORD_LOCKER_RECOVERY_KEY_INVALID_FORMAT";
    public const string PasswordLockerRecoveryKeyIncorrect = "PASSWORD_LOCKER_RECOVERY_KEY_INCORRECT";
    public const string PasswordLockerLockedOut = "PASSWORD_LOCKER_LOCKED_OUT";
    public const string PasswordLockerEntryNotFound = "PASSWORD_LOCKER_ENTRY_NOT_FOUND";
    public const string PasswordLockerNotVerified = "PASSWORD_LOCKER_NOT_VERIFIED";
    public const string PasswordLockerCsvInvalidFormat = "PASSWORD_LOCKER_CSV_INVALID_FORMAT";
    public const string PasswordLockerTotpNotConfigured = "PASSWORD_LOCKER_TOTP_NOT_CONFIGURED";
}
