using System.Text.Json;
using FileLocker.PluginContracts;

namespace FileLocker.PasswordLocker.Tests;

/// <summary>
/// 驗證 <see cref="PasswordLockerPlugin"/> 這個對外唯一入口——主體只知道「訊息名稱＋JSON 內容
/// 轉發進去、拿回一個已經帶好 type 欄位的回應物件」，這裡驗證這條轉發路徑本身是通的，不是重新
/// 測一次 PasswordLockerService／PasswordLockerProtocolHandlers 已經測過的業務邏輯。
/// </summary>
public class PasswordLockerPluginTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly IPasswordLockerPlugin _plugin;

    public PasswordLockerPluginTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerPasswordLockerPluginTests_");
        _plugin = new PasswordLockerPlugin();
        _plugin.Initialize(new PasswordLockerPluginContext(_tempDir.FullName, _ => true));
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    // camelCase 比照 MainWindow.SendToFrontendJsonOptions 的既有序列化設定——回應物件最終
    // 就是透過那組設定送到前端，這裡用同一套設定序列化才是真的驗證到「前端收到的欄位長什麼樣」，
    // 不是驗證一個測試自己方便、但跟正式路徑不一致的假設。
    private static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonElement ToJson(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;

    private static JsonElement GetProperty(object? response, string name)
        => JsonDocument.Parse(JsonSerializer.Serialize(response, CamelCaseOptions)).RootElement.GetProperty(name);

    [Fact]
    public void Initialize_CreatesDataDirectory()
    {
        Assert.True(Directory.Exists(_tempDir.FullName));
    }

    [Fact]
    public async Task HandleRequestAsync_WithoutInitialize_Throws()
    {
        var plugin = new PasswordLockerPlugin();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => plugin.HandleRequestAsync("listPasswordLocker", ToJson(new { }), IntPtr.Zero));
    }

    [Fact]
    public async Task HandleRequestAsync_UnknownMessageType_ReturnsNull()
    {
        var response = await _plugin.HandleRequestAsync("somethingUnrelated", ToJson(new { }), IntPtr.Zero);

        Assert.Null(response);
    }

    [Fact]
    public async Task HandleRequestAsync_ListBeforeSetup_ReturnsNotConfigured()
    {
        var response = await _plugin.HandleRequestAsync("listPasswordLocker", ToJson(new { }), IntPtr.Zero);

        Assert.Equal("passwordLockerListResult", GetProperty(response, "type").GetString());
        Assert.False(GetProperty(response, "configured").GetBoolean());
    }

    [Fact]
    public async Task HandleRequestAsync_SetupCredential_ThenVerify_Succeeds()
    {
        var setupResponse = await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        Assert.True(GetProperty(setupResponse, "success").GetBoolean());

        var verifyResponse = await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);

        Assert.Equal("verifyPasswordLockerResult", GetProperty(verifyResponse, "type").GetString());
        Assert.True(GetProperty(verifyResponse, "success").GetBoolean());
    }

    [Fact]
    public async Task HandleRequestAsync_AddCredential_ThenReveal_RoundTrips()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);

        var addResponse = await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website",
            title = "Example",
            domains = new[] { "example.com" },
            username = "user@example.com",
            password = "hunter2"
        }), IntPtr.Zero);

        Assert.True(GetProperty(addResponse, "success").GetBoolean());
        var entryId = GetProperty(addResponse, "entryId").GetString();

        var revealResponse = await _plugin.HandleRequestAsync(
            "revealPasswordLockerPassword", ToJson(new { id = entryId }), IntPtr.Zero);

        Assert.True(GetProperty(revealResponse, "success").GetBoolean());
        Assert.Equal("hunter2", GetProperty(revealResponse, "password").GetString());

        var listResponse = await _plugin.HandleRequestAsync("listPasswordLocker", ToJson(new { }), IntPtr.Zero);
        Assert.Equal(1, GetProperty(listResponse, "items").GetArrayLength());
    }

    [Fact]
    public async Task HandleRequestAsync_AddCredentialWithNotes_ThenRevealNotes_RoundTrips()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);

        var addResponse = await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website",
            title = "Example",
            domains = new[] { "example.com" },
            username = "user@example.com",
            password = "hunter2",
            notes = "使用者名稱：小明"
        }), IntPtr.Zero);

        Assert.True(GetProperty(addResponse, "success").GetBoolean());
        var entryId = GetProperty(addResponse, "entryId").GetString();

        var revealResponse = await _plugin.HandleRequestAsync(
            "revealPasswordLockerNotes", ToJson(new { id = entryId }), IntPtr.Zero);

        Assert.True(GetProperty(revealResponse, "success").GetBoolean());
        Assert.Equal("使用者名稱：小明", GetProperty(revealResponse, "notes").GetString());
    }

    [Fact]
    public async Task HandleRequestAsync_RevealBeforeVerify_ReturnsNotVerifiedError()
    {
        var response = await _plugin.HandleRequestAsync(
            "revealPasswordLockerPassword", ToJson(new { id = "some-id" }), IntPtr.Zero);

        Assert.False(GetProperty(response, "success").GetBoolean());
        Assert.Equal("PASSWORD_LOCKER_NOT_VERIFIED", GetProperty(response, "errorCode").GetString());
    }

    [Fact]
    public async Task HandleRequestAsync_GeneratePassword_ReturnsRequestedLength()
    {
        var response = await _plugin.HandleRequestAsync(
            "generatePasswordLockerPassword", ToJson(new { length = 24, includeSymbols = false }), IntPtr.Zero);

        Assert.Equal(24, GetProperty(response, "password").GetString()!.Length);
    }

    [Fact]
    public async Task HandleRequestAsync_ExportCsvBeforeVerify_ReturnsNotVerifiedError()
    {
        var response = await _plugin.HandleRequestAsync("exportPasswordLockerCsv", ToJson(new { }), IntPtr.Zero);

        Assert.False(GetProperty(response, "success").GetBoolean());
        Assert.Equal("PASSWORD_LOCKER_NOT_VERIFIED", GetProperty(response, "errorCode").GetString());
    }

    [Fact]
    public async Task HandleRequestAsync_ExportCsv_AfterVerify_ReturnsCsvContent()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);
        await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website",
            title = "Example",
            domains = new[] { "example.com" },
            username = "user@example.com",
            password = "hunter2"
        }), IntPtr.Zero);

        var response = await _plugin.HandleRequestAsync("exportPasswordLockerCsv", ToJson(new { }), IntPtr.Zero);

        Assert.True(GetProperty(response, "success").GetBoolean());
        Assert.Contains("hunter2", GetProperty(response, "csv").GetString());
    }

    [Fact]
    public async Task HandleRequestAsync_CheckPasswordReuse_ExcludesGivenId_CountsOthers()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);

        var first = await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website", title = "A", domains = new[] { "a.com" }, username = "u", password = "shared-pw"
        }), IntPtr.Zero);
        var firstId = GetProperty(first, "entryId").GetString();
        await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website", title = "B", domains = new[] { "b.com" }, username = "u", password = "shared-pw"
        }), IntPtr.Zero);

        var withoutExclude = await _plugin.HandleRequestAsync(
            "checkPasswordLockerPasswordReuse", ToJson(new { password = "shared-pw" }), IntPtr.Zero);
        Assert.Equal(2, GetProperty(withoutExclude, "reuseCount").GetInt32());

        var withExclude = await _plugin.HandleRequestAsync(
            "checkPasswordLockerPasswordReuse", ToJson(new { password = "shared-pw", excludeId = firstId }), IntPtr.Zero);
        Assert.Equal(1, GetProperty(withExclude, "reuseCount").GetInt32());
    }

    [Fact]
    public async Task HandleRequestAsync_ImportCsv_AfterVerify_AddsEntriesAndReportsCounts()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);

        var response = await _plugin.HandleRequestAsync("importPasswordLockerCsv", ToJson(new
        {
            csv = "name,url,username,password\nExample,https://example.com,user,hunter2\n"
        }), IntPtr.Zero);

        Assert.True(GetProperty(response, "success").GetBoolean());
        Assert.Equal(1, GetProperty(response, "importedCount").GetInt32());

        var listResponse = await _plugin.HandleRequestAsync("listPasswordLocker", ToJson(new { }), IntPtr.Zero);
        Assert.Equal(1, GetProperty(listResponse, "items").GetArrayLength());
    }

    // ---- 瀏覽器擴充功能專用的網站相關訊息（規劃文件第 5 節，Native Messaging Host 轉接層會呼叫這些）----

    [Fact]
    public async Task HandleRequestAsync_FindCredentialsForDomain_DoesNotRequireVerification()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);
        await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website", title = "Example", domains = new[] { "example.com" }, username = "u", password = "hunter2"
        }), IntPtr.Zero);

        // 建立一個全新、沒驗證過的部件實體，模擬瀏覽器擴充功能在使用者還沒驗證身份的情況下
        // 查詢有沒有已存憑證——這個查詢本來就設計成不需要驗證（只回傳 metadata，不含密碼）。
        var freshPlugin = new PasswordLockerPlugin();
        freshPlugin.Initialize(new PasswordLockerPluginContext(_tempDir.FullName, _ => true));

        var response = await freshPlugin.HandleRequestAsync(
            "findPasswordLockerCredentialsForDomain", ToJson(new { domain = "example.com" }), IntPtr.Zero);

        var items = GetProperty(response, "items");
        Assert.Equal(1, items.GetArrayLength());
    }

    [Fact]
    public async Task HandleRequestAsync_SiteSession_InvalidBeforeRecord_ValidAfterRecord()
    {
        var before = await _plugin.HandleRequestAsync(
            "isPasswordLockerSiteSessionValid", ToJson(new { domain = "example.com" }), IntPtr.Zero);
        Assert.False(GetProperty(before, "valid").GetBoolean());

        await _plugin.HandleRequestAsync(
            "recordPasswordLockerSiteVerified", ToJson(new { domain = "example.com" }), IntPtr.Zero);

        var after = await _plugin.HandleRequestAsync(
            "isPasswordLockerSiteSessionValid", ToJson(new { domain = "example.com" }), IntPtr.Zero);
        Assert.True(GetProperty(after, "valid").GetBoolean());
    }

    [Fact]
    public async Task HandleRequestAsync_RevealCredentialForSite_WithoutSiteSession_ReturnsNotVerifiedEvenIfAppSessionValid()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);
        var add = await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website", title = "Example", domains = new[] { "example.com" }, username = "u", password = "hunter2"
        }), IntPtr.Zero);
        var entryId = GetProperty(add, "entryId").GetString();

        // App 分頁 session（TryGetAppSessionMasterKey）有效，但這個網站的獨立 session 從沒記錄過——
        // 兩者是分開的執行期狀態，任一個沒過都不能直接把密碼吐出來給瀏覽器。
        var response = await _plugin.HandleRequestAsync(
            "revealPasswordLockerCredentialForSite", ToJson(new { id = entryId, domain = "example.com" }), IntPtr.Zero);

        Assert.False(GetProperty(response, "success").GetBoolean());
        Assert.Equal("PASSWORD_LOCKER_NOT_VERIFIED", GetProperty(response, "errorCode").GetString());
    }

    [Fact]
    public async Task HandleRequestAsync_RevealCredentialForSite_WithBothSessionsValid_ReturnsPassword()
    {
        await _plugin.HandleRequestAsync(
            "setupPasswordLockerCredential", ToJson(new { password = "correct-horse-battery-staple" }), IntPtr.Zero);
        await _plugin.HandleRequestAsync(
            "verifyPasswordLocker", ToJson(new { password = "correct-horse-battery-staple", tryPasskeyFirst = false }), IntPtr.Zero);
        var add = await _plugin.HandleRequestAsync("addOrUpdatePasswordLockerCredential", ToJson(new
        {
            category = "Website", title = "Example", domains = new[] { "example.com" }, username = "u", password = "hunter2"
        }), IntPtr.Zero);
        var entryId = GetProperty(add, "entryId").GetString();

        await _plugin.HandleRequestAsync(
            "recordPasswordLockerSiteVerified", ToJson(new { domain = "example.com" }), IntPtr.Zero);

        var response = await _plugin.HandleRequestAsync(
            "revealPasswordLockerCredentialForSite", ToJson(new { id = entryId, domain = "example.com" }), IntPtr.Zero);

        Assert.True(GetProperty(response, "success").GetBoolean());
        Assert.Equal("hunter2", GetProperty(response, "password").GetString());
    }
}
