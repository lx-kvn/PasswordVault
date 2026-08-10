using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using PasswordVault.Core;

namespace PasswordVault.App;

/// <summary>
/// 骨架階段的宿主進入點（見 PasswordVault_獨立化_規劃.md 第 14 節）：單一執行個體 Mutex、
/// 系統匣圖示、瀏覽器整合的 Named Pipe Server 都比照 FileLocker.App 現有模式，只是這裡沒有
/// FolderGuard／Vault／ShellExtension 這些 FileLocker 專屬功能，也還沒有真正的前端畫面
/// （WebView2 + Vue 共用元件的拆分方式留待另外規劃，見規劃文件第 3 節）。
///
/// 單一執行個體處理遵守 CLAUDE.md「已知的坑」的既有原則：偵測到 Mutex 已被持有時，不能直接
/// 結束或讓例外把行程弄崩潰，要想辦法把既有視窗搶到前景——這裡透過 Named Pipe 送一個「顯示
/// 視窗」訊號給第一個實體，由它自己呼叫 <see cref="WindowActivation.ForceToForeground"/>。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "PasswordVault-SingleInstance-Mutex";
    private const string SignalPipeName = "PasswordVault-SingleInstance-Pipe";
    private const string StartupArgFlag = "--startup";

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    private PasswordLockerPlugin? _passwordLockerPlugin;
    private PasswordVaultNativePipeServer? _passwordLockerPipeServer;
    private TrayIconManager? _trayIconManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            // 這個行程從來沒有真正拿到 Mutex 的所有權——OnExit 絕對不能對這個 Mutex 呼叫
            // ReleaseMutex，否則會因為「釋放一個自己沒有持有的鎖」丟出未處理例外，把這個
            // 原本只負責轉送訊號、馬上要結束的行程整個弄崩潰（FileLocker.App 曾經踩過這個坑，
            // 見 CLAUDE.md「已知的坑」）。
            TrySignalRunningInstance();
            Shutdown();
            return;
        }

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PasswordVault");
        Directory.CreateDirectory(appDataDir);

        // TODO（骨架階段先不做）：偵測 %AppData%\FileLocker\PasswordLocker\ 底下有沒有舊資料、
        // 這裡的新路徑是空的的話自動搬過去（見 PasswordVault_獨立化_規劃.md 第 7 節）。
        // 失敗時如何提示使用者、兩邊都有資料時怎麼處理，規劃文件明確留到動工前另外定案，
        // 這輪骨架階段還沒有 UI 可以顯示這類提示，先不接這塊邏輯，避免不完整的搬移邏輯
        // 沒有對應的錯誤顯示管道。
        var passwordLockerDir = Path.Combine(appDataDir, "PasswordLocker");
        _passwordLockerPlugin = new PasswordLockerPlugin();
        _passwordLockerPlugin.Initialize(new FileLocker.PluginContracts.PasswordLockerPluginContext(
            passwordLockerDir,
            // 獨立版預設沒有「已加密檔案」這個分類可以關聯（見規劃文件第 6 節：只有偵測到
            // FileLocker 主體也裝在同一台機器上才顯示，這個偵測邏輯本身也還沒接上），
            // 固定回傳 false——不影響一般網站帳密憑證的任何功能。
            vaultItemExists: _ => false));

        _passwordLockerPipeServer = new PasswordVaultNativePipeServer(
            () => _passwordLockerPlugin,
            RequestBrowserVerificationAsync,
            () => Dispatcher.InvokeAsync(() => ShowMainWindow()).Task,
            Path.Combine(AppContext.BaseDirectory, "PasswordVault.NativeHost.exe"));
        _passwordLockerPipeServer.Start();

        StartSignalPipeListener();

        CreateTrayIcon();

        if (e.Args is not [StartupArgFlag])
        {
            ShowMainWindow();
        }
    }

    /// <summary>瀏覽器擴充功能觸發、但密碼庫尚未通過驗證時呼叫。真正的驗證彈窗（比照
    /// FileLocker.App 的 PasswordLockerBrowserVerifyWindow）要等前端／UI 拆分方式定案後才會
    /// 動工（見類別開頭說明）——骨架階段固定回傳「無法驗證」，不假裝這塊已經做完。</summary>
    private static Task<bool> RequestBrowserVerificationAsync(string domain, string? targetDomain)
        => Task.FromResult(false);

    protected override void OnExit(ExitEventArgs e)
    {
        _passwordLockerPipeServer?.Stop();
        _trayIconManager?.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        _trayIconManager = new TrayIconManager(
            openMainWindow: ShowMainWindow,
            exitApplication: ExitApplicationFromTray);
    }

    private void ShowMainWindow()
    {
        var existingMainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
        if (existingMainWindow is not null)
        {
            WindowActivation.ForceToForeground(existingMainWindow);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        WindowActivation.ForceToForeground(mainWindow);
    }

    private void ExitApplicationFromTray()
    {
        _trayIconManager?.Dispose();
        _trayIconManager = null;
        Shutdown();
    }

    /// <summary>第一個實體背景監聽：等待被 Mutex 擋下來的行程送來的「顯示視窗」訊號。
    /// 骨架階段還沒有右鍵選單／檔案關聯這類複雜的啟動參數要轉送，訊號本身不帶任何內容，
    /// 收到就切回 UI 執行緒叫出視窗——之後如果加了需要轉送參數的啟動路徑（例如未來的
    /// CLI 快捷操作），這裡要比照 FileLocker.App 的 HandleLaunchArgs 模式擴充。</summary>
    private void StartSignalPipeListener()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        SignalPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();

                    Dispatcher.Invoke(ShowMainWindow);
                }
                catch (Exception)
                {
                    // 背景監聽迴圈本身不能因為單次連線失敗就整個停掉，吞掉繼續等下一次連線。
                }
            }
        });
    }

    private static void TrySignalRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SignalPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write("show");
            writer.Flush();

            // 見 WindowActivation 上的說明：這個轉送行程握有前景權限，開放給任何行程用，
            // 讓舊行程接下來呼叫的 Activate() 真的能生效。
            AllowSetForegroundWindow(AsfwAny);
        }
        catch (Exception)
        {
            // 轉送失敗就放棄，這次操作沒反應，比意外開出第二個視窗互相打架更容易處理。
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
