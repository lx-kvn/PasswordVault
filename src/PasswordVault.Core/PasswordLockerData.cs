namespace PasswordVault.Core;

/// <summary>
/// 對應規劃文件（FileLocker_密碼庫_功能規劃.md）：獨立於加密 Vault、資料夾防護之外的第三套
/// 憑證儲存，credentials.json 的完整內容。三條解鎖路徑（密碼／Passkey／恢復金鑰）最終都是為了
/// 拿到同一把 Locker 主金鑰，用來加解密 <see cref="PasswordCredentialEntry"/> 裡的密碼／備註。
/// </summary>
public class PasswordLockerData
{
    /// <summary>密碼驗證用（Argon2id 分割金鑰模式，跟 Vault／資料夾防護同一套）。Locker 主金鑰
    /// 本身是獨立產生的隨機值，不是直接從密碼衍生——這裡的 Argon2 輸出只是拿來「包住」主金鑰的
    /// 包裝金鑰（PasswordWrappedMasterKeyBase64），跟 Passkey／恢復金鑰的 wrap/unwrap 模式對稱。
    /// 這樣改密碼（見 PasswordLockerService.ChangePasswordAsync）只需要重新包一次主金鑰，不用
    /// 重新加密每一筆憑證，Passkey／恢復金鑰包的也還是同一把主金鑰，不受影響。</summary>
    public string? PasswordSaltBase64 { get; set; }
    public string? PasswordVerificationHashBase64 { get; set; }
    public string? PasswordWrappedMasterKeyBase64 { get; set; }

    /// <summary>Passkey（裝置綁定），重用 PasskeyProtector 的完整 wrap/unwrap 流程——
    /// 密碼庫存的是真正要加密的內容，不是資料夾防護那種純驗證用法，需要真的把 Locker 主金鑰包起來。
    /// PasskeyChallengeBase64 是設定當下用來簽章、進而衍生包裝金鑰的那份挑戰資料，驗證時必須
    /// 重複使用同一份（不能每次都重新產生亂數），否則簽章結果不同、衍生出來的包裝金鑰跟著不同，
    /// UnwrapContentKey 一定會失敗——跟 Vault 的 LockService／VaultMetadata.PasskeyChallenge
    /// 是同一個道理，見 LockService.DecryptByPasskeyAsync。</summary>
    public bool PasskeyEnabled { get; set; }
    public string? PasskeyCredentialName { get; set; }
    public string? PasskeyChallengeBase64 { get; set; }
    public string? PasskeyWrappedMasterKeyBase64 { get; set; }

    /// <summary>恢復金鑰，重用 RecoveryKeyProtector 的 wrap/unwrap 模式，第三條獨立解鎖路徑。</summary>
    public bool RecoveryKeyEnabled { get; set; }
    public string? RecoveryKeyWrappedMasterKeyBase64 { get; set; }

    /// <summary>自動填入的驗證有效期：每個網站獨立計時、滑動視窗，這裡只存逾時分鐘數，
    /// 實際的「網站→上次驗證時間」對應表是執行期記憶體狀態，不持久化（見 PasswordLockerService）。</summary>
    public int SessionTimeoutMinutes { get; set; } = 1;

    public List<PasswordCredentialEntry> Entries { get; set; } = new();
}

public enum CredentialCategory
{
    Website,
    EncryptedFile
}

/// <summary>
/// 密碼庫裡的一筆憑證。AssociatedDomains／Title 刻意不加密——瀏覽器分頁載入網站時要能在使用者
/// 驗證身份之前就比對「有沒有存過這個網站的憑證」，否則每個網站都得先驗證才知道有沒有存過，
/// 變相強迫每次都要驗證，違背「不打擾」的設計（見規劃文件第 5 節）。這個比對只依賴
/// AssociatedDomains，不依賴 Username，所以 Username 可以額外提供「隱藏」選項（見
/// UsernameHidden）而不影響這個既有設計。EncryptedPasswordBase64／EncryptedNotesBase64
/// 一律是需要保護的機密內容。
/// </summary>
public class PasswordCredentialEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CredentialCategory Category { get; set; }

    /// <summary>Website 類別是使用者自訂標題；EncryptedFile 類別是對應 Vault 項目的檔名，
    /// 來源項目消失後這個欄位變成唯一的識別依據（見規劃文件第 4 節「已加密檔案」類別）。</summary>
    public string Title { get; set; } = "";

    public List<string> AssociatedDomains { get; set; } = new();

    /// <summary>UsernameHidden 為 true 時，這裡固定是空字串——真正的帳號值只存在
    /// EncryptedUsernameBase64 裡，跟密碼欄位一樣用 Locker 主金鑰加密。這是使用者自願放棄
    /// 「不驗證就能瀏覽帳號」這個好處、換取「檔案本身也看不到帳號」的個別選項，預設關閉
    /// （見規劃討論：多數帳號不需要，只有少數敏感帳號才會特地勾選）。</summary>
    public bool UsernameHidden { get; set; }
    public string Username { get; set; } = "";
    public string? EncryptedUsernameBase64 { get; set; }

    public string EncryptedPasswordBase64 { get; set; } = "";
    public string? EncryptedNotesBase64 { get; set; }

    /// <summary>只有 EncryptedFile 類別使用，對應 Vault 項目的 UUID。</summary>
    public string? LinkedVaultItemUuid { get; set; }

    /// <summary>由 PasswordLockerService.CheckLinkedVaultItemsAsync 維護——LinkedVaultItemUuid
    /// 對應的 Vault 項目已經消失時設為 true，UI 層依此顯示刪除線＋標示來源消失，不刪除這筆憑證
    /// （見規劃文件第 4 節）。</summary>
    public bool SourceDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>只有 Website 分類使用。解密後是 JSON：
    /// <c>{"secret":"BASE32...","algorithm":"SHA1","digits":6,"period":30}</c>——algorithm／
    /// digits／period 雖然不是機密本身，但一起包進這個加密欄位，不另外開明文欄位，避免未驗證
    /// 查詢（FindCredentialsForDomainAsync／ListCredentialsMetadataAsync）多洩漏一點這筆憑證的
    /// TOTP 設定細節。有沒有設定 TOTP（這個欄位是不是 null）本身會透過 metadata 的 HasTotp
    /// 布林值對外可見，跟 UsernameHidden 的既有設計同一個道理——「有沒有」可以公開，「內容」
    /// 才是機密。</summary>
    public string? EncryptedTotpSecretBase64 { get; set; }
}
