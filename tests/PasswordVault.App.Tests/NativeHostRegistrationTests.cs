using System.Text.Json;
using PasswordVault.App;

namespace PasswordVault.App.Tests;

/// <summary>
/// 密碼庫瀏覽器整合的登錄內容（manifest）決策邏輯。
///
/// 這份測試與 FileLocker repo 的 tests/FileLocker.App.Tests/NativeHostRegistrationTests.cs
/// 是對等的獨立副本——兩個 repo 沒有共用程式碼（見 ADR-0003），而這裡要固定住的正是「兩邊
/// 算出來的內容必須逐位元組相同」，因此兩邊各自都要有一份守住同一組行為的測試。任何一邊
/// 改了 manifest 的欄位、順序或說明文字，另一邊要跟著改，否則協調會無聲失效。
///
/// 背景：`FileLocker.App` 與 `PasswordVault.exe` 兩邊都會寫同一組登錄機碼，而登錄機碼只有
/// 一個值，先前的寫法是「最後啟動的一方贏」。PasswordVault_獨立化_規劃.md 第 8.1 節已經用
/// 共用轉接程式路徑解掉「贏家判斷不一致」那半，這裡處理剩下的那半：**兩邊寫出來的內容必須
/// 逐位元組相同，誰贏才會真的失去意義**。因此這裡的斷言不是「內容看起來合理」，而是把
/// 「同樣的輸入必須產生同樣的位元組」「自己的擴充功能 ID 是併進去不是蓋掉」這兩件事固定住。
///
/// 這些函式全部不碰登錄檔與檔案系統——寫入那一層薄到不需要測試，真正會出錯的是「該寫什麼
/// 內容」的判斷，把它抽出來才測得到（原本的 EnsureRegistered 把判斷與寫入綁在一起，測一次
/// 就得真的動使用者的登錄檔）。
/// </summary>
public sealed class NativeHostRegistrationTests
{
    private const string OwnId = "ihhcdgkacinknnbaibnjpaamfhbebpdj";
    private const string OtherId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SharedExe =
        @"C:\Users\Tester\AppData\Local\PasswordVault\NativeHost\PasswordVault.NativeHost.exe";

    // ---- allowed_origins：併進去，不是蓋掉 ----

    [Fact]
    public void 沒有既有內容時只有自己的擴充功能識別碼()
    {
        var ids = NativeHostRegistration.MergeAllowedExtensionIds(null, OwnId);
        Assert.Equal(new[] { OwnId }, ids);
    }

    [Fact]
    public void 既有內容裡的別的識別碼會被保留()
    {
        // 兩邊各自帶著不同版本的擴充功能識別碼時（例如上架商店拿到新識別碼、其中一邊還沒
        // 更新），整欄覆蓋會讓 manifest 在兩個值之間來回跳：今天 A 先啟動就寫 A 的、明天 B
        // 先啟動就寫 B 的，而且兩種狀態下都有一邊的擴充功能連不上。改成併集就不會震盪。
        var existing = NativeHostRegistration.BuildManifest(SharedExe, new[] { OtherId });
        var ids = NativeHostRegistration.MergeAllowedExtensionIds(existing, OwnId);
        Assert.Contains(OwnId, ids);
        Assert.Contains(OtherId, ids);
    }

