using PasswordVault.Core;

namespace PasswordVault.Core.Tests;

public class PasswordLockerStoreTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly PasswordLockerStore _store;

    public PasswordLockerStoreTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerPasswordLockerStoreTests_");
        _store = new PasswordLockerStore(Path.Combine(_tempDir.FullName, "credentials.json"));
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNewDataWithDefaults()
    {
        var data = _store.Load();

        Assert.Null(data.PasswordSaltBase64);
        Assert.Null(data.PasswordVerificationHashBase64);
        Assert.False(data.PasskeyEnabled);
        Assert.False(data.RecoveryKeyEnabled);
        Assert.Equal(1, data.SessionTimeoutMinutes);
        Assert.Empty(data.Entries);
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsNewDataInsteadOfThrowing()
    {
        var filePath = Path.Combine(_tempDir.FullName, "credentials.json");
        File.WriteAllText(filePath, "not valid json {{{");
        var store = new PasswordLockerStore(filePath);

        var data = store.Load();

        Assert.Empty(data.Entries);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEntry()
    {
        var data = _store.Load();
        data.PasswordSaltBase64 = "salt";
        data.PasswordVerificationHashBase64 = "hash";
        data.Entries.Add(new PasswordCredentialEntry
        {
            Category = CredentialCategory.Website,
            Title = "Example",
            AssociatedDomains = ["example.com"],
            Username = "user@example.com",
            EncryptedPasswordBase64 = "ciphertext",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        _store.Save(data);
        var loaded = _store.Load();

        Assert.Equal("salt", loaded.PasswordSaltBase64);
        Assert.Equal("hash", loaded.PasswordVerificationHashBase64);
        Assert.Single(loaded.Entries);
        Assert.Equal("example.com", loaded.Entries[0].AssociatedDomains[0]);
        Assert.Equal("user@example.com", loaded.Entries[0].Username);
        Assert.Equal("ciphertext", loaded.Entries[0].EncryptedPasswordBase64);
    }

    [Fact]
    public void PasswordLockerData_DefaultValues_SessionTimeoutMinutes1()
    {
        var data = new PasswordLockerData();

        Assert.Equal(1, data.SessionTimeoutMinutes);
        Assert.False(data.PasskeyEnabled);
        Assert.False(data.RecoveryKeyEnabled);
    }

    [Fact]
    public void Load_OldFormatJsonWithoutSessionTimeoutField_FallsBackToDefault()
    {
        // 模擬升級前就存在的 credentials.json：完全沒有 SessionTimeoutMinutes 這個鍵，
        // 驗證舊使用者升級後不會因為缺欄位而讀出 int 預設值 0，而是拿到跟全新安裝一樣的預設值。
        var filePath = Path.Combine(_tempDir.FullName, "credentials.json");
        File.WriteAllText(filePath, """
            {
              "PasswordSaltBase64": null,
              "PasswordVerificationHashBase64": null,
              "PasskeyEnabled": false,
              "RecoveryKeyEnabled": false,
              "Entries": []
            }
            """);
        var oldFormatStore = new PasswordLockerStore(filePath);

        var data = oldFormatStore.Load();

        Assert.Equal(1, data.SessionTimeoutMinutes);
    }
}
