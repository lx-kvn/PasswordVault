using System.IO;
using System.Text.Json;

namespace PasswordVault.App;

/// <summary>
/// 密碼庫瀏覽器整合的登錄內容決策——「該寫什麼」與「該不該換」的純計算，不碰登錄檔、不碰
/// 檔案系統（實際的寫入在 <see cref="PasswordVaultNativeHostSync"/>）。
///
/// 這個檔案與 FileLocker repo 的 src/FileLocker.App/NativeHostRegistration.cs 是對等的獨立
/// 副本——兩個 repo 沒有共用程式碼（見 ADR-0003，FileLocker 那邊只透過二進位相依取得
/// PasswordVault.Core.dll），因此這段邏輯必須各自維護一份。**兩份的輸出必須逐位元組相同**，
/// 任何一邊改了欄位、順序、說明文字或序列化選項，另一邊要跟著改，否則協調會無聲失效：
/// 兩邊各寫各的內容，登錄機碼重新退化成「最後啟動的一方贏」。
///
/// 為什麼要把這一層抽出來：`FileLocker.App` 與 `PasswordVault.exe` 兩邊都會寫同一組登錄
/// 機碼，而那個機碼只有一個值，因此永遠是「最後啟動的一方贏」。
/// PasswordVault_獨立化_規劃.md 第 8.1 節已經用「兩邊共用同一支轉接程式、同一個路徑」解掉
/// 「登錄機碼的贏家」與「Named Pipe 的贏家」判斷不一致的那半問題；剩下的那半是**兩邊寫出來
/// 的內容必須逐位元組相同**——只要內容相同，誰贏就真的不重要了，不需要再發明一套跨行程的
/// 協商機制（那跟 ADR-0001 拒絕擁有權轉移、第 8 節拒絕強制接手是同一種判斷：不為了邊緣情境
/// 換取不成比例的架構成本）。
///
/// 因此這裡的每一個決定都以「兩邊算出來會不會一樣」為準：說明文字不帶任何一邊的品牌、
/// 識別碼清單排序後才輸出、允許的擴充功能識別碼用併集而不是整欄覆蓋。
/// </summary>
public static class NativeHostRegistration
{
    /// <summary>維持 <c>com.filelocker.passwordlocker</c> 不改名——這是已安裝的擴充功能
    /// 認得的識別碼，不是「屬於 FileLocker」的名稱，換掉會讓現有使用者的瀏覽器整合直接失效
    /// （跟擴充功能簽署金鑰沿用舊值是同一個理由）。</summary>
    public const string HostName = "com.filelocker.passwordlocker";

    /// <summary>不帶品牌的說明文字。兩邊必須寫出完全相同的字串，寫成「FileLocker 密碼庫
    /// 瀏覽器整合」這種帶自己品牌的版本，會讓兩邊各寫各的、重新退化成「最後寫的贏」。
    /// 這個字串只出現在 manifest 檔案裡，使用者介面上看不到，不需要雙語版本。</summary>
    private const string Description = "密碼庫瀏覽器整合（Native Messaging Host）";

