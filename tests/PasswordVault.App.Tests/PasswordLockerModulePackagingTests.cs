using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FileLocker.PluginContracts;

namespace PasswordVault.App.Tests;

/// <summary>
/// 「打包給 FileLocker 消費的部件」這件事的組裝規則。
///
/// FileLocker.App 從本 repo 的 Release 下載一個 zip、解壓縮成它自己的
/// <c>plugins/PasswordLocker/</c>，再用 <see cref="AssemblyLoadContext"/> 載入
/// <c>PasswordVault.Core.dll</c>（見 FileLocker repo 的 PasswordLockerPluginLoader.cs）。
/// 這條路徑跨兩個 repo，而且只有使用者按下「安裝密碼庫」時才會走到，漏掉一個檔案不會在建置
/// 或既有測試裡留下任何痕跡——使用者端看到的只有「部件壞了」這個狀態。因此把「zip 裡要有哪些
/// 檔案」與「這樣組出來真的載得起來、而且跑得動密碼雜湊」兩件事固定在這裡。
///
/// 為什麼要跑到密碼雜湊才算數：載入器解析相依組件用的是
/// <see cref="AssemblyDependencyResolver"/>，而 Konscious（Argon2／Blake2）只有在真的衍生金鑰
/// 時才會被載入。只做「載入部件、查詢憑證清單」這種操作完全碰不到它們，會把「相依缺檔」誤判
/// 成沒問題——2026-09-04 那輪虛擬機驗證就是這樣漏掉的。
///
/// 為什麼清單裡沒有 PasswordVault.Core.deps.json：實測顯示相依組件跟 PasswordVault.Core.dll
/// 放在同一個資料夾時，解析器直接就掃得到，不需要那份清單；而建置產出的那份 deps.json 內容
/// 指向 NuGet 快取裡的 lib/net8.0/ 路徑，在使用者機器上並不存在，放進去也起不了作用。本檔的
/// 載入測試就是固定住這個結論的迴歸測試。
///
/// 為什麼清單裡沒有 FileLocker.PluginContracts.dll：載入器對這個組件名強制退回宿主已經載入的
/// 那一份（兩份同名介面在 CLR 眼中是不同型別，轉型會失敗），部件自己帶一份不會有作用，理由見
/// vendor/README.md。
/// </summary>
public sealed class PasswordLockerModulePackagingTests
{
    /// <summary>
    /// zip 根目錄要有的檔案。缺任何一個的後果分別是：Core 本體與 Konscious 兩個檔缺了會在密碼
    /// 雜湊時炸；NativeHost 四個檔缺任何一個瀏覽器轉接程式都啟動不了；extension-id.txt 缺了
    /// FileLocker 那邊的登錄流程會安靜跳過，瀏覽器整合等於沒裝。
    /// </summary>
    private static readonly string[] ModuleFileNames =
    [
        "PasswordVault.Core.dll",
        "Konscious.Security.Cryptography.Argon2.dll",
        "Konscious.Security.Cryptography.Blake2.dll",
        "PasswordVault.NativeHost.exe",
        "PasswordVault.NativeHost.dll",
        "PasswordVault.NativeHost.deps.json",
        "PasswordVault.NativeHost.runtimeconfig.json",
        "extension-id.txt"
    ];

    /// <summary>載入部件本身需要的最小集合，載入測試只組這幾個檔就夠。</summary>
    private static readonly string[] LoadableSubset =
    [
        "PasswordVault.Core.dll",
        "Konscious.Security.Cryptography.Argon2.dll",
        "Konscious.Security.Cryptography.Blake2.dll"
    ];

