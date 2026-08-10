using PasswordVault.Core.Models;
using PasswordVault.Core.Security;
using PasswordVault.Core;

namespace PasswordVault.Core.Tests;

/// <summary>
/// 只測密碼與恢復金鑰路徑——Passkey 相關方法牽涉真的 Windows Hello 硬體互動，跟
/// PasskeyProtectorTests／FolderGuardServiceTests 同樣的限制，沒辦法自動化測試。
/// </summary>
public class PasswordLockerServiceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly PasswordLockerService _service;

    public PasswordLockerServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerPasswordLockerServiceTests_");
        var store = new PasswordLockerStore(Path.Combine(_tempDir.FullName, "credentials.json"));
        var lockoutTracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
        _service = new PasswordLockerService(store, lockoutTracker);
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    // ---- 設定與驗證（密碼路徑）----

    [Fact]
    public void IsConfigured_BeforeSetup_IsFalse()
    {
        Assert.False(_service.IsConfigured);
    }

    [Fact]
    public async Task SetupCredentialAsync_ThenIsConfigured_IsTrue()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public async Task SetupRecoveryKeyAsync_ThenIsRecoveryKeyEnabled_IsTrue()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.False(_service.IsRecoveryKeyEnabled);
        await _service.SetupRecoveryKeyAsync(verify.MasterKey!);
        Assert.True(_service.IsRecoveryKeyEnabled);
    }

    [Fact]
    public void SessionTimeoutMinutes_DefaultsToOne()
    {
        Assert.Equal(1, _service.SessionTimeoutMinutes);
    }

    [Fact]
    public async Task VerifyAsync_BeforeSetup_ReturnsNotConfiguredError()
    {
        var result = await _service.VerifyAsync("anything", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotConfigured, result.ErrorCode);
    }

    [Fact]
    public async Task VerifyAsync_CorrectPassword_ReturnsMasterKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.MasterKey);
        Assert.Equal(32, result.MasterKey!.Length);
    }

    [Fact]
    public async Task VerifyAsync_CorrectPassword_TwiceReturnsSameMasterKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var first = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var second = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.Equal(first.MasterKey, second.MasterKey);
    }

    [Fact]
    public async Task VerifyAsync_WrongPassword_ReturnsPasswordIncorrectError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _service.VerifyAsync("wrong-password", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerPasswordIncorrect, result.ErrorCode);
        Assert.Null(result.MasterKey);
    }

    [Fact]
    public async Task VerifyAsync_FiveWrongAttempts_LocksOutEvenCorrectPassword()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        for (var i = 0; i < 5; i++)
        {
            await _service.VerifyAsync("wrong-password", IntPtr.Zero);
        }

        var result = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerLockedOut, result.ErrorCode);
    }

    // ---- 改密碼：主金鑰不變，只是重新包一次（見 PasswordLockerData.PasswordWrappedMasterKeyBase64
    // 的說明），既有憑證不用重新加密，Passkey／恢復金鑰包的還是同一把主金鑰、不受影響 ----

    [Fact]
    public async Task ChangePasswordAsync_ThenVerifyWithNewPassword_CanDecryptEntryAddedBeforeChange()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var oldVerify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example", domains: ["example.com"],
            username: "user@example.com", password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: oldVerify.MasterKey!);

        var changeResult = await _service.ChangePasswordAsync("new-correct-horse-battery-staple", oldVerify.MasterKey!);
        Assert.True(changeResult.Success);

        var newVerify = await _service.VerifyAsync("new-correct-horse-battery-staple", IntPtr.Zero);
        Assert.True(newVerify.Success);
        Assert.Equal(oldVerify.MasterKey, newVerify.MasterKey);

        var decrypted = await _service.GetDecryptedPasswordAsync(add.EntryId!, newVerify.MasterKey!);
        Assert.True(decrypted.Success);
        Assert.Equal("hunter2", decrypted.Password);
    }

    [Fact]
    public async Task ChangePasswordAsync_OldPasswordNoLongerWorks()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.ChangePasswordAsync("new-correct-horse-battery-staple", verify.MasterKey!);

        var result = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerPasswordIncorrect, result.ErrorCode);
    }

    [Fact]
    public async Task ChangePasswordAsync_RecoveryKeySetUpBeforeChange_StillUnwrapsSameMasterKeyAfterChange()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var setupRecovery = await _service.SetupRecoveryKeyAsync(verify.MasterKey!);

        await _service.ChangePasswordAsync("new-correct-horse-battery-staple", verify.MasterKey!);

        var recoveryVerify = await _service.VerifyByRecoveryKeyAsync(setupRecovery.RecoveryKey!);
        Assert.True(recoveryVerify.Success);
        Assert.Equal(verify.MasterKey, recoveryVerify.MasterKey);
    }

    [Fact]
    public async Task VerifyAsync_LegacyDataWithoutWrappedMasterKey_StillAuthenticatesUsingRawDerivedKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        // 模擬這次改版前建立的舊格式資料：沒有 PasswordWrappedMasterKeyBase64 這個欄位
        // （這個欄位是這次改版才新增的，改版前的資料本來就不會有，見架構審查說明）——
        // 不能讓既有使用者的本機資料一升級就整個驗證不了，要能相容退回舊行為。
        var legacyStore = new PasswordLockerStore(Path.Combine(_tempDir.FullName, "credentials.json"));
        var data = legacyStore.Load();
        data.PasswordWrappedMasterKeyBase64 = null;
        legacyStore.Save(data);

        var result = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.MasterKey);
        Assert.Equal(32, result.MasterKey!.Length);
    }

    // ---- 恢復金鑰路徑（純函式，不牽涉 Windows Hello，可以自動化測試）----

    [Fact]
    public async Task SetupRecoveryKeyAsync_ThenVerifyByRecoveryKeyAsync_Succeeds()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var setup = await _service.SetupRecoveryKeyAsync(verify.MasterKey!);

        Assert.True(setup.Result.Success);
        Assert.NotNull(setup.RecoveryKey);

        var result = await _service.VerifyByRecoveryKeyAsync(setup.RecoveryKey!);

        Assert.True(result.Success);
        Assert.NotNull(result.MasterKey);
        Assert.Equal(verify.MasterKey, result.MasterKey);
    }

    [Fact]
    public async Task VerifyByRecoveryKeyAsync_WrongRecoveryKey_ReturnsIncorrectError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.SetupRecoveryKeyAsync(verify.MasterKey!);

        var result = await _service.VerifyByRecoveryKeyAsync("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AA");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerRecoveryKeyIncorrect, result.ErrorCode);
    }

    [Fact]
    public async Task VerifyByRecoveryKeyAsync_NotEnabled_ReturnsNotEnabledError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _service.VerifyByRecoveryKeyAsync("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AA");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerRecoveryKeyNotEnabled, result.ErrorCode);
    }

    // ---- CRUD ----

    [Fact]
    public async Task AddOrUpdateCredentialAsync_ThenGetDecryptedPasswordAsync_RoundTrips()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        Assert.True(add.Success);
        Assert.NotNull(add.EntryId);

        var decrypted = await _service.GetDecryptedPasswordAsync(add.EntryId!, verify.MasterKey!);

        Assert.True(decrypted.Success);
        Assert.Equal("hunter2", decrypted.Password);
    }

    [Fact]
    public async Task ListCredentialsMetadata_DoesNotExposeDecryptedPassword()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Single(list);
        Assert.Equal("example.com", list[0].AssociatedDomains[0]);
        Assert.Equal("user@example.com", list[0].Username);
    }

    // ---- 帳號遮蔽（UsernameHidden）----

    [Fact]
    public async Task AddOrUpdateCredentialAsync_UsernameHidden_ClearsPlaintextAndListReflectsFlag()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "secret-user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!, usernameHidden: true);

        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Single(list);
        Assert.True(list[0].UsernameHidden);
        Assert.Equal("", list[0].Username);
    }

    [Fact]
    public async Task GetDecryptedNotesAsync_WithStoredNotes_ReturnsDecryptedValue()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: "使用者名稱：小明", linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var decrypted = await _service.GetDecryptedNotesAsync(add.EntryId!, verify.MasterKey!);

        Assert.True(decrypted.Success);
        Assert.Equal("使用者名稱：小明", decrypted.Notes);
    }

    [Fact]
    public async Task GetDecryptedNotesAsync_NoNotesStored_ReturnsEmptyStringWithoutRequiringDecryption()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var decrypted = await _service.GetDecryptedNotesAsync(add.EntryId!, verify.MasterKey!);

        Assert.True(decrypted.Success);
        Assert.Equal("", decrypted.Notes);
    }

    [Fact]
    public async Task GetDecryptedUsernameAsync_HiddenUsername_ReturnsOriginalValue()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "secret-user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!, usernameHidden: true);

        var decrypted = await _service.GetDecryptedUsernameAsync(add.EntryId!, verify.MasterKey!);

        Assert.True(decrypted.Success);
        Assert.Equal("secret-user@example.com", decrypted.Username);
    }

    [Fact]
    public async Task GetDecryptedUsernameAsync_NotHidden_ReturnsPlaintextWithoutRequiringDecryption()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var decrypted = await _service.GetDecryptedUsernameAsync(add.EntryId!, verify.MasterKey!);

        Assert.True(decrypted.Success);
        Assert.Equal("user@example.com", decrypted.Username);
    }

    [Fact]
    public async Task AddOrUpdateCredentialAsync_ToggleHiddenBackToVisible_RestoresPlaintextAndClearsEncryptedField()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "secret-user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!, usernameHidden: true);

        await _service.AddOrUpdateCredentialAsync(
            id: add.EntryId, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "secret-user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!, usernameHidden: false);

        var list = await _service.ListCredentialsMetadataAsync();

        Assert.False(list[0].UsernameHidden);
        Assert.Equal("secret-user@example.com", list[0].Username);
    }

    [Fact]
    public async Task DeleteCredentialAsync_RemovesEntry()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var deleteResult = await _service.DeleteCredentialAsync(add.EntryId!);
        var list = await _service.ListCredentialsMetadataAsync();

        Assert.True(deleteResult.Success);
        Assert.Empty(list);
    }

    [Fact]
    public async Task FindCredentialsForDomain_MatchesOnlyAssociatedDomain_WithoutRequiringMasterKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var matches = await _service.FindCredentialsForDomainAsync("example.com");
        var noMatches = await _service.FindCredentialsForDomainAsync("other.com");

        Assert.Single(matches);
        Assert.Empty(noMatches);
    }

    // ---- 已加密檔案類別的自我修復 ----

    [Fact]
    public async Task CheckLinkedVaultItemsAsync_MissingVaultItem_FlagsAsSourceDeletedWithoutRemoving()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.EncryptedFile, title: "報稅資料.zip",
            domains: [], username: "", password: "hunter2", notes: null,
            linkedVaultItemUuid: "missing-uuid", masterKey: verify.MasterKey!);

        var flagged = await _service.CheckLinkedVaultItemsAsync(_ => false);
        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Contains(add.EntryId, flagged);
        Assert.Single(list);
        Assert.True(list[0].SourceDeleted);
    }

    [Fact]
    public async Task CheckLinkedVaultItemsAsync_ExistingVaultItem_NotFlagged()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.EncryptedFile, title: "報稅資料.zip",
            domains: [], username: "", password: "hunter2", notes: null,
            linkedVaultItemUuid: "existing-uuid", masterKey: verify.MasterKey!);

        var flagged = await _service.CheckLinkedVaultItemsAsync(_ => true);
        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Empty(flagged);
        Assert.False(list[0].SourceDeleted);
    }

    // ---- 自動填入 session（每網站獨立、滑動視窗）----

    [Fact]
    public void IsSiteSessionValid_NeverVerified_IsFalse()
    {
        Assert.False(_service.IsSiteSessionValid("example.com"));
    }

    [Fact]
    public void RecordSiteVerified_ThenIsSiteSessionValid_WithinTimeout_IsTrue()
    {
        var now = DateTime.UtcNow;
        _service.RecordSiteVerified("example.com", now);

        Assert.True(_service.IsSiteSessionValid("example.com", now.AddSeconds(30)));
    }

    [Fact]
    public void IsSiteSessionValid_AfterTimeoutExpires_IsFalse()
    {
        var now = DateTime.UtcNow;
        _service.RecordSiteVerified("example.com", now);

        Assert.False(_service.IsSiteSessionValid("example.com", now.AddMinutes(2)));
    }

    [Fact]
    public void RecordSiteVerified_DoesNotAffectOtherDomains()
    {
        var now = DateTime.UtcNow;
        _service.RecordSiteVerified("example.com", now);

        Assert.False(_service.IsSiteSessionValid("other.com", now));
    }

    // ---- App 分頁內驗證 session（主金鑰只留在後端記憶體，不送前端）----

    [Fact]
    public void TryGetAppSessionMasterKey_NeverVerified_ReturnsNull()
    {
        Assert.Null(_service.TryGetAppSessionMasterKey());
    }

    [Fact]
    public async Task RecordAppSessionVerified_ThenTryGetAppSessionMasterKey_WithinTimeout_ReturnsKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var now = DateTime.UtcNow;
        var masterKey = new byte[32];

        _service.RecordAppSessionVerified(masterKey, now);

        Assert.Equal(masterKey, _service.TryGetAppSessionMasterKey(now.AddSeconds(30)));
    }

    [Fact]
    public async Task TryGetAppSessionMasterKey_AfterTimeoutExpires_ReturnsNull()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var now = DateTime.UtcNow;
        _service.RecordAppSessionVerified(new byte[32], now);

        Assert.Null(_service.TryGetAppSessionMasterKey(now.AddMinutes(2)));
    }

    [Fact]
    public void ClearAppSession_AfterRecordVerified_TryGetReturnsNull()
    {
        _service.RecordAppSessionVerified(new byte[32], DateTime.UtcNow);

        _service.ClearAppSession();

        Assert.Null(_service.TryGetAppSessionMasterKey());
    }

    // ---- 備註內容搜尋（清單搜尋框「更聰明」的一部分：標題/帳號/網域已經是明文可以直接前端比對，
    // 備註是加密欄位，只能在已驗證、拿得到主金鑰的情況下由後端解密比對）----

    [Fact]
    public async Task FindEntriesWithNotesContainingAsync_MatchesCaseInsensitiveSubstring()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var withNote = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "A", domains: ["a.com"],
            username: "u", password: "p1", notes: "備份信箱是 backup@example.com", linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "B", domains: ["b.com"],
            username: "u", password: "p2", notes: "跟這次搜尋無關", linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var matches = await _service.FindEntriesWithNotesContainingAsync("BACKUP@EXAMPLE.COM", verify.MasterKey!);

        Assert.Equal([withNote.EntryId!], matches);
    }

    [Fact]
    public async Task FindEntriesWithNotesContainingAsync_EntryWithoutNotes_NotMatched()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "A", domains: ["a.com"],
            username: "u", password: "p1", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var matches = await _service.FindEntriesWithNotesContainingAsync("anything", verify.MasterKey!);

        Assert.Empty(matches);
    }

    // ---- 密碼強度／重複使用提示 ----

    [Theory]
    [InlineData("123", PasswordStrength.Weak)]
    [InlineData("password1", PasswordStrength.Weak)]
    [InlineData("Tr0ub4dor", PasswordStrength.Medium)]
    [InlineData("Correct-Horse-Battery-Staple-42!", PasswordStrength.Strong)]
    // 常見密碼（含常見的符號替代寫法，例如 @→a、0→o）即使字元類型湊到三種以上也還是弱密碼——
    // 單純比對「有幾種字元類型」抓不出這種規律性，見 EstimateStrength 的說明。
    [InlineData("P@ssw0rd", PasswordStrength.Weak)]
    // 連續遞增字元（鍵盤或字母順序）即使湊到三種字元類型也算弱密碼。
    [InlineData("Abcdefgh1", PasswordStrength.Weak)]
    // 同一個字元重複超過門檻次數也算弱密碼。
    [InlineData("aaaaaaaaaa1A", PasswordStrength.Weak)]
    public void EstimateStrength_ReturnsExpectedBucket(string password, PasswordStrength expected)
    {
        Assert.Equal(expected, PasswordLockerService.EstimateStrength(password));
    }

    [Fact]
    public async Task FindEntriesReusingPassword_TwoEntriesWithSamePassword_ReturnsBoth()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var first = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "A", domains: ["a.com"],
            username: "u", password: "shared-password", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);
        var second = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "B", domains: ["b.com"],
            username: "u", password: "shared-password", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "C", domains: ["c.com"],
            username: "u", password: "different-password", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var reused = await _service.FindEntriesReusingPasswordAsync("shared-password", verify.MasterKey!);

        Assert.Equal(2, reused.Count);
        Assert.Contains(first.EntryId, reused);
        Assert.Contains(second.EntryId, reused);
    }

    // ---- 密碼產生器 ----

    [Fact]
    public void GeneratePassword_RespectsRequestedLength()
    {
        var password = PasswordLockerService.GeneratePassword(20, includeSymbols: true);

        Assert.Equal(20, password.Length);
    }

    [Fact]
    public void GeneratePassword_WithoutSymbols_OnlyContainsAlphanumerics()
    {
        var password = PasswordLockerService.GeneratePassword(50, includeSymbols: false);

        Assert.All(password, c => Assert.True(char.IsLetterOrDigit(c)));
    }

    // ---- CSV 匯出 ----

    [Fact]
    public async Task ExportToCsv_IncludesDecryptedPasswordForEachEntry()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example", domains: ["example.com"],
            username: "user@example.com", password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var csv = await _service.ExportToCsvAsync(verify.MasterKey!);

        Assert.Contains("example.com", csv);
        Assert.Contains("user@example.com", csv);
        Assert.Contains("hunter2", csv);
    }

    [Fact]
    public async Task ExportToCsv_HiddenUsername_DecryptsUsernameForExport()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example", domains: ["example.com"],
            username: "secret-user@example.com", password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!, usernameHidden: true);

        var csv = await _service.ExportToCsvAsync(verify.MasterKey!);

        Assert.Contains("secret-user@example.com", csv);
    }

    // ---- CSV 匯入（規劃文件第 7 節：支援自己的匯出格式，以及 Chrome／Edge 匯出格式）----

    [Fact]
    public async Task ImportFromCsv_OwnExportFormat_CreatesEntryWithDomainsAndNotes()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var csv = "title,domains,username,password,notes\r\nExample,example.com;example.org,user@example.com,hunter2,備註文字\r\n";

        var result = await _service.ImportFromCsvAsync(csv, verify.MasterKey!);

        Assert.True(result.Success);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);

        var items = await _service.ListCredentialsMetadataAsync();
        var imported = Assert.Single(items);
        Assert.Equal("Example", imported.Title);
        Assert.Equal(["example.com", "example.org"], imported.AssociatedDomains);
        Assert.Equal("user@example.com", imported.Username);

        var password = await _service.GetDecryptedPasswordAsync(imported.Id, verify.MasterKey!);
        Assert.Equal("hunter2", password.Password);
    }

    [Fact]
    public async Task ImportFromCsv_ChromeExportFormat_DerivesDomainFromUrl()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var csv = "name,url,username,password\r\nExample Site,https://www.example.com/login,user@example.com,hunter2\r\n";

        var result = await _service.ImportFromCsvAsync(csv, verify.MasterKey!);

        Assert.True(result.Success);
        Assert.Equal(1, result.ImportedCount);

        var items = await _service.ListCredentialsMetadataAsync();
        var imported = Assert.Single(items);
        Assert.Equal("Example Site", imported.Title);
        Assert.Equal(["www.example.com"], imported.AssociatedDomains);
        Assert.Equal(CredentialCategory.Website, imported.Category);
    }

    [Fact]
    public async Task ImportFromCsv_RowMissingPassword_IsSkippedNotImported()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var csv = "name,url,username,password\r\nNo Password,https://example.com,user,\r\nHas Password,https://example.org,user,hunter2\r\n";

        var result = await _service.ImportFromCsvAsync(csv, verify.MasterKey!);

        Assert.True(result.Success);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public async Task ImportFromCsv_QuotedFieldWithEmbeddedComma_ParsesCorrectly()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var csv = "title,domains,username,password,notes\r\n\"Example, Inc.\",example.com,user,hunter2,\"line with \"\"quotes\"\"\"\r\n";

        var result = await _service.ImportFromCsvAsync(csv, verify.MasterKey!);

        Assert.Equal(1, result.ImportedCount);
        var items = await _service.ListCredentialsMetadataAsync();
        Assert.Equal("Example, Inc.", items[0].Title);
    }

    [Fact]
    public async Task ImportFromCsv_MissingPasswordColumn_ReturnsInvalidFormatError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var csv = "name,url,username\r\nExample,https://example.com,user\r\n";

        var result = await _service.ImportFromCsvAsync(csv, verify.MasterKey!);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerCsvInvalidFormat, result.ErrorCode);
    }

    [Fact]
    public async Task ImportFromCsv_EmptyBody_ImportsNothingButSucceeds()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var csv = "title,domains,username,password,notes\r\n";

        var result = await _service.ImportFromCsvAsync(csv, verify.MasterKey!);

        Assert.True(result.Success);
        Assert.Equal(0, result.ImportedCount);
    }
}
