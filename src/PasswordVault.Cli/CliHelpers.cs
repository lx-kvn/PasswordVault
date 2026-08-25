using System.Text;
using PasswordVault.Core;

namespace PasswordVault.Cli;

/// <summary>
/// 從 <c>Program.cs</c>（top-level statements，外部測試專案碰不到裡面的 local function）抽出來的
/// 兩塊純邏輯，補測試用（見 PasswordVault_獨立化_規劃.md 第 17 節「測試覆蓋補齊」這輪定案）——
/// 只抽這兩塊，互動流程本身、實際呼叫 PasswordVault.Core 驗證主密碼的部分留在 Program.cs 不動。
/// </summary>
public static class CliHelpers
{
    /// <summary>互動輸入主密碼，逐字元遮罩顯示成 <c>*</c>；標準輸入被重新導向時（腳本管線）
    /// <see cref="Console.ReadKey()"/> 會直接丟例外，改退回逐行讀取。這不是替「非互動模式」
    /// 開後門：沒有任何指令支援用這種方式跳過互動提示，單純是讓「使用者透過某種終端機模擬工具
    /// 間接輸入」這種正常情境還能動。
    ///
    /// <paramref name="isInputRedirected"/>／<paramref name="redirectedReader"/> 預設吃真正的
    /// <see cref="Console.IsInputRedirected"/>／<see cref="Console.In"/>——這兩個是刻意抽出來的
    /// 依賴，不是內部寫死呼叫，因為 <see cref="Console.IsInputRedirected"/> 判斷的是行程實際的
    /// 標準輸入控制代碼，<see cref="Console.SetIn"/> 換掉 <see cref="Console.In"/> 並不會讓它
    /// 跟著變，測試沒辦法只靠換讀取來源就切到這個分支，只能整個判斷結果一起傳進來。</summary>
    public static string ReadPasswordMasked(bool? isInputRedirected = null, TextReader? redirectedReader = null)
    {
        if (isInputRedirected ?? Console.IsInputRedirected)
        {
            return (redirectedReader ?? Console.In).ReadLine() ?? "";
        }

        var password = new StringBuilder();
        ConsoleKeyInfo key;

        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }

        return password.ToString();
    }

    /// <summary>`--list` 一筆憑證要印出的所有行，不含密碼（見 <see cref="PasswordCredentialMetadata"/>
    /// 本身就不帶密碼的既有設計）。回傳陣列而不是單一字串，方便測試逐行斷言；呼叫端自己決定
    /// 要不要在每筆之間多印一行空白（Program.cs 原本的排版習慣）。</summary>
    public static string[] FormatCredentialLines(PasswordCredentialMetadata entry)
    {
        var lines = new List<string>
        {
            $"{entry.Id}  [{entry.Category}]  {entry.Title}"
        };

        if (entry.AssociatedDomains.Count > 0)
        {
            lines.Add($"    關聯網站：{string.Join("、", entry.AssociatedDomains)}");
        }

        lines.Add($"    帳號：{(entry.UsernameHidden ? "（已隱藏，需驗證後查看）" : entry.Username)}");

        return lines.ToArray();
    }
}
