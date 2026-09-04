using System.IO;
using Microsoft.Win32;

namespace PasswordVault.App;

/// <summary>
/// 瀏覽器擴充功能的 Native Messaging Host 註冊：把這個行程自帶的轉接程式
/// （PasswordVault.NativeHost.exe 及其相依檔案）放到跟 FileLocker.App 共用的位置，並寫入
/// 瀏覽器查得到的 manifest 與登錄機碼。
///
/// 共用位置的由來（見 FileLocker repo PasswordVault_獨立化_規劃.md 第 8.1 節）：
/// <c>FileLocker.App</c> 跟 <c>PasswordVault.exe</c> 過去各自帶一份自己的轉接程式副本、各自
/// 指向自己的路徑，登錄檔（每次啟動都自我修復覆寫，最後啟動的一方贏）跟 Named Pipe（先搶到
/// 的一方持有連線，最先啟動的一方贏）這兩套「贏家」判斷邏輯不一致時，Chrome 被登錄檔指向 A
/// 的轉接程式，但 Pipe 被 B 持有，B 的 <c>VerifyClientIsExpectedHost</c> 拿自己認得的路徑一
/// 比對，發現對不上就直接切斷連線（「Pipe is broken」）。兩邊改認同一個實體檔案、同一個路徑
/// 之後，不管誰贏得 Pipe、誰贏得登錄檔，雙方講的都是同一個地址。
///
/// **這個類別原本只負責複製、不寫登錄檔**，當時的理由是「FileLocker.App 那邊已經會寫，內容
/// 會收斂成同一個值」。那個理由只在使用者兩邊都裝的情況下成立：只裝 PasswordVault、不裝
/// FileLocker 的使用者，沒有任何一方會寫登錄機碼，瀏覽器擴充功能連要把轉接程式啟動起來都
/// 做不到（2026-09-04 在乾淨的 Windows 11 虛擬機上實測確認：兩個 App 輪流啟動四次，登錄機碼
/// 從頭到尾沒有被建立過）。因此這裡補上自己的註冊邏輯。
///
/// 兩邊同時要寫同一組登錄機碼、而那個機碼只有一個值的協調方式，是**讓兩邊寫出來的內容逐位元組
/// 相同**（見 <see cref="NativeHostRegistration"/>）——內容相同的話，「最後寫的贏」就不再有
/// 任何影響，不需要再發明一套跨行程的協商機制（跟第 8 節拒絕「強制接手 Pipe」是同一種判斷：
/// 不為了邊緣情境換取不成比例的架構成本）。manifest 檔案本身也放在共用位置，不放進任一邊自己
/// 的資料夾：放在 FileLocker 底下時，使用者移除 FileLocker、只留 PasswordVault，登錄機碼會
/// 指著一個已經被刪掉的檔案。
/// </summary>
internal static class PasswordVaultNativeHostSync
{
    private const string HostExeFileName = "PasswordVault.NativeHost.exe";

    /// <summary>轉接程式的相依檔案跟它同名不同副檔名（.dll／.deps.json／.runtimeconfig.json），
    /// 用前綴一起搬——少複製任何一個到共用位置，執行檔就會啟動失敗。</summary>
    private const string HostFilePattern = "PasswordVault.NativeHost.*";

    /// <summary>擴充功能識別碼，跟轉接程式一起發布（見 PasswordVault.NativeHost.csproj）。
    /// 找不到或內容是空的就安靜略過註冊，不當成錯誤——密碼庫其餘功能完全不受影響。</summary>
    private const string ExtensionIdFileName = "extension-id.txt";

    private const string RegistryKeyPath =
        @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostRegistration.HostName;

    /// <summary>共用位置固定選在跟兩邊安裝路徑都無關的 %LocalAppData%\PasswordVault\NativeHost\
    /// ——理由跟第 7 節「密碼庫資料改指向共用路徑」一致：不屬於任一邊的安裝資料夾，兩邊安裝、
    /// 解除安裝、版本升級都不會動到它。</summary>
    internal static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasswordVault", "NativeHost");

    /// <summary>即使共用位置還沒有實體檔案，這裡也回傳固定路徑字串，不做檔案是否存在的檢查
    /// ——Pipe Server 拿這個值比對的是路徑字串本身，不要求檔案當下存在。</summary>
    internal static string SharedExePath => Path.Combine(SharedDirectory, HostExeFileName);

    /// <summary>manifest 也放共用位置，兩邊寫的是同一個檔案（見類別開頭說明）。</summary>
    internal static string SharedManifestPath =>
        Path.Combine(SharedDirectory, $"{NativeHostRegistration.HostName}.json");

    /// <summary><paramref name="sourceDirectory"/> 是這個行程自己的目錄——轉接程式與
    /// extension-id.txt 都被複製到跟 PasswordVault.exe 同一層（見 PasswordVault.App.csproj
    /// 的 CopyNativeHostForRelease）。</summary>
    internal static void EnsureRegistered(string sourceDirectory)
    {
        EnsureSharedHostUpToDate(sourceDirectory);

        var extensionId = TryReadExtensionId(sourceDirectory);
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return;
        }

        WriteManifestAndRegistry(extensionId);
    }

    private static string? TryReadExtensionId(string sourceDirectory)
    {
        var path = Path.Combine(sourceDirectory, ExtensionIdFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>寫入 manifest 與登錄機碼。內容一律經由 <see cref="NativeHostRegistration"/>
    /// 算出，不在這裡自行組裝——那是兩邊內容保持一致的唯一保證。</summary>
    private static void WriteManifestAndRegistry(string extensionId)
    {
        try
        {
            Directory.CreateDirectory(SharedDirectory);

            var existing = TryReadAllText(SharedManifestPath);
            var ids = NativeHostRegistration.MergeAllowedExtensionIds(existing, extensionId);
            var manifestJson = NativeHostRegistration.BuildManifest(SharedExePath, ids);

            // 內容沒變就不重寫。兩邊算出來的內容相同時，這個比對也順便讓後啟動的一方
            // 什麼都不做，不會去動另一邊剛寫好的檔案時間戳記。
            if (existing != manifestJson)
            {
                File.WriteAllText(SharedManifestPath, manifestJson);
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (key.GetValue(null) as string != SharedManifestPath)
            {
                key.SetValue(null, SharedManifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>共用位置沒有轉接程式、或那份比手上這份舊時，複製過去（判斷邏輯與理由見
    /// <see cref="NativeHostRegistration.ShouldReplaceSharedHost"/>）。原本的規則是「有就
    /// 不動、不比對版本」，後果是共用位置永遠停在最早安裝的那個版本，之後任何一邊修好了
    /// 轉接程式都送不過去。
    ///
    /// 複製失敗安靜放棄——最常見的原因是瀏覽器正好把轉接程式叫起來、檔案被占用。轉接程式是
    /// 「用完就結束」的短命行程，下次啟動再試就會成功；這次失敗也不影響 Pipe Server 照常
    /// 監聽，共用位置留著的仍然是一份可用的舊版。</summary>
    private static void EnsureSharedHostUpToDate(string sourceDirectory)
    {
        var incoming = Path.Combine(sourceDirectory, HostExeFileName);
        var shouldReplace = NativeHostRegistration.ShouldReplaceSharedHost(
            File.Exists(SharedExePath),
            NativeHostRegistration.ReadFileVersion(SharedExePath),
            NativeHostRegistration.ReadFileVersion(incoming));

        if (!shouldReplace)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SharedDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, HostFilePattern))
            {
                var destinationFile = Path.Combine(SharedDirectory, Path.GetFileName(sourceFile));
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
        }
    }
}