    /// <summary>兩邊寫出來的位元組要一致，序列化選項因此必須固定：縮排開啟、屬性順序照下方
    /// 宣告順序、逸出規則用預設值。任何一邊改了這裡，另一邊要跟著改。</summary>
    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };

    /// <summary>把自己的擴充功能識別碼併進既有內容裡的清單，而不是整欄覆蓋。
    ///
    /// 覆蓋的後果：兩邊各自帶著不同的擴充功能識別碼時（例如上架商店拿到正式識別碼、其中
    /// 一邊還沒跟著更新），manifest 會在兩個值之間來回震盪——今天 A 先啟動就寫 A 的、明天
    /// B 先啟動就寫 B 的，而且兩種狀態下都有一邊的擴充功能連不上。這個欄位本來就是陣列，
    /// 併集就沒有震盪可言。
    ///
    /// 代價是舊的識別碼會一直留著、沒有清除機制。這是刻意接受的：識別碼由擴充功能的簽署
    /// 金鑰決定，而那把金鑰在使用者自己手上，留著一個不再使用的識別碼的風險，遠小於「兩邊
    /// 互相把對方擦掉」造成的功能失效。要清除時直接刪掉 manifest 檔案，下次啟動會重建。
    ///
    /// 讀不懂既有內容時只保留自己的——那份內容沒有可信的識別碼可以繼承，但不能因此連自己的
    /// 也不寫，那會讓瀏覽器整合完全失效。</summary>
    public static IReadOnlyList<string> MergeAllowedExtensionIds(
        string? existingManifestJson, string ownExtensionId)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ownExtensionId))
        {
            ids.Add(ownExtensionId.Trim());
        }

        foreach (var existing in ReadExtensionIds(existingManifestJson))
        {
            ids.Add(existing);
        }

        return ids.ToList();
    }

    /// <summary>從既有 manifest 的 allowed_origins 反推出裡面登記過的擴充功能識別碼。
    /// 格式不符的項目直接略過，不當成錯誤——這個檔案可能被手動改過，或由未來版本寫入而
    /// 帶著這個版本還不認得的內容。</summary>
    private static IEnumerable<string> ReadExtensionIds(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            yield break;
        }

        JsonElement origins;
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("allowed_origins", out var property)
                || property.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }
            origins = property.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var origin in origins.EnumerateArray())
        {
            var value = origin.ValueKind == JsonValueKind.String ? origin.GetString() : null;
            var id = ExtractExtensionId(value);
            if (id is not null)
            {
                yield return id;
            }
        }
    }

    private const string OriginPrefix = "chrome-extension://";

    private static string? ExtractExtensionId(string? origin)
    {
        if (origin is null || !origin.StartsWith(OriginPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = origin[OriginPrefix.Length..].TrimEnd('/').Trim();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>產生 manifest 內容。識別碼先排序再輸出，讓兩邊即使以不同順序拿到同一組
    /// 識別碼也算得出同樣的位元組。</summary>
    public static string BuildManifest(string hostExePath, IEnumerable<string> extensionIds)
    {
        var ordered = extensionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"{OriginPrefix}{id}/")
            .ToArray();

        var manifest = new
        {
            name = HostName,
            description = Description,
            path = hostExePath,
            type = "stdio",
            allowed_origins = ordered,
        };

        return JsonSerializer.Serialize(manifest, ManifestOptions);
    }

    /// <summary>共用位置那份轉接程式該不該換成手上這份。
    ///
    /// 原本的規則是「共用位置有檔案就什麼都不做，不比對版本」（見
    /// PasswordVault_獨立化_規劃.md 第 8.1 節的實作紀錄）。那條規則在當時是對的——目標只是
    /// 讓兩邊指向同一個地址——但它的後果是共用位置永遠停在最早安裝的那個版本，之後任何一邊
    /// 修好了轉接程式都送不過去。這個缺陷目前還沒咬到人，但轉接程式一旦要改就會咬。
    ///
    /// 判斷不了新舊時傾向不動：覆蓋是有風險的動作（可能把新的換成舊的），而維持現狀至少
    /// 不會讓已經正常運作的組合退化。唯一的例外是「共用位置那份讀不出版本」——認不出身分的
    /// 檔案沒辦法保證完整可用（例如上一次複製到一半被中斷），換成一個版本明確的副本比留著
    /// 一個來歷不明的好。</summary>
    public static bool ShouldReplaceSharedHost(
        bool sharedExists, Version? sharedVersion, Version? incomingVersion)
    {
        if (!sharedExists)
        {
            return true;
        }

        if (incomingVersion is null)
        {
            return false;
        }

        if (sharedVersion is null)
        {
            return true;
        }

        return incomingVersion > sharedVersion;
    }

    /// <summary>讀出執行檔的版本；檔案不存在、沒有版本資訊、或格式不符時回 null，由
    /// <see cref="ShouldReplaceSharedHost"/> 決定那種情況要怎麼辦。</summary>
    public static Version? ReadFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var raw = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(raw, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }
}
