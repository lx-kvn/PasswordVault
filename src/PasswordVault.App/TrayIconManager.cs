using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PasswordVault.App;

/// <summary>
/// 系統匣圖示——背景模式開啟時，關閉主視窗不會結束程式，改成留在這裡。
///
/// 這輪是骨架階段（見 PasswordVault_獨立化_規劃.md 第 14 節），右鍵選單先用
/// System.Windows.Forms.ContextMenuStrip（WinForms 內建、幾行就能用），不是 FileLocker.App
/// 那個自製的 WPF 圓角視窗（TrayMenuWindow）——那個做法是為了解決 ContextMenuStrip 在 DWM
/// 圓角下的殘影問題，這輪還沒有真正的 UI 可以切換分頁，先用最簡單、能動的版本，等前端／
/// 分頁架構定案後再比照 FileLocker.App 的做法升級。
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconManager(Action openMainWindow, Action exitApplication)
    {
        // 直接從執行檔本身抽出已經內嵌的圖示（ApplicationIcon 編譯時期就打包進 exe 資源），
        // 跟 FileLocker.App.TrayIconManager 同一個做法，不用另外複製一份 .ico 檔到輸出目錄。
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PasswordVault.exe");
        var icon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "PasswordVault",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("開啟 PasswordVault", null, (_, _) => openMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束 PasswordVault", null, (_, _) => exitApplication());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => openMainWindow();
    }

    public void Dispose()
    {
        // Visible = false 要在 Dispose() 之前——NotifyIcon 的圖示殘影有時候要等下一次滑鼠移過去
        // 才會消失，先明確關閉可見性可以避免這個殘影更明顯（跟 FileLocker.App 同一個做法）。
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
