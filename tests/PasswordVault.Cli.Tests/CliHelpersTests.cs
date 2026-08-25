using PasswordVault.Cli;
using PasswordVault.Core;

namespace PasswordVault.Cli.Tests;

/// <summary>
/// 測 <see cref="CliHelpers"/> 從 Program.cs 抽出來的兩塊邏輯（見 PasswordVault_獨立化_規劃.md
/// 第 17 節「測試覆蓋補齊」這輪定案）——不測互動流程本身、不測實際呼叫 PasswordVault.Core
/// 驗證主密碼的部分。
/// </summary>
public sealed class CliHelpersTests
{
    private static PasswordCredentialMetadata MakeEntry(
        string id = "abc123",
        CredentialCategory category = CredentialCategory.Website,
        string title = "GitHub",
        IReadOnlyList<string>? associatedDomains = null,
        string username = "octocat",
        bool usernameHidden = false)
        => new(
            id, category, title, associatedDomains ?? [], username, usernameHidden,
            LinkedVaultItemUuid: null, SourceDeleted: false,
            CreatedAtUtc: DateTime.UtcNow, UpdatedAtUtc: DateTime.UtcNow);

    // ---- FormatCredentialLines ----

    [Fact]
    public void FormatCredentialLines_NoAssociatedDomains_OmitsThatLine()
    {
        var entry = MakeEntry(associatedDomains: []);

        var lines = CliHelpers.FormatCredentialLines(entry);

        Assert.Equal(2, lines.Length);
        Assert.Equal("abc123  [Website]  GitHub", lines[0]);
        Assert.Equal("    帳號：octocat", lines[1]);
    }

    [Fact]
    public void FormatCredentialLines_WithAssociatedDomains_IncludesDomainsLine()
    {
        var entry = MakeEntry(associatedDomains: ["github.com", "github.io"]);

        var lines = CliHelpers.FormatCredentialLines(entry);

        Assert.Equal(3, lines.Length);
        Assert.Equal("    關聯網站：github.com、github.io", lines[1]);
    }

    [Fact]
    public void FormatCredentialLines_UsernameHidden_ShowsPlaceholderInsteadOfRealUsername()
    {
        var entry = MakeEntry(username: "octocat", usernameHidden: true);

        var lines = CliHelpers.FormatCredentialLines(entry);

        Assert.Contains("（已隱藏，需驗證後查看）", lines[^1]);
        Assert.DoesNotContain("octocat", lines[^1]);
    }

    // ---- ReadPasswordMasked ----

    [Fact]
    public void ReadPasswordMasked_InputRedirected_FallsBackToReaderReadLine()
    {
        // Console.ReadKey 在標準輸入被重新導向時會直接丟例外——這裡直接把「已重新導向」跟
        // 假的讀取來源一起注入，驗證會走逐行讀取這條分支，而不是嘗試呼叫 ReadKey（那樣這個
        // 測試本身就會先炸掉，不需要另外斷言「沒有呼叫 ReadKey」）。
        using var reader = new StringReader("hunter2\n");

        var result = CliHelpers.ReadPasswordMasked(isInputRedirected: true, redirectedReader: reader);

        Assert.Equal("hunter2", result);
    }

    [Fact]
    public void ReadPasswordMasked_InputRedirected_EmptyLine_ReturnsEmptyString()
    {
        using var reader = new StringReader("\n");

        var result = CliHelpers.ReadPasswordMasked(isInputRedirected: true, redirectedReader: reader);

        Assert.Equal("", result);
    }
}
