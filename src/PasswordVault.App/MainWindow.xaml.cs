using System.Windows;

namespace PasswordVault.App;

/// <summary>
/// 骨架階段的最小視窗——真正的密碼庫畫面（WebView2 + 前端）留到 App.vue 共用元件拆分方式
/// 定案後再接上（見 PasswordVault_獨立化_規劃.md 第 3 節）。這個視窗目前只負責證明「單一執行
/// 個體、系統匣、視窗前景搶奪」這幾塊骨架能正常運作。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
