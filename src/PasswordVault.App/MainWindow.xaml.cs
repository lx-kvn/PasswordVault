using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FileLocker.PluginContracts;
using Microsoft.Web.WebView2.Core;

namespace PasswordVault.App;

/// <summary>
/// 接上 WebView2、顯示 PasswordVault.Web 的真實密碼庫畫面（見排程批次 1）。WebView2 初始化／
/// Debug-Release 導覽切換／訊息收送整段照抄 FileLocker.App 既有、已經上線驗證過的模式
/// （FileLocker.App/MainWindow.xaml.cs），差異只在這裡沒有 Vault／FolderGuard 這些功能，
/// 訊息分派單純很多——WebView2 送過來的每一則訊息幾乎都直接轉發給內建的 <see cref="IPasswordLockerPlugin"/>。
/// </summary>
public partial class MainWindow : Window
{
    // Release 建置時 SetVirtualHostNameToFolderMapping 用的虛擬主機名稱，純粹是本機識別用，
    // 不是真的網域，不需要真的擁有或註冊這個名稱。
    private const string AppOrigin = "passwordvault.local";

    private readonly IPasswordLockerPlugin _passwordLockerPlugin;

    // WebView2 的 CoreWebView2 是非同步初始化，理由跟 FileLocker.App 同一個既有考量：
    // CoreWebView2 還沒準備好之前送出的訊息先進佇列，NavigationCompleted 那一刻再依序補送，
    // 不遺失、也不會對還是 null 的 CoreWebView2 呼叫而崩潰。
    private readonly List<object> _pendingFrontendMessages = new();
    private bool _frontendReady;

