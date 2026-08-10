namespace PasswordVault.Core;

/// <summary>
/// 舊版使用者的密碼庫資料從 FileLocker 的 <c>%AppData%\FileLocker\PasswordLocker\</c> 自動搬到
/// PasswordVault 自己的資料目錄——這輪定案的行為（見 PasswordVault_獨立化_規劃.md 第 7 節）：
///
/// 1. 搬移是複製，不是移動：成功搬完之後保留舊檔案不刪，理由是萬一新版邏輯有 bug、或使用者
///    這段過渡期還想繼續用 FileLocker 那邊的密碼庫，舊資料還在，不會有「唯一一份資料」的風險。
///    代價是兩份資料之後會不同步（在 PasswordVault 這邊修改不會反映回 FileLocker 那份），
///    但這本來就是遷移後的預期行為——FileLocker 那邊的密碼庫功能本來就要被 PasswordVault 取代。
/// 2. 新舊路徑都已經有資料時：新路徑優先，不覆蓋、不搬移，安靜略過。
/// </summary>
public static class LegacyDataMigration
{
    private const string CredentialsFileName = "credentials.json";

    /// <summary>回傳是否真的執行了搬移——呼叫端目前沒有 UI 可以顯示搬移結果，但保留回傳值
    /// 方便之後有設定頁／首次啟動畫面時可以顯示「已經幫你搬過舊資料」之類的提示。</summary>
    public static bool MigrateIfNeeded(string oldDataDirectory, string newDataDirectory)
    {
        var oldCredentialsPath = Path.Combine(oldDataDirectory, CredentialsFileName);
        if (!File.Exists(oldCredentialsPath))
        {
            return false;
        }

        var newCredentialsPath = Path.Combine(newDataDirectory, CredentialsFileName);
        if (File.Exists(newCredentialsPath))
        {
            return false;
        }

        Directory.CreateDirectory(newDataDirectory);
        foreach (var sourceFile in Directory.GetFiles(oldDataDirectory))
        {
            var destinationPath = Path.Combine(newDataDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationPath, overwrite: false);
        }

        return true;
    }
}