    [Fact]
    public void 同一個識別碼不會被加第二次()
    {
        var existing = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId });
        var ids = NativeHostRegistration.MergeAllowedExtensionIds(existing, OwnId);
        Assert.Equal(new[] { OwnId }, ids);
    }

    [Fact]
    public void 識別碼順序穩定不受既有內容的順序影響()
    {
        // 順序不穩定的話，兩邊寫出來的位元組就不同，會退化回「最後寫的贏」。
        var oneWay = NativeHostRegistration.MergeAllowedExtensionIds(
            NativeHostRegistration.BuildManifest(SharedExe, new[] { OtherId }), OwnId);
        var otherWay = NativeHostRegistration.MergeAllowedExtensionIds(
            NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId }), OtherId);
        Assert.Equal(oneWay, otherWay);
    }

    [Fact]
    public void 壞掉的既有內容不會讓自己的識別碼消失()
    {
        // 讀不懂的 manifest 沒有可信的既有識別碼可以保留，但不能因此連自己的也不寫——
        // 那會讓瀏覽器整合完全失效，比少保留一個舊識別碼嚴重得多。
        var ids = NativeHostRegistration.MergeAllowedExtensionIds("這不是 JSON", OwnId);
        Assert.Equal(new[] { OwnId }, ids);
    }

    [Fact]
    public void 空白的識別碼不會被寫進去()
    {
        var existing = NativeHostRegistration.BuildManifest(SharedExe, new[] { OtherId, "   " });
        var ids = NativeHostRegistration.MergeAllowedExtensionIds(existing, OwnId);
        Assert.Equal(new[] { OtherId, OwnId }.OrderBy(x => x, StringComparer.Ordinal), ids);
    }

    // ---- manifest 內容：兩邊必須產生完全一樣的位元組 ----

    [Fact]
    public void 相同輸入產生完全相同的內容()
    {
        var first = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId });
        var second = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId });
        Assert.Equal(first, second);
    }

    [Fact]
    public void 識別碼順序不同也產生相同的內容()
    {
        var first = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId, OtherId });
        var second = NativeHostRegistration.BuildManifest(SharedExe, new[] { OtherId, OwnId });
        Assert.Equal(first, second);
    }

    [Fact]
    public void 內容指向傳進來的共用轉接程式路徑()
    {
        var manifest = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId });
        using var parsed = JsonDocument.Parse(manifest);
        Assert.Equal(SharedExe, parsed.RootElement.GetProperty("path").GetString());
        Assert.Equal(NativeHostRegistration.HostName,
                     parsed.RootElement.GetProperty("name").GetString());
        Assert.Equal("stdio", parsed.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void 每個識別碼都寫成瀏覽器認得的來源格式()
    {
        var manifest = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId, OtherId });
        using var parsed = JsonDocument.Parse(manifest);
        var origins = parsed.RootElement.GetProperty("allowed_origins")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains($"chrome-extension://{OwnId}/", origins);
        Assert.Contains($"chrome-extension://{OtherId}/", origins);
    }

    [Fact]
    public void 說明文字不帶任何一邊的品牌()
    {
        // 兩邊寫的內容要逐位元組相同，所以這個欄位不能是「FileLocker 密碼庫瀏覽器整合」
        // 這種帶自己品牌的字串——那會讓兩邊各寫各的，重新變成「最後寫的贏」。
        var manifest = NativeHostRegistration.BuildManifest(SharedExe, new[] { OwnId });
        using var parsed = JsonDocument.Parse(manifest);
        var description = parsed.RootElement.GetProperty("description").GetString();
        Assert.DoesNotContain("FileLocker", description);
        Assert.DoesNotContain("PasswordVault", description);
    }

    // ---- 共用位置的轉接程式該不該換 ----

    [Fact]
    public void 共用位置還沒有轉接程式就要複製過去()
    {
        Assert.True(NativeHostRegistration.ShouldReplaceSharedHost(
            sharedExists: false, sharedVersion: null, incomingVersion: new Version(1, 0)));
    }

    [Fact]
    public void 手上這份比較新就換掉()
    {
        // 原本的規則是「有就不動、不比版本」。那條規則的後果是共用位置永遠停在最早安裝的
        // 那個版本，之後任何一邊修好了轉接程式都送不過去——目前還沒咬到人，但一定會咬。
        Assert.True(NativeHostRegistration.ShouldReplaceSharedHost(
            sharedExists: true, sharedVersion: new Version(1, 0), incomingVersion: new Version(1, 1)));
    }

    [Fact]
    public void 版本相同就不動()
    {
        Assert.False(NativeHostRegistration.ShouldReplaceSharedHost(
            sharedExists: true, sharedVersion: new Version(1, 1), incomingVersion: new Version(1, 1)));
    }

    [Fact]
    public void 手上這份比較舊就不要覆蓋掉新的()
    {
        Assert.False(NativeHostRegistration.ShouldReplaceSharedHost(
            sharedExists: true, sharedVersion: new Version(2, 0), incomingVersion: new Version(1, 0)));
    }

    [Fact]
    public void 讀不出手上這份的版本就不要動已經在用的那份()
    {
        // 讀不到版本代表判斷不了新舊，覆蓋是有風險的動作（可能把新的換成舊的），維持現狀
        // 至少不會讓已經正常運作的組合退化。
        Assert.False(NativeHostRegistration.ShouldReplaceSharedHost(
            sharedExists: true, sharedVersion: new Version(1, 0), incomingVersion: null));
    }

    [Fact]
    public void 共用位置那份的版本讀不出來就換成手上這份()
    {
        // 認不出身分的檔案沒辦法保證是完整可用的（例如上一次複製到一半被中斷），換成一個
        // 版本明確的副本，比留著一個來歷不明的好。
        Assert.True(NativeHostRegistration.ShouldReplaceSharedHost(
            sharedExists: true, sharedVersion: null, incomingVersion: new Version(1, 0)));
    }
}
