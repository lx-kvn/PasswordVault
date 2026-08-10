using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PasswordVault.App;

/// <summary>
/// 單純呼叫 Activate()（本質上是 SetForegroundWindow）在「被 Mutex 擋下來的行程透過 Named Pipe
/// 轉送訊號給已經在跑的實體」這條路徑上不可靠——轉送行程給的搶焦權限只在很短時間內有效，但從
/// Pipe 收到訊號到真正呼叫 Activate() 之間，往往還要先建構視窗，這段時間常常已經足夠讓權限失效。
///
/// 從 FileLocker repo 的 src/FileLocker.App/WindowActivation.cs 複製過來的獨立副本（跟
/// FileLocker.Core.Crypto.WindowFocusHelper 是同一套技巧，那邊是給 Passkey 驗證視窗用的，
/// 這裡兩個專案分屬不同組件，各自獨立實作一份，不特地共用）。
/// </summary>
internal static class WindowActivation
{
    public static void ForceToForeground(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Topmost = true;
        window.Topmost = false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ForceSetForegroundWindow(hwnd);
        }

        window.Activate();
    }

    private static bool ForceSetForegroundWindow(IntPtr hWnd)
    {
        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();

        if (foregroundWindow == IntPtr.Zero || foregroundWindow == hWnd)
        {
            return SetForegroundWindow(hWnd);
        }

        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (foregroundThreadId == currentThreadId)
        {
            return SetForegroundWindow(hWnd);
        }

        var attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
        try
        {
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
}
