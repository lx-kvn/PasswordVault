using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FileLocker.PluginContracts;
using Microsoft.Win32.SafeHandles;

namespace PasswordVault.App;

/// <summary>
/// 瀏覽器擴充功能的本機端點：PasswordVault.NativeHost.exe（或 FileLocker 那份轉接層——兩者
/// 轉發的目標是同一條 Pipe，見 PasswordVault_獨立化_規劃.md 第 8 節「共存」）透過這條 Named Pipe
/// 把訊息轉進來，呼叫編譯期直接內建的 <see cref="PasswordVault.Core.PasswordLockerPlugin"/>。
///
/// 從 FileLocker repo 的 src/FileLocker.App/PasswordLockerNativePipeServer.cs 複製過來的獨立
/// 副本，安全性三層防線（DACL 只允許目前使用者、反查連線端行程路徑、訊息類型白名單）原樣保留
/// ——這是 2026-08-09 那輪安全稽核加上的，不能因為是「骨架階段」就先跳過。
///
/// 跟 FileLocker 那份的差異：<see cref="_requestBrowserVerification"/> 目前固定回傳「未實作」——
/// 真正的驗證彈窗（PasswordLockerBrowserVerifyWindow 的對應版本）是前端／UI 決定共用方式之後
/// 才會動工的部分（見 PasswordVault_獨立化_規劃.md 第 3 節「前端拆分方式留待動工前另外規劃」），
/// 這輪骨架階段只確保「能收到請求、白名單／安全檢查生效、不需要驗證的訊息類型能正常回應」。
/// </summary>
public sealed class PasswordVaultNativePipeServer
{
    // 刻意跟 FileLocker.App 那份用同一個 Pipe 名稱——這條 Pipe 名稱是兩邊宿主程式共用的識別碼，
    // 不是「屬於 FileLocker」的名稱，換一個新名稱會讓瀏覽器擴充功能連不到「先搶到 Pipe 的一邊」。
    public const string PipeName = "FileLocker-PasswordLocker-Pipe";

    private readonly string _pipeName;
    private const int MaxMessageBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly HashSet<string> AllowedMessageTypes = new(StringComparer.Ordinal)
    {
        "openPasswordLockerApp",
        "listPasswordLocker",
        "findPasswordLockerCredentialsForDomain",
        "revealPasswordLockerCredentialForSite",
        "addOrUpdatePasswordLockerCredential",
        "generatePasswordLockerPassword",
        "revealPasswordLockerTotpForSite"
    };

    private static readonly HashSet<string> RetryAfterVerificationMessageTypes = new(StringComparer.Ordinal)
    {
        "revealPasswordLockerCredentialForSite",
        "addOrUpdatePasswordLockerCredential",
        "revealPasswordLockerTotpForSite"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<IPasswordLockerPlugin?> _getPlugin;
    private readonly Func<string, string?, Task<bool>> _requestBrowserVerification;
    private readonly Func<Task> _openPasswordLockerApp;
    private readonly string _expectedClientExePath;
    private CancellationTokenSource? _cts;

    public PasswordVaultNativePipeServer(
        Func<IPasswordLockerPlugin?> getPlugin, Func<string, string?, Task<bool>> requestBrowserVerification,
        Func<Task> openPasswordLockerApp, string expectedClientExePath, string? pipeName = null)
    {
        _getPlugin = getPlugin;
        _requestBrowserVerification = requestBrowserVerification;
        _openPasswordLockerApp = openPasswordLockerApp;
        _expectedClientExePath = expectedClientExePath;
        _pipeName = pipeName ?? PipeName;
    }

    /// <summary>從 UI 執行緒同步呼叫，故意用 Task.Run 把整個接受迴圈丟到執行緒集區——理由跟
    /// FileLocker.App 那份完全一樣：不這樣包一層，第一次 await 之後的延續會透過
    /// DispatcherSynchronizationContext 排回 UI 執行緒，實測會把整個視窗訊息迴圈卡死。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("無法取得目前使用者的 SID");
        security.AddAccessRule(new PipeAccessRule(
            currentUser, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var pipeSecurity = BuildPipeSecurity();

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
                await pipe.WaitForConnectionAsync(token);

                if (!VerifyClientIsExpectedHost(pipe))
                {
                    pipe.Dispose();
                    continue;
                }

                _ = HandleConnectionAsync(pipe, token);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
            }
            catch (IOException)
            {
                pipe?.Dispose();
                try
                {
                    await Task.Delay(RetryDelay, token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    private bool VerifyClientIsExpectedHost(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
            {
                return false;
            }

            using var process = Process.GetProcessById((int)clientProcessId);
            var clientPath = process.MainModule?.FileName;
            if (clientPath is null)
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(clientPath), Path.GetFullPath(_expectedClientExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                var request = await ReadMessageAsync(pipe, token);
                if (request is null)
                {
                    break;
                }

                var response = await HandleMessageAsync(request.Value);
                await WriteMessageAsync(pipe, response, token);
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private async Task<object> HandleMessageAsync(JsonElement request)
    {
        var type = request.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (type is null)
        {
            return new { type = "error", message = "缺少 type 欄位" };
        }

        if (!AllowedMessageTypes.Contains(type))
        {
            return new { type = "error", message = "這條管線不接受這個訊息類型" };
        }

        if (type == "openPasswordLockerApp")
        {
            await _openPasswordLockerApp();
            return new { type = "openPasswordLockerAppResult", success = true };
        }

        var plugin = _getPlugin();
        if (plugin is null)
        {
            return new { type = "error", message = "密碼庫尚未初始化" };
        }

        object? response;
        try
        {
            response = await plugin.HandleRequestAsync(type, request, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or JsonException)
        {
            return new { type = "error", message = "請求格式不正確" };
        }

        if (response is not null
            && TryGetStringProperty(response, "errorCode") == "PASSWORD_LOCKER_NOT_VERIFIED"
            && RetryAfterVerificationMessageTypes.Contains(type)
            && request.TryGetProperty("domain", out var domainProp))
        {
            var domain = domainProp.GetString() ?? "";
            var targetDomain = request.TryGetProperty("targetDomain", out var targetDomainProp)
                ? targetDomainProp.GetString()
                : null;
            var verified = await _requestBrowserVerification(domain, targetDomain);
            if (verified)
            {
                response = await plugin.HandleRequestAsync(type, request, IntPtr.Zero);
            }
        }

        return response ?? new { type = $"{type}Result", success = false, errorMessage = "密碼庫不認得這個請求" };
    }

    private static string? TryGetStringProperty(object response, string propertyName)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions));
        return doc.RootElement.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static async Task<JsonElement?> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        var lengthBuffer = new byte[4];
        if (!await ReadExactAsync(stream, lengthBuffer, token))
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > MaxMessageBytes)
        {
            return null;
        }

        var buffer = new byte[length];
        if (!await ReadExactAsync(stream, buffer, token))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(buffer);
        return doc.RootElement.Clone();
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static async Task WriteMessageAsync(Stream stream, object message, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await stream.WriteAsync(BitConverter.GetBytes(json.Length), token);
        await stream.WriteAsync(json, token);
        await stream.FlushAsync(token);
    }
}
