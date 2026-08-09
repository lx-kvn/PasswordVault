using FileLocker.Core.Models;
using FileLocker.Core.Security;
using FileLocker.PasswordLocker;

namespace FileLocker.PasswordLocker.Tests;

/// <summary>
/// 比照 VaultProtocolHandlersTests：驗證「解析請求 → 呼叫 Core 業務邏輯 → 組裝回應」這一層，
/// 不依賴任何 WPF／WebView2 型別。這裡額外驗證一件 VaultProtocolHandlers 沒有的東西——
/// Locker 主金鑰只留在 PasswordLockerService 的 app session 記憶體裡，VerifyAsync 的回應
/// 不能把 MasterKey 洩漏給前端呼叫端（見 PasswordLockerVerifyResponse 沒有 MasterKey 欄位）。
/// </summary>
public class PasswordLockerProtocolHandlersTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly PasswordLockerService _service;
    private readonly PasswordLockerProtocolHandlers _handlers;
    private bool _vaultItemExists = true;

    public PasswordLockerProtocolHandlersTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerPasswordLockerProtocolHandlersTests_");
        var store = new PasswordLockerStore(Path.Combine(_tempDir.FullName, "credentials.json"));
        var lockoutTracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
        _service = new PasswordLockerService(store, lockoutTracker);
        _handlers = new PasswordLockerProtocolHandlers(_service, uuid => _vaultItemExists);
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public async Task VerifyAsync_CorrectPassword_SucceedsWithoutExposingMasterKey()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.True(result.Success);
        // PasswordLockerVerifyResponse 刻意不含 MasterKey 欄位——編譯期就保證不會外洩，
        // 這裡用型別本身沒有這個屬性當斷言，而不是檢查某個欄位是 null。
    }

    [Fact]
    public async Task VerifyAsync_ThenAddOrUpdateCredential_UsesSessionWithoutPassword()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null);

        Assert.True(add.Success);
        Assert.NotNull(add.EntryId);
    }

    [Fact]
    public async Task AddOrUpdateCredentialAsync_WithoutPriorVerify_ReturnsNotVerifiedError()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null);

        Assert.False(add.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, add.ErrorCode);
    }

    [Fact]
    public async Task RevealPasswordAsync_WithoutPriorVerify_ReturnsNotVerifiedError()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _handlers.RevealPasswordAsync("some-id");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    [Fact]
    public async Task RevealPasswordAsync_AfterVerify_ReturnsDecryptedPassword()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null);

        var reveal = await _handlers.RevealPasswordAsync(add.EntryId!);

        Assert.True(reveal.Success);
        Assert.Equal("hunter2", reveal.Password);
    }

    [Fact]
    public async Task RevealNotesAsync_WithoutPriorVerify_ReturnsNotVerifiedError()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _handlers.RevealNotesAsync("some-id");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    [Fact]
    public async Task RevealNotesAsync_AfterVerify_ReturnsDecryptedNotes()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: "使用者名稱：小明", linkedVaultItemUuid: null);

        var reveal = await _handlers.RevealNotesAsync(add.EntryId!);

        Assert.True(reveal.Success);
        Assert.Equal("使用者名稱：小明", reveal.Notes);
    }

    [Fact]
    public async Task RevealUsernameAsync_WithoutPriorVerify_ReturnsNotVerifiedError()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _handlers.RevealUsernameAsync("some-id");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    [Fact]
    public async Task RevealUsernameAsync_AfterVerify_ReturnsDecryptedHiddenUsername()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "secret-user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null, usernameHidden: true);

        var reveal = await _handlers.RevealUsernameAsync(add.EntryId!);

        Assert.True(reveal.Success);
        Assert.Equal("secret-user@example.com", reveal.Username);
    }

    [Fact]
    public async Task AddOrUpdateCredentialAsync_UsernameHidden_ListedMetadataHasEmptyUsername()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "secret-user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null, usernameHidden: true);

        var list = await _handlers.ListCredentialsAsync();

        Assert.True(list[0].UsernameHidden);
        Assert.Equal("", list[0].Username);
    }

    [Fact]
    public async Task DeleteCredentialsAsync_WithoutPriorVerify_ReturnsNotVerifiedError()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _handlers.DeleteCredentialsAsync(["some-id"]);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    [Fact]
    public async Task DeleteCredentialsAsync_AfterVerify_RemovesAllGivenIds()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var first = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "A", domains: ["a.com"],
            username: "u", password: "p1", notes: null, linkedVaultItemUuid: null);
        var second = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "B", domains: ["b.com"],
            username: "u", password: "p2", notes: null, linkedVaultItemUuid: null);

        var result = await _handlers.DeleteCredentialsAsync([first.EntryId!, second.EntryId!]);
        var list = await _handlers.ListCredentialsAsync();

        Assert.True(result.Success);
        Assert.Empty(list);
    }

    [Fact]
    public async Task ListCredentialsAsync_DoesNotRequireVerification()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var list = await _handlers.ListCredentialsAsync();

        Assert.Empty(list);
    }

    [Fact]
    public async Task ListCredentialsAsync_RefreshesSourceDeletedForEncryptedFileEntries()
    {
        // CheckLinkedVaultItemsAsync 本身之前只有方法寫好、從沒被任何呼叫端接上，導致
        // 檔案解密後密碼庫清單裡的那筆密碼不會顯示刪除線——這裡驗證 ListCredentialsAsync
        // 現在會在回傳清單前自動跑一次這個自我修復檢查，不需要呼叫端另外記得呼叫
        // CheckLinkedVaultItemsAsync。
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.EncryptedFile, title: "報稅資料.zip",
            domains: [], username: "", password: "hunter2", notes: null,
            linkedVaultItemUuid: "some-uuid");

        _vaultItemExists = false;
        var list = await _handlers.ListCredentialsAsync();

        Assert.True(list[0].SourceDeleted);
    }

    [Fact]
    public async Task CheckLinkedVaultItemsAsync_UsesInjectedVaultItemExistsDelegate()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.EncryptedFile, title: "報稅資料.zip",
            domains: [], username: "", password: "hunter2", notes: null,
            linkedVaultItemUuid: "some-uuid");

        _vaultItemExists = false;
        var flagged = await _handlers.CheckLinkedVaultItemsAsync();

        Assert.Contains(add.EntryId, flagged);
    }

    [Fact]
    public void GeneratePassword_RespectsLength()
    {
        var password = PasswordLockerProtocolHandlers.GeneratePassword(24, includeSymbols: false);

        Assert.Equal(24, password.Length);
    }

    [Fact]
    public async Task SetupRecoveryKeyAsync_AfterVerify_ReturnsRecoveryKeyOnce()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        var result = await _handlers.SetupRecoveryKeyAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.RecoveryKey);
    }

    [Fact]
    public async Task SetupRecoveryKeyAsync_WithoutPriorVerify_ReturnsNotVerifiedError()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _handlers.SetupRecoveryKeyAsync();

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    // ---- 瀏覽器擴充功能專用：RevealCredentialForSiteAsync 的網域歸屬檢查 ----
    // 2026-08-09 這輪安全稽核發現的漏洞：原本的實作只檢查「網站 session 有沒有過期」跟
    // 「app session 主金鑰在不在」兩個計時器，從沒比對過這筆憑證的 AssociatedDomains 真的
    // 包不包含呼叫端宣稱的 domain。後果是：對任何一個網站驗證過一次，就能拿那個網站的
    // session 讀出密碼庫裡「其他任何一筆」密碼的明文——這幾個測試把攻擊流程直接寫成斷言，
    // 固定住修復後的行為，避免之後改動又悄悄退回沒有歸屬檢查的版本。

    [Fact]
    public async Task RevealCredentialForSiteAsync_DomainOwnsCredential_ReturnsPassword()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2",
            notes: null, linkedVaultItemUuid: null);
        _handlers.RecordSiteVerified("github.com");

        var result = await _handlers.RevealCredentialForSiteAsync(add.EntryId!, "github.com");

        Assert.True(result.Success);
        Assert.Equal("hunter2", result.Password);
    }

    [Fact]
    public async Task RevealCredentialForSiteAsync_DomainDoesNotOwnCredential_ReturnsNotFoundNotThePassword()
    {
        // 攻擊流程模擬：使用者曾經在 evil.com 上完整驗證過身份（例如那個網站也剛好存過一筆
        // 帳密），evil.com 因此有一份有效的網站 session。攻擊者接著嘗試用同一份 evil.com
        // session 去讀 github.com 那筆完全不相干的密碼——修復前這裡會直接回傳明文密碼，
        // 修復後必須被擋下來，回傳的錯誤碼要跟「id 打錯」一樣（EntryNotFound），不能讓
        // 攻擊者藉由不同的錯誤碼分辨出「這個 id 存在、只是網域不符」。
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var githubEntry = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2",
            notes: null, linkedVaultItemUuid: null);
        await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Evil",
            domains: ["evil.com"], username: "victim", password: "should-not-leak",
            notes: null, linkedVaultItemUuid: null);
        _handlers.RecordSiteVerified("evil.com");

        var result = await _handlers.RevealCredentialForSiteAsync(githubEntry.EntryId!, "evil.com");

        Assert.False(result.Success);
        Assert.Null(result.Password);
        Assert.Equal(ErrorCodes.PasswordLockerEntryNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RevealCredentialForSiteAsync_SiteSessionNotVerified_ReturnsNotVerifiedRegardlessOfDomainOwnership()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2",
            notes: null, linkedVaultItemUuid: null);
        // 故意不呼叫 RecordSiteVerified("github.com")——網站 session 這一關要先過，
        // 網域歸屬檢查是第二關，不能因為歸屬對得上就跳過第一關。

        var result = await _handlers.RevealCredentialForSiteAsync(add.EntryId!, "github.com");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    // ---- TOTP：新鮮度視窗——揭露動態碼比揭露密碼更嚴格的行為 ----

    [Fact]
    public async Task AddOrUpdateCredential_WithTotpSecret_ThenReveal_RoundTripsWithDefaults()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");

        var result = await _handlers.RevealTotpAsync(add.EntryId!);

        Assert.True(result.Success);
        Assert.Equal("JBSWY3DPEHPK3PXP", result.Secret);
        Assert.Equal("SHA1", result.Algorithm);
        Assert.Equal(6, result.Digits);
        Assert.Equal(30, result.PeriodSeconds);
    }

    [Fact]
    public async Task RevealTotpAsync_WithoutTotpConfigured_ReturnsTotpNotConfigured()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2",
            notes: null, linkedVaultItemUuid: null);

        var result = await _handlers.RevealTotpAsync(add.EntryId!);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerTotpNotConfigured, result.ErrorCode);
    }

    [Fact]
    public async Task RevealTotpAsync_FreshnessWindowExpired_ReturnsNotVerified_EvenThoughAppSessionStillValid()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");

        // 模擬「上次完整驗證是 45 秒前」——比 TOTP 新鮮度視窗（30 秒）久，但比一般 app session
        // 逾時（預設 1 分鐘）短，剛好落在「密碼／備註還能沿用既有 session，但 TOTP 必須重新
        // 驗證」這個中間地帶，這正是本測試要驗證的行為。Clone 一份主金鑰再傳回去（不能直接
        // 傳 TryGetAppSessionMasterKey() 拿到的原始參考——RecordAppSessionVerified 內部會先
        // ZeroMemory 舊的主金鑰才指派新的，如果新舊是同一個參考，會把自己剛指派的金鑰也歸零）。
        var clonedMasterKey = (byte[])_service.TryGetAppSessionMasterKey()!.Clone();
        _service.RecordAppSessionVerified(clonedMasterKey, DateTime.UtcNow.AddSeconds(-45));

        var totpResult = await _handlers.RevealTotpAsync(add.EntryId!);
        Assert.False(totpResult.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, totpResult.ErrorCode);

        // 對照組：同一個「過期新鮮度視窗、但 app session 還沒到期」的狀態下，密碼揭露完全
        // 不受影響——證明新鮮度視窗是 TOTP 專屬的額外限制，不是不小心把一般 session 也弄短了。
        var passwordResult = await _handlers.RevealPasswordAsync(add.EntryId!);
        Assert.True(passwordResult.Success);
    }

    [Fact]
    public async Task RevealTotpAsync_ImmediatelyAfterVerify_Succeeds()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");

        // 重新驗證一次（模擬前端強制跳驗證彈窗的行為），緊接著揭露——這才是正常使用流程。
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var result = await _handlers.RevealTotpAsync(add.EntryId!);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RevealTotpForSiteAsync_DomainOwnsCredentialAndSiteSessionValid_ReturnsSecret()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");
        _handlers.RecordSiteVerified("github.com");

        var result = await _handlers.RevealTotpForSiteAsync(add.EntryId!, "github.com");

        Assert.True(result.Success);
        Assert.Equal("JBSWY3DPEHPK3PXP", result.Secret);
    }

    [Fact]
    public async Task RevealTotpForSiteAsync_DomainDoesNotOwnCredential_ReturnsEntryNotFound()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var githubEntry = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");
        await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Evil",
            domains: ["evil.com"], username: "victim", password: "should-not-leak",
            notes: null, linkedVaultItemUuid: null);
        _handlers.RecordSiteVerified("evil.com");

        var result = await _handlers.RevealTotpForSiteAsync(githubEntry.EntryId!, "evil.com");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerEntryNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RevealTotpForSiteAsync_FreshnessWindowExpired_ReturnsNotVerified_EvenThoughSiteSessionValid()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");
        _handlers.RecordSiteVerified("github.com");

        var clonedMasterKey = (byte[])_service.TryGetAppSessionMasterKey()!.Clone();
        _service.RecordAppSessionVerified(clonedMasterKey, DateTime.UtcNow.AddSeconds(-45));

        var result = await _handlers.RevealTotpForSiteAsync(add.EntryId!, "github.com");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotVerified, result.ErrorCode);
    }

    [Fact]
    public async Task AddOrUpdateCredential_TotpPropertyOmitted_LeavesExistingTotpUntouched()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");

        // 之後只改密碼，不帶 updateTotp（預設 false）——TOTP 要維持原樣，不能被悄悄清掉。
        await _handlers.AddOrUpdateCredentialAsync(
            id: add.EntryId, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "new-password",
            notes: null, linkedVaultItemUuid: null);

        var result = await _handlers.RevealTotpAsync(add.EntryId!);
        Assert.True(result.Success);
        Assert.Equal("JBSWY3DPEHPK3PXP", result.Secret);
    }

    [Fact]
    public async Task AddOrUpdateCredential_UpdateTotpWithEmptySecret_RemovesTotp()
    {
        await _handlers.SetupCredentialAsync("correct-horse-battery-staple");
        await _handlers.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _handlers.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2", notes: null,
            linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "JBSWY3DPEHPK3PXP");

        await _handlers.AddOrUpdateCredentialAsync(
            id: add.EntryId, CredentialCategory.Website, title: "GitHub",
            domains: ["github.com"], username: "octocat", password: "hunter2",
            notes: null, linkedVaultItemUuid: null, usernameHidden: false,
            updateTotp: true, totpSecret: "");

        var result = await _handlers.RevealTotpAsync(add.EntryId!);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerTotpNotConfigured, result.ErrorCode);
    }
}