    private static readonly JsonSerializerOptions SendToFrontendJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MainWindow(IPasswordLockerPlugin passwordLockerPlugin)
    {
        InitializeComponent();

        _passwordLockerPlugin = passwordLockerPlugin;

        Loaded += async (_, _) =>
        {
            // 明確指定使用者資料目錄，不依賴 WebView2 預設「在執行檔旁邊建資料夾」的行為——
            // 理由跟 FileLocker.App 一致：安裝到系統保護目錄時，一般使用者權限沒辦法在執行檔
            // 旁邊寫入。app 名稱換成 PasswordVault，避免跟 FileLocker.App 那份使用者資料目錄
            // 混在一起（兩支程式可能裝在同一台機器上）。
            var webView2UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PasswordVault", "WebView2");
            var webView2Environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webView2UserDataFolder);
            await MainWebView.EnsureCoreWebView2Async(webView2Environment);

            // WebView2 安全性硬化，跟 FileLocker.App 同一套理由：密碼庫的密碼輸入框不該被
            // Chromium 內建密碼管理員另外存一份；桌面應用程式不該有瀏覽器式的縮放手勢。
            MainWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            MainWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            MainWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
#if DEBUG
            MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

            // 右鍵選單：只有點在可編輯欄位上才保留瀏覽器預設的剪下/複製/貼上選單，其餘一律不顯示
            // ——理由跟 FileLocker.App 一致，這不是瀏覽器，不需要「上一頁」「檢視原始碼」這類項目。
            MainWebView.CoreWebView2.ContextMenuRequested += (_, ctxArgs) =>
            {
                if (!ctxArgs.ContextMenuTarget.IsEditable)
                {
                    ctxArgs.Handled = true;
                }
            };

            // 導覽限制：只允許導覽到預期的網址，其餘一律擋下——理由跟 FileLocker.App 一致。
            MainWebView.CoreWebView2.NavigationStarting += (_, navArgs) =>
            {
#if DEBUG
                var isAllowed = navArgs.Uri.StartsWith("http://localhost:5183/", StringComparison.Ordinal);
#else
                var isAllowed = navArgs.Uri.StartsWith($"https://{AppOrigin}/", StringComparison.Ordinal);
#endif
                if (!isAllowed)
                {
                    navArgs.Cancel = true;
                }
            };

            // 擋掉 window.open()／target="_blank" 開新視窗——理由跟 FileLocker.App 一致。
            MainWebView.CoreWebView2.NewWindowRequested += (_, newWindowArgs) =>
            {
                newWindowArgs.Handled = true;
            };

            // 剪貼簿權限（密碼庫「複製」按鈕用的 navigator.clipboard.writeText）自動核准——
            // 理由跟 FileLocker.App 一致，載入的內容全部是我們自己的前端，不是外部網站。
            MainWebView.CoreWebView2.PermissionRequested += (_, permArgs) =>
            {
                if (permArgs.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    permArgs.State = CoreWebView2PermissionState.Allow;
                }
            };

#if DEBUG
            // Debug 建置：連到 Vite 開發伺服器，需要另外開一個終端機跑 npm run dev
            // （PasswordVault.Web/vite.config.js 已經把埠固定成 5183，跟 FileLocker.Web
            // 常用的 5173 分開，避免兩個 repo 同時開著時連錯埠）。
            MainWebView.CoreWebView2.Navigate("http://localhost:5183/");
#else
            // Release 建置：直接從封裝好的靜態檔案載入，不透過任何本機網路埠——
            // webapp 資料夾由 PasswordVault.App.csproj 的 Release 建置流程自動產生
            // （npm run build + 複製檔案）。
            var webAppFolder = Path.Combine(AppContext.BaseDirectory, "webapp");
            if (!File.Exists(Path.Combine(webAppFolder, "index.html")))
            {
                MessageBox.Show(
                    $"找不到前端畫面檔案（預期位置：{webAppFolder}）。\n\n" +
                    "如果是開發階段自己編譯的 Release 版本，請確認 PasswordVault.App.csproj 的建置流程有" +
                    "成功執行 npm run build，並把輸出複製到這個資料夾。",
                    "PasswordVault",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            MainWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppOrigin, webAppFolder, CoreWebView2HostResourceAccessKind.Deny);
            MainWebView.CoreWebView2.Navigate($"https://{AppOrigin}/index.html");
#endif
            MainWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MainWebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    return;
                }

                // 補送 CoreWebView2 準備好之前排隊的訊息——理由跟 FileLocker.App 一致。
                _frontendReady = true;
                if (_pendingFrontendMessages.Count > 0)
                {
                    var pending = _pendingFrontendMessages.ToArray();
                    _pendingFrontendMessages.Clear();
                    foreach (var message in pending)
                    {
                        SendToFrontend(message);
                    }
                }
            };
        };
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        // 密碼庫是內建功能，不是外部部件——這個訊息問的是「部件有沒有裝、狀態正不正常」，
        // 對 PasswordVault.exe 來說永遠是「有、正常」，不轉發、直接硬編碼回答（跟
        // FileLocker.App 攔截同一則訊息、但回答邏輯不同：那邊真的要查外部部件狀態）。
        if (type == "getPasswordLockerModuleStatus")
        {
            SendToFrontend(new { type = "passwordLockerModuleStatusResult", status = "ok" });
            return;
        }

        // CSV 存檔/開檔是 WPF 平台能力，跟 FileLocker.App 同一個既有分工：密碼庫引擎
        // （PasswordLockerPlugin）只負責 CSV 內容的加解密/解析，跟磁碟打交道的部分留在這裡。
        if (type == "savePasswordLockerCsvToFile")
        {
            HandleSavePasswordLockerCsvToFileRequest(root);
            return;
        }
        if (type == "pickAndImportPasswordLockerCsv")
        {
            await HandlePickAndImportPasswordLockerCsvRequestAsync();
            return;
        }

        // 這三個訊息名稱含 "PasswordLocker"，但語意是「另外裝一個部件」——PasswordVault.exe
        // 內建 PasswordVault.Core，沒有這個概念，不轉發給 plugin（plugin 也不認得這三個
        // 訊息類型）。直接回對應的 xxxResult、success = false，讓套件既有的罐頭失敗提示照常
        // 顯示，不會卡住、不會崩潰。已知限制：這會讓「解除安裝密碼庫部件」按鈕顯示一個語意
        // 不太精確的失敗提示，而不是「這個按鈕在獨立版不適用」——根治需要回頭改共用套件加一個
        // prop 隱藏這塊 UI，留到之後批次一併處理，見排程批次 1 的規劃紀錄。
        if (type == "checkForPasswordLockerModuleUpdate")
        {
            SendToFrontend(new { type = "checkForPasswordLockerModuleUpdateResult", success = false });
            return;
        }
        if (type == "installPasswordLockerModuleUpdate")
        {
            SendToFrontend(new { type = "installPasswordLockerModuleUpdateResult", success = false });
            return;
        }
        if (type == "uninstallPasswordLockerModule")
        {
            SendToFrontend(new { type = "uninstallPasswordLockerModuleResult", success = false });
            return;
        }

        if (type is not null && type.Contains("PasswordLocker", StringComparison.Ordinal))
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var response = await _passwordLockerPlugin.HandleRequestAsync(type, root, hwnd);
            if (response is not null)
            {
                SendToFrontend(response);
                return;
            }

            // plugin 認不得這個訊息類型：GUI 跟 Core 是同一次編譯出來的，理論上不會發生
            // （不像 FileLocker.App 的外部部件可能版本不同步）。不能照抄 FileLocker.App 那個
            // 通用 { type = "error" } 回應——packages/password-locker-ui 沒有 FileLocker.Web
            // rejectAllPending 那種全域收播機制，送一個套件沒在等的型別，等於讓那次請求永遠
            // 卡住。這裡只記一筆 log，讓那次操作維持原狀（使用者可以重試或換個動作），比送出
            // 一個保證解不開任何 pending promise 的訊息更安全。
            Debug.WriteLine($"[PasswordVault] PasswordLockerPlugin 不認得這個操作：{type}");
        }
    }

    /// <summary>密碼庫 CSV 匯出：部件只負責把明文 CSV 內容組好、送回來，實際寫進磁碟的原生
    /// 存檔對話框由這裡處理——跟 FileLocker.App 的
    /// HandleSavePasswordLockerCsvToFileRequest 同一個分工慣例。</summary>
    private void HandleSavePasswordLockerCsvToFileRequest(JsonElement request)
    {
        try
        {
            var content = request.GetProperty("content").GetString() ?? "";

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "匯出密碼庫",
                FileName = "PasswordVault-密碼庫.csv",
                Filter = "CSV 檔 (*.csv)|*.csv|所有檔案 (*.*)|*.*",
                DefaultExt = ".csv"
            };

            if (dialog.ShowDialog(this) == true)
            {
                File.WriteAllText(dialog.FileName, content);
                SendToFrontend(new { type = "savePasswordLockerCsvToFileResult", success = true, path = dialog.FileName });
            }
            else
            {
                SendToFrontend(new { type = "savePasswordLockerCsvToFileResult", success = false, cancelled = true });
            }
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "savePasswordLockerCsvToFileResult", success = false, errorMessage = ex.Message });
        }
    }

    /// <summary>密碼庫 CSV 匯入：原生開檔對話框選 CSV 檔案、讀取內容，轉發給 plugin 的
    /// importPasswordLockerCsv 做實際解析/加密/寫入——跟 FileLocker.App 的
    /// HandlePickAndImportPasswordLockerCsvCoreAsync 同一個分工慣例。</summary>
    private async Task HandlePickAndImportPasswordLockerCsvRequestAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "匯入密碼庫 CSV",
                CheckFileExists = true,
                Filter = "CSV 檔 (*.csv)|*.csv|所有檔案 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                SendToFrontend(new { type = "importPasswordLockerCsvResult", success = false, cancelled = true });
                return;
            }

            var csv = File.ReadAllText(dialog.FileName);
            var hwnd = new WindowInteropHelper(this).Handle;
            var request = JsonDocument.Parse(JsonSerializer.Serialize(new { csv })).RootElement;
            var response = await _passwordLockerPlugin.HandleRequestAsync("importPasswordLockerCsv", request, hwnd);
            if (response is not null)
            {
                SendToFrontend(response);
            }
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "importPasswordLockerCsvResult", success = false, errorMessage = ex.Message });
        }
    }

    private void SendToFrontend(object message)
    {
        if (!_frontendReady || MainWebView.CoreWebView2 is null)
        {
            _pendingFrontendMessages.Add(message);
            return;
        }
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, SendToFrontendJsonOptions));
    }
}
