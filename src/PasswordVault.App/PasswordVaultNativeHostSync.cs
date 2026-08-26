using System.IO;

namespace PasswordVault.App;

/// <summary>
/// 把這個行程自帶的 Native Messaging Host 轉接程式（PasswordVault.NativeHost.exe 及其相依檔案）
/// 複製到跟 FileLocker.App 共用的位置（見 FileLocker repo PasswordVault_獨立化_規劃.md 第 8.1
/// 節）——根因是 <c>FileLocker.App</c> 跟 <c>PasswordVault.exe</c> 過去各自帶一份自己的轉接程式
/// 副本、各自指向自己的路徑，登錄檔（每次啟動都自我修復覆寫，最後啟動的一方贏）跟 Named Pipe
/// （先搶到的一方持有連線，最先啟動的一方贏）這兩套「贏家」判斷邏輯不一致時，Chrome 被登錄檔
/// 指向 A 的轉接程式，但 Pipe 被 B 持有，B 的 <c>VerifyClientIsExpectedHost</c> 拿自己認得的
/// 路徑一比對，發現對不上就直接切斷連線（「Pipe is broken」）。
///
/// 修正方向：不管誰的 Pipe、誰的登錄檔贏，雙方認的都是同一個實體檔案、同一個路徑——
/// <see cref="SharedExePath"/> 是這個共用位置的固定路徑，兩邊 Pipe Server 建構時的
/// expectedClientExePath 都指向這裡。
///
/// <c>PasswordVault.exe</c> 自己的 Native Messaging Host 登錄（寫 manifest、登記
/// <c>HKCU\...\NativeMessagingHosts\...</c>）目前還沒有實作（見規劃文件「待辦事項」一節）——
/// 這裡只負責「把轉接程式複製到共用位置」跟「Pipe Server 認共用位置」這兩件事，不牽動登錄檔。
/// <c>FileLocker.App</c> 那邊的 <c>PasswordLockerNativeHostRegistrar</c> 已經會把登錄檔指向
/// 這個共用路徑，兩邊寫入的內容因此收斂成同一個值，不需要 <c>PasswordVault.exe</c> 也重複寫一次。
/// </summary>
internal static class PasswordVaultNativeHostSync
{
    private const string HostExeFileName = "PasswordVault.NativeHost.exe";

    /// <summary>共用位置固定選在跟兩邊安裝路徑都無關的 %LocalAppData%\PasswordVault\NativeHost\
    /// ——理由跟第 7 節「密碼庫資料改指向共用路徑」一致：不屬於任一邊的安裝資料夾，兩邊安裝、
    /// 解除安裝、版本升級都不會動到它。</summary>
    internal static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasswordVault", "NativeHost");

    /// <summary>即使共用位置還沒有實體檔案，這裡也回傳固定路徑字串，不做檔案是否存在的檢查
    /// ——Pipe Server 拿這個值比對的是路徑字串本身，不要求檔案當下存在，跟原本直接指向
    /// AppContext.BaseDirectory 那份的行為一致。</summary>
    internal static string SharedExePath => Path.Combine(SharedDirectory, HostExeFileName);

    /// <summary>誰先啟動就負責複製，共用位置已經有轉接程式的話什麼都不做——不比對版本新舊，
    /// 用最簡單的「有就不動」規則（規劃文件第 8.1 節定案的方向：「誰先啟動，就負責把轉接程式
    /// 複製到這個共用位置（如果還沒有的話）」，沒有要求比對版本）。複製失敗（檔案被占用、權限
    /// 問題等）安靜放棄——共用位置多半已經有另一邊複製好的版本，這裡失敗不影響 Pipe Server
    /// 照常啟動監聽，只是這次連線驗證會因為共用位置真的沒有檔案而全部失敗，等下次啟動再重試。</summary>
    internal static void EnsureCopiedFrom(string sourceDirectory)
    {
        if (File.Exists(SharedExePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SharedDirectory);
            // 比對前綴而不是只複製 HostExeFileName 本身——Native Host 是完整的 .NET 執行檔，
            // 還帶著自己的 .dll／.deps.json／.runtimeconfig.json，少複製任何一個到共用位置，
            // 執行檔就會啟動失敗。
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "PasswordVault.NativeHost.*"))
            {
                var destinationFile = Path.Combine(SharedDirectory, Path.GetFileName(sourceFile));
                File.Copy(sourceFile, destinationFile, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
