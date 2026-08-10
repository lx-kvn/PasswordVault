using System.Runtime.InteropServices;

namespace PasswordVault.Core.Crypto;

/// <summary>
/// 未封裝的桌面應用程式呼叫 Windows Hello 相關 API 時，系統跳出的驗證視窗沒有正式的視窗擁有
/// （ownership）關係，會有跳到背景、輸入框沒有自動取得焦點、驗證結束後焦點沒有還給呼叫端這幾個症狀。
///
/// PrepareForegroundHandoff／ReclaimForeground 是第一層緩解（讓自己的視窗先搶到前景、開放接下來
/// 的新視窗也能搶焦點），PromoteNewForeignWindowAsync 是更直接的第二層做法：主動輪詢找出「觸發
/// 驗證後新出現、不屬於自己程式」的視窗，抓到就直接強制釘到最上層、搶前景。
///
/// 從 FileLocker repo 的 src/FileLocker.Core/Crypto/WindowFocusHelper.cs 複製過來的獨立副本，
/// 理由同 KeyDerivation.cs 開頭的說明。
/// </summary>
public static class WindowFocusHelper
{
    private const uint AsfwAny = 0xFFFFFFFF;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    private static readonly object PromotionLock = new();
    private static readonly HashSet<IntPtr> PromotedWindows = new();
    private static bool _promotionSuspended;

    public static void PrepareForegroundHandoff(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle != IntPtr.Zero)
        {
            ForceSetForegroundWindow(ownerWindowHandle);
        }

        AllowSetForegroundWindow(AsfwAny);
    }

    public static void ReclaimForeground(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle != IntPtr.Zero)
        {
            ForceSetForegroundWindow(ownerWindowHandle);
        }
    }

    /// <summary>
    /// 繞過 Windows 的防搶焦點限制：暫時把呼叫端（我們自己）執行緒的輸入佇列跟目前前景視窗
    /// 的執行緒接在一起（AttachThreadInput），系統就會允許我們呼叫 SetForegroundWindow 生效，
    /// 結束後立刻解除接合，不影響其他視窗之間的正常輸入隔離。
    /// </summary>
    private static bool ForceSetForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

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

    /// <summary>
    /// 背景輪詢最多 60 秒，找出「觸發驗證後新出現」的可見視窗，找到就強制釘到最上層＋搶前景。
    /// 透過 CancellationToken 在驗證完成（不管成功失敗）時提前停止，不會一直空轉到 60 秒逾時。
    /// </summary>
    public static async Task PromoteNewForeignWindowAsync(CancellationToken cancellationToken)
    {
        var before = EnumerateVisibleTopLevelWindows();
        var deadline = DateTime.UtcNow.AddSeconds(60);

        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var current = EnumerateVisibleTopLevelWindows();

                lock (PromotionLock)
                {
                    // 已經追蹤到、但已經關掉的視窗要從清單移除，不然這個集合只會越滾越大。
                    PromotedWindows.RemoveWhere(hwnd => !current.Contains(hwnd));

                    // Windows Hello 的驗證 UI 常常是分階段的多個視窗，持續掃描＋持續重新置頂
                    // 「目前還存在的所有新視窗」，不限一個。
                    foreach (var hwnd in current)
                    {
                        if (before.Contains(hwnd))
                        {
                            continue;
                        }
                        PromotedWindows.Add(hwnd);
                    }

                    if (!_promotionSuspended)
                    {
                        foreach (var hwnd in PromotedWindows)
                        {
                            ApplyTopmost(hwnd);
                        }
                    }
                }

                try
                {
                    await Task.Delay(50, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (PromotionLock)
            {
                PromotedWindows.Clear();
                _promotionSuspended = false;
            }
        }
    }

    private static void ApplyTopmost(IntPtr hwnd)
    {
        // 先降回非置頂再重新升成置頂——單純重複呼叫「設成置頂」有時候會被 Windows
        // 忽略（z-band 判定被快取住，尤其這種系統层級的 Windows Hello 對話框），
        // 先降再升是強迫視窗管理員重新處理這個視窗所在 z-band 的已知技巧。
        SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        ForceSetForegroundWindow(hwnd);
    }

    /// <summary>暫時把目前正在維持置頂的視窗（Windows Hello 對話框）降回非置頂——一個真正
    /// 置頂的視窗，不管另一個視窗怎麼搶輸入焦點都不可能被疊到它上面，這是 Windows z-order
    /// 的硬規則。呼叫端要在做完想做的事之後呼叫 <see cref="ResumePromotion"/> 還原。</summary>
    public static void SuspendPromotion()
    {
        lock (PromotionLock)
        {
            _promotionSuspended = true;
            foreach (var hwnd in PromotedWindows)
            {
                SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
            }
        }
    }

    /// <summary>還原 <see cref="SuspendPromotion"/> 暫停之前的置頂狀態。</summary>
    public static void ResumePromotion()
    {
        lock (PromotionLock)
        {
            _promotionSuspended = false;
            foreach (var hwnd in PromotedWindows)
            {
                ApplyTopmost(hwnd);
            }
        }
    }

    private static HashSet<IntPtr> EnumerateVisibleTopLevelWindows()
    {
        var windows = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