    [Fact]
    public void 部件清單裡的每個檔案都在來源資料夾裡()
    {
        var source = GetModuleSourceDirectory();

        var missing = ModuleFileNames
            .Where(name => !File.Exists(Path.Combine(source, name)))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void 相依組件不在_PasswordVault_Core_自己的建置輸出裡()
    {
        // Library 專案的建置輸出不含攤平的相依組件，只有可執行專案才有。打包時來源抓錯資料夾會
        // 得到一個「看起來有 PasswordVault.Core.dll」但缺 Konscious 的部件，因此把這個差異固定
        // 下來，避免有人把打包來源改成 PasswordVault.Core 的 bin。
        var coreOutput = Path.Combine(
            GetRepositoryRoot(), "src", "PasswordVault.Core", "bin", GetConfiguration(), GetTargetFramework());

        Assert.True(Directory.Exists(coreOutput), $"找不到 PasswordVault.Core 的建置輸出：{coreOutput}");
        Assert.False(File.Exists(Path.Combine(coreOutput, "Konscious.Security.Cryptography.Argon2.dll")));
    }

    [Fact]
    public async Task 不含_deps_json_的部件也能完成_Argon2_金鑰衍生()
    {
        using var module = StageModule(LoadableSubset);
        Assert.False(File.Exists(Path.Combine(module.Path, "PasswordVault.Core.deps.json")));

        var (plugin, loadContext) = LoadPlugin(module.Path, module.DataPath);

        // 建立主密碼是真的會跑到 Argon2id 金鑰衍生的操作，不像查詢憑證清單那樣碰不到 Konscious。
        var response = await plugin.HandleRequestAsync(
            "setupPasswordLockerCredential",
            ParseRequest(SetupCredentialRequest),
            IntPtr.Zero);

        Assert.True(GetBooleanProperty(response, "Success"));

        // 只斷言「有跑成功」還不夠：宿主行程自己也帶著同名的 Konscious 組件，解析器找不到時會
        // 安靜退回宿主那一份，測試照樣會綠。要固定住的是「相依組件確實是從部件資料夾解析出來
        // 的」，這樣部件才真的自給自足，不會哪天宿主換版本就跟著壞。
        var resolved = Assert.Contains("Konscious.Security.Cryptography.Argon2", loadContext.ResolvedPaths);
        Assert.StartsWith(module.Path, resolved);
    }

    [Fact]
    public void 少了_Konscious_的部件解析不到相依組件()
    {
        // 這個測試存在的理由是「證明上一個測試驗得出差別」：如果少檔案時解析器照樣回報找得到，
        // 上一個測試的路徑斷言就沒有意義。
        //
        // 這裡刻意不驗「少了會炸」——宿主行程自己就帶著同名組件，少檔案時實際行為是安靜改用宿主
        // 那一份，當下不會有任何錯誤，測不出來。真正驗得到的差別在解析器這一層。
        using var module = StageModule(["PasswordVault.Core.dll"]);

        var resolver = new AssemblyDependencyResolver(Path.Combine(module.Path, "PasswordVault.Core.dll"));
        var argon2 = new AssemblyName("Konscious.Security.Cryptography.Argon2");

        Assert.Null(resolver.ResolveAssemblyToPath(argon2));
    }

    // ---- 輔助：組裝、載入、路徑 ----

    private const string SetupCredentialRequest =
        """{"type":"setupPasswordLockerCredential","password":"PackagingTest!2345"}""";

    private static StagedModule StageModule(IEnumerable<string> fileNames)
    {
        var source = GetModuleSourceDirectory();
        var staged = new StagedModule();

        foreach (var name in fileNames)
        {
            File.Copy(Path.Combine(source, name), Path.Combine(staged.Path, name));
        }

        return staged;
    }

    /// <summary>
    /// 複製 FileLocker.App 的 PasswordLockerLoadContext 載入流程。這裡不是「測試自己方便怎麼載
    /// 就怎麼載」——換一種載入方式（例如直接 Assembly.LoadFrom）就不會用到
    /// AssemblyDependencyResolver，也就驗不到「相依組件解析得到嗎」這件事本身。
    /// </summary>
    private static (IPasswordLockerPlugin Plugin, ModuleLoadContext LoadContext) LoadPlugin(
        string moduleDirectory, string dataDirectory)
    {
        var pluginDllPath = Path.Combine(moduleDirectory, "PasswordVault.Core.dll");
        var loadContext = new ModuleLoadContext(pluginDllPath);
        var assembly = loadContext.LoadFromAssemblyPath(pluginDllPath);

        var pluginType = assembly.GetTypes()
            .First(t => typeof(IPasswordLockerPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        var plugin = (IPasswordLockerPlugin)Activator.CreateInstance(pluginType)!;
        plugin.Initialize(new PasswordLockerPluginContext(dataDirectory, _ => false));
        return (plugin, loadContext);
    }

    private static JsonElement ParseRequest(string json) => JsonDocument.Parse(json).RootElement;

    private static bool GetBooleanProperty(object? response, string propertyName)
    {
        Assert.NotNull(response);
        var property = response.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return (bool)property.GetValue(response)!;
    }

    /// <summary>
    /// 部件檔案的來源資料夾。預設是本機 PasswordVault.App 的建置輸出；設了
    /// <c>PASSWORDVAULT_PUBLISHED_MODULE_DIR</c>（CI 的「驗證已發布的部件 zip」工作會設）時改用
    /// 那個資料夾，同一組測試就變成在驗「真的發布到 Release 上的那包內容」。
    ///
    /// 這裡不寫成兩套測試——本機建置輸出跟線上那包要滿足的條件完全一樣，寫成兩份遲早會分歧，
    /// 而分歧的那一份不會有人發現。
    /// </summary>
    private static string GetModuleSourceDirectory() =>
        Environment.GetEnvironmentVariable("PASSWORDVAULT_PUBLISHED_MODULE_DIR") is { Length: > 0 } published
            ? published
            : Path.Combine(
                GetRepositoryRoot(), "src", "PasswordVault.App", "bin", GetConfiguration(), GetTargetFramework());

    /// <summary>
    /// 測試組件位在 tests/PasswordVault.App.Tests/bin/&lt;組態&gt;/&lt;TFM&gt;/，往上五層就是 repo
    /// 根目錄。組態與 TFM 直接從這個路徑讀回來，Debug／Release 都能對應到同一組態的建置輸出。
    /// </summary>
    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string GetTargetFramework() =>
        new DirectoryInfo(AppContext.BaseDirectory).Name;

    private static string GetConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    private sealed class StagedModule : IDisposable
    {
        public StagedModule()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pv-module-" + Guid.NewGuid().ToString("N"));
            DataPath = System.IO.Path.Combine(Path, "data");
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(DataPath);
        }

        public string Path { get; }

        public string DataPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 載入過的 dll 還被行程鎖著時刪不掉（Windows 上回報的是「拒絕存取」而不是 IO
                // 錯誤，兩種都要接），殘留在暫存目錄裡不影響任何事，不值得讓測試因為清理失敗
                // 而變紅。
            }
        }
    }

    private sealed class ModuleLoadContext(string pluginPath)
        : AssemblyLoadContext("PasswordLockerPluginPackagingTest", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        /// <summary>
        /// 解析器實際找到的路徑，只有測試會用到——宿主那邊沒有這個欄位。載入成功與否看不出相依
        /// 組件是從哪裡來的，而「從部件資料夾來」正是這份測試要固定的事情。
        /// </summary>
        public Dictionary<string, string> ResolvedPaths { get; } = [];

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "FileLocker.PluginContracts" or "FileLocker.Core")
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null && assemblyName.Name is not null)
            {
                ResolvedPaths[assemblyName.Name] = path;
            }

            return path is not null ? LoadFromAssemblyPath(path) : null;
        }
    }
}
