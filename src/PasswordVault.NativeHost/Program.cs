using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

// Chrome Native Messaging Host：純轉接層，不含任何密碼庫業務邏輯。Chrome 每次
// chrome.runtime.connectNative() 就會啟動一個這支程式的新進程，透過 stdin/stdout 溝通
// （4-byte little-endian 長度前綴 + UTF-8 JSON，Chrome 官方標準格式）；這裡原封不動把訊息
// 轉發到「FileLocker.App 或 PasswordVault.exe，兩者之中先啟動、先搶到這條 Named Pipe 的
// 那一邊」（見 PasswordVault_獨立化_規劃.md 第 8 節「共存」，兩邊搶同一條 Pipe、不強制接手），
// 回應也原封不動轉發回 stdout。真正的驗證/加解密/UI 全部留在宿主程式那一側，這支程式沒有、
// 也不該有任何那類邏輯。
//
// 這是從 FileLocker repo 的 src/FileLocker.PasswordLockerNativeHost/Program.cs 複製過來的
// 獨立副本（那個專案沒有被 git filter-repo 遷移過來，PasswordVault 需要自己重新寫一份轉接層，
// 見 PasswordVault_獨立化_規劃.md 第 8 節）——刻意跟 FileLocker 那份功能完全對等、只換掉
// TryLaunchInBackground 找的執行檔名稱，因為兩邊各自只需要負責「自己這邊沒開的話，
// 幫忙背景啟動自己這邊」，不需要互相知道對方的存在或安裝路徑。
//
// Pipe 名稱刻意維持跟 FileLocker 那份轉接層完全相同的 "FileLocker-PasswordLocker-Pipe"——
// 這條 Pipe 名稱是兩邊宿主程式共用的識別碼，不是「屬於 FileLocker」的名稱，換一個新名稱會讓
// 這支轉接層連不到「先搶到 Pipe 的另一邊」。

const string PipeName = "FileLocker-PasswordLocker-Pipe";
const int MaxMessageBytes = 10 * 1024 * 1024;

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

while (true)
{
    var message = await ReadFramedAsync(stdin);
    if (message is null)
    {
        // Chrome 關閉了這條連線（分頁關掉、擴充功能重載、瀏覽器結束等）——stdin 讀到 EOF，
        // 這支進程的任務就結束了，直接退出，不用自己做任何清理，生命週期完全交給 Chrome。
        return;
    }

    byte[] response;
    try
    {
        response = await ForwardToAppAsync(message);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
    {
        response = ErrorPayload(ex.Message);
    }

    await WriteFramedAsync(stdout, response);
}

static async Task<byte[]?> ReadFramedAsync(Stream stream)
{
    var lengthBuffer = new byte[4];
    if (!await ReadExactAsync(stream, lengthBuffer))
    {
        return null;
    }

    var length = BitConverter.ToInt32(lengthBuffer, 0);
    if (length <= 0 || length > MaxMessageBytes)
    {
        return null;
    }

    var buffer = new byte[length];
    return await ReadExactAsync(stream, buffer) ? buffer : null;
}

static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
        if (read == 0)
        {
            return false;
        }
        offset += read;
    }
    return true;
}

static async Task WriteFramedAsync(Stream stream, byte[] payload)
{
    await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
    await stream.WriteAsync(payload);
    await stream.FlushAsync();
}

// 用 JsonSerializer 組錯誤訊息，保證輸出永遠是合法 JSON（手刻字串插值容易在訊息裡出現反斜線、
// 引號等未跳脫字元時產生格式不正確的 JSON，這是 FileLocker 那份轉接層曾經踩過的坑）。
static byte[] ErrorPayload(string message)
    => JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message });

/// <summary>連不上 Named Pipe 就代表兩邊宿主程式都沒在跑——自動在背景安靜啟動 PasswordVault.exe
/// （沿用 --startup 旗標，不開視窗、只留系統匣圖示），重試幾次等它把 Named Pipe 初始化完成。
/// 這支轉接層只負責啟動 PasswordVault.exe，不負責啟動 FileLocker.exe——FileLocker.App 那邊
/// 有它自己的一份轉接層執行檔，各自只管自己這一側。</summary>
static async Task<byte[]> ForwardToAppAsync(byte[] message)
{
    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    var connected = await TryConnectAsync(pipe, TimeSpan.FromMilliseconds(500));
    if (!connected)
    {
        TryLaunchInBackground();

        for (var attempt = 0; attempt < 20 && !connected; attempt++)
        {
            await Task.Delay(500);
            connected = await TryConnectAsync(pipe, TimeSpan.FromMilliseconds(500));
        }
    }

    if (!connected)
    {
        return ErrorPayload("無法連線到 PasswordVault 或 FileLocker，請確認至少一邊已安裝並可以正常啟動");
    }

    await WriteFramedAsync(pipe, message);

    var response = await ReadFramedAsync(pipe);
    return response ?? ErrorPayload("沒有收到回應");
}

static async Task<bool> TryConnectAsync(NamedPipeClientStream pipe, TimeSpan timeout)
{
    try
    {
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
        return true;
    }
    catch (Exception ex) when (ex is TimeoutException or IOException)
    {
        return false;
    }
}

/// <summary>這支程式假設跟 PasswordVault.exe 安裝在同一個資料夾（PasswordVault.exe 編譯期
/// 內建 PasswordVault.Core，不像 FileLocker 的部件那樣要解壓到巢狀的 plugins/PasswordLocker/
/// 子資料夾，見 PasswordVault_獨立化_規劃.md 第 3、11 節）——這個假設要等 PasswordVault.exe
/// 本體、安裝程式打包方式實際定案後再驗證是否成立，屆時如果實際安裝佈局不是這樣，這裡的相對
/// 路徑要跟著調整。</summary>
static void TryLaunchInBackground()
{
    try
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "PasswordVault.exe");
        if (!File.Exists(exePath))
        {
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = exePath, Arguments = "--startup", UseShellExecute = true });
    }
    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
    {
    }
}
