using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using PasswordVault.App;
using FileLocker.PluginContracts;

namespace PasswordVault.App.Tests;

/// <summary>
/// 移植自 FileLocker repo 的 tests/FileLocker.App.Tests/PasswordLockerNativePipeServerTests.cs——
/// 2026-08-09 那輪安全稽核發現 Pipe Server 原本完全信任連線端跟訊息內容，這裡不是一般的行為
/// 測試，而是把稽核發現的每一條攻擊流程直接寫成斷言，固定住修復後的行為，避免之後的改動又
/// 悄悄放寬回沒有防護的版本。<see cref="PasswordVaultNativePipeServer"/> 的安全性邏輯是從
/// FileLocker 那份原樣複製過來的獨立副本（見該類別的 XML doc 註解），修復本身已經遷移過來，
/// 只是這份對應的回歸測試原本沒有跟著搬——這次補上，不主動找其他新的測試範圍。
///
/// 每個測試案例用自己專屬、帶隨機後綴的管道名稱（見 <see cref="UniquePipeName"/>），一來避免
/// 跟同一台機器上真的在跑的 PasswordVault.exe／FileLocker.exe 搶同一個具名管道，二來避免測試
/// 案例之間互相干擾（xunit 預設會平行跑同一個類別以外的測試）。<c>expectedClientExePath</c>
/// 一律傳目前測試行程自己的模組路徑——測試裡的「連線端」就是這個測試行程本身，不是真正的
/// Native Host exe，這樣 VerifyClientIsExpectedHost 才會通過，除非某個測試刻意要驗證
/// 「連線端不符」的情況。
/// </summary>
public sealed class PasswordVaultNativePipeServerTests
{
    private static string OwnExePath => Process.GetCurrentProcess().MainModule!.FileName!;

    private static string UniquePipeName([System.Runtime.CompilerServices.CallerMemberName] string testName = "")
        => $"FileLocker-PasswordLocker-Pipe-Test-{testName}-{Guid.NewGuid():N}";

    /// <summary>可控制回應內容／記錄呼叫紀錄的假部件——不用真正的 PasswordLockerPlugin，
    /// 這裡只需要驗證 PasswordVaultNativePipeServer 這一層「該不該轉發、轉發後怎麼處理回應」
    /// 的邏輯，不需要真的跑一次加解密。</summary>
    private sealed class FakePlugin : IPasswordLockerPlugin
    {
        public List<string> ReceivedMessageTypes { get; } = [];
        public Func<string, JsonElement, object?>? OnHandleRequest { get; set; }

        public void Initialize(PasswordLockerPluginContext context)
        {
        }

        public Task<object?> HandleRequestAsync(string messageType, JsonElement requestBody, IntPtr ownerWindowHandle)
        {
            ReceivedMessageTypes.Add(messageType);
            if (OnHandleRequest is not null)
            {
                return Task.FromResult(OnHandleRequest(messageType, requestBody));
            }
            return Task.FromResult<object?>(new { type = $"{messageType}Result", success = true });
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return client;
    }

    private static async Task WriteMessageAsync(Stream stream, object message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        await stream.WriteAsync(BitConverter.GetBytes(json.Length));
        await stream.WriteAsync(json);
        await stream.FlushAsync();
    }

    private static async Task<JsonElement?> TryReadMessageAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var lengthBuffer = new byte[4];
            if (!await ReadExactAsync(stream, lengthBuffer, cts.Token)) return null;
            var length = BitConverter.ToInt32(lengthBuffer, 0);
            var buffer = new byte[length];
            if (!await ReadExactAsync(stream, buffer, cts.Token)) return null;
            using var doc = JsonDocument.Parse(buffer);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    // ---- 攻擊流程 1：訊息白名單——擴充功能不需要的密碼庫操作不該能從這條管線觸發 ----

    [Fact]
    public async Task DisallowedMessageType_IsRejectedWithoutReachingPlugin()
    {
        var plugin = new FakePlugin();
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin, (_, _) => Task.FromResult(true), () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);
            // exportPasswordLockerCsv 一次會吐出整個密碼庫的明文——這正是這條攻擊流程最嚴重
            // 的一步（見稽核報告 1-2），不在白名單內，就算格式完全合法也不該被轉發。
            await WriteMessageAsync(client, new { type = "exportPasswordLockerCsv" });
            var response = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));

            Assert.NotNull(response);
            Assert.Equal("error", response.Value.GetProperty("type").GetString());
            Assert.Empty(plugin.ReceivedMessageTypes);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RecordSiteVerified_IsRejected_CannotBeUsedToForgeSiteSession()
    {
        // recordPasswordLockerSiteVerified 原本任何人都能送、任意把一個網域標成「已驗證」
        // （見稽核報告 2-2）——這則訊息只應該由 App 本體驗證成功後直接呼叫部件觸發，不該
        // 開放給這條瀏覽器擴充功能專用的管線。
        var plugin = new FakePlugin();
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin, (_, _) => Task.FromResult(true), () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);
            await WriteMessageAsync(client, new { type = "recordPasswordLockerSiteVerified", domain = "evil.com" });
            var response = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));

            Assert.NotNull(response);
            Assert.Equal("error", response.Value.GetProperty("type").GetString());
            Assert.Empty(plugin.ReceivedMessageTypes);
        }
        finally
        {
            server.Stop();
        }
    }

    // ---- 攻擊流程 2：自動重試驗證只能被真正需要它的訊息類型觸發 ----

    [Fact]
    public async Task NotVerifiedResponse_ForNonRetryEligibleType_DoesNotTriggerVerificationWindow()
    {
        // listPasswordLocker 本身不需要驗證（回傳的是不含密碼的 metadata），但只要請求裡
        // 帶一個 domain 欄位，修復前的判斷條件（只看「有沒有 domain 欄位」）就會誤觸發
        // 「叫出驗證視窗、通過後重打一次」——等於借用使用者的一次驗證動作幫任何訊息開後門
        // （見稽核報告 1-3）。這裡故意讓 FakePlugin 回一個帶 NOT_VERIFIED 錯誤碼的回應，
        // 驗證委派完全不該被呼叫到。
        var verificationCalled = false;
        var plugin = new FakePlugin
        {
            OnHandleRequest = (type, _) => new { type = $"{type}Result", success = false, errorCode = "PASSWORD_LOCKER_NOT_VERIFIED" }
        };
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin,
            (_, _) => { verificationCalled = true; return Task.FromResult(true); },
            () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);
            await WriteMessageAsync(client, new { type = "listPasswordLocker", domain = "example.com" });
            var response = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));

            Assert.NotNull(response);
            Assert.False(verificationCalled);
            // plugin 只被呼叫一次（沒有重試那一次）。
            Assert.Single(plugin.ReceivedMessageTypes);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task NotVerifiedResponse_ForRetryEligibleType_TriggersVerificationThenRetries()
    {
        // revealPasswordLockerCredentialForSite 是少數幾個真的需要「先驗證、通過再重試」的
        // 訊息之一（另外兩個是 addOrUpdatePasswordLockerCredential／
        // revealPasswordLockerTotpForSite，見下面 TOTP 專用的測試）——這裡驗證正常路徑
        // （合法使用情境）在白名單收緊之後仍然可以運作。
        var verificationCalled = false;
        string? verificationDomain = null;
        var callCount = 0;
        var plugin = new FakePlugin
        {
            OnHandleRequest = (type, _) =>
            {
                callCount++;
                return callCount == 1
                    ? new { type = $"{type}Result", success = false, errorCode = "PASSWORD_LOCKER_NOT_VERIFIED" }
                    : new { type = $"{type}Result", success = true, password = "hunter2" };
            }
        };
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin,
            (domain, _) => { verificationCalled = true; verificationDomain = domain; return Task.FromResult(true); },
            () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);
            await WriteMessageAsync(client, new { type = "revealPasswordLockerCredentialForSite", id = "abc", domain = "github.com" });
            var response = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));

            Assert.NotNull(response);
            Assert.True(verificationCalled);
            Assert.Equal("github.com", verificationDomain);
            Assert.Equal(2, callCount);
            Assert.True(response.Value.GetProperty("success").GetBoolean());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RevealTotpForSite_IsWhitelistedAndTriggersVerificationThenRetries()
    {
        // TOTP 的揭露比密碼更嚴格（見 PasswordLockerService 的新鮮度視窗），但那道限制是在
        // ProtocolHandlers／Service 那一層擋，不是這條管線的事——這裡只需要確認
        // revealPasswordLockerTotpForSite 有進白名單、而且沿用跟 revealPasswordLockerCredentialForSite
        // 同一套「先驗證再重試」機制，不會被誤擋。
        var verificationCalled = false;
        var callCount = 0;
        var plugin = new FakePlugin
        {
            OnHandleRequest = (type, _) =>
            {
                callCount++;
                return callCount == 1
                    ? new { type = $"{type}Result", success = false, errorCode = "PASSWORD_LOCKER_NOT_VERIFIED" }
                    : new { type = $"{type}Result", success = true, secret = "JBSWY3DPEHPK3PXP" };
            }
        };
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin,
            (_, _) => { verificationCalled = true; return Task.FromResult(true); },
            () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);
            await WriteMessageAsync(client, new { type = "revealPasswordLockerTotpForSite", id = "abc", domain = "github.com" });
            var response = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));

            Assert.NotNull(response);
            Assert.True(verificationCalled);
            Assert.Equal(2, callCount);
            Assert.True(response.Value.GetProperty("success").GetBoolean());
        }
        finally
        {
            server.Stop();
        }
    }

    // ---- 攻擊流程 3：連線端身份驗證——不是我們自己的 Native Host exe 就拒絕連線 ----

    [Fact]
    public async Task ClientProcessMismatch_ConnectionIsSilentlyDropped()
    {
        var plugin = new FakePlugin();
        var pipeName = UniquePipeName();
        // 故意給一個絕對不會是目前這個測試行程的路徑——模擬「連線端不是我們自己的 Native
        // Host exe」的情況（見稽核報告 1-1：DACL 只能限制「哪個 Windows 使用者」，擋不住
        // 「同一個使用者身分下，剛好也在跑的其他本機程式」）。
        var server = new PasswordVaultNativePipeServer(
            () => plugin, (_, _) => Task.FromResult(true), () => Task.CompletedTask,
            expectedClientExePath: @"C:\definitely-not-the-real-native-host.exe", pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);

            // 伺服器那端一驗證失敗就立刻 Dispose 這條連線、從不進入訊息處理迴圈——實測這個
            // Dispose 通常快到連 client 端的第一次 WriteAsync 都會直接撞見「Pipe is broken」，
            // 不一定會撐到能送出完整訊息再等回應。不管是寫入就失敗、還是寫入成功但永遠等不到
            // 回應，都同樣代表「這條連線沒有被當成合法的 Native Host 對待」，兩種情況都算通過。
            JsonElement? response = null;
            try
            {
                await WriteMessageAsync(client, new { type = "listPasswordLocker" });
                response = await TryReadMessageAsync(client, TimeSpan.FromSeconds(3));
            }
            catch (IOException)
            {
            }

            Assert.Null(response);
            Assert.Empty(plugin.ReceivedMessageTypes);
        }
        finally
        {
            server.Stop();
        }
    }

    // ---- 攻擊流程 4：CPU 忙迴圈——連線期間第二個連線端要能正常排隊，不能撞見「所有管道
    // 例項都在使用中」而觸發零延遲重試迴圈（見稽核報告 3-1，探針程式已經實測證實過舊版行為
    // 會佔滿一顆 CPU 核心） ----

    [Fact]
    public async Task ConcurrentConnections_BothSucceedWithoutInstanceExhaustion()
    {
        var plugin = new FakePlugin();
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin, (_, _) => Task.FromResult(true), () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var firstClient = await ConnectAsync(pipeName);
            // 第一條連線還開著的情況下，第二條連線也要能成功建立——maxNumberOfServerInstances
            // 如果還是寫死 1，這裡在稽核修復前會直接連線逾時或失敗。
            using var secondClient = await ConnectAsync(pipeName);

            await WriteMessageAsync(firstClient, new { type = "listPasswordLocker" });
            await WriteMessageAsync(secondClient, new { type = "listPasswordLocker" });
            var firstResponse = await TryReadMessageAsync(firstClient, TimeSpan.FromSeconds(5));
            var secondResponse = await TryReadMessageAsync(secondClient, TimeSpan.FromSeconds(5));

            Assert.NotNull(firstResponse);
            Assert.NotNull(secondResponse);
        }
        finally
        {
            server.Stop();
        }
    }

    // ---- 攻擊流程 5：格式不符的請求不該打斷整條連線 ----

    [Fact]
    public async Task MalformedRequest_ReturnsErrorWithoutKillingConnection()
    {
        var plugin = new FakePlugin
        {
            OnHandleRequest = (_, _) => throw new ArgumentException("模擬部件解析請求欄位失敗（例如 Enum.Parse 撞到不存在的值)")
        };
        var pipeName = UniquePipeName();
        var server = new PasswordVaultNativePipeServer(
            () => plugin, (_, _) => Task.FromResult(true), () => Task.CompletedTask, OwnExePath, pipeName);
        server.Start();
        try
        {
            using var client = await ConnectAsync(pipeName);
            await WriteMessageAsync(client, new { type = "addOrUpdatePasswordLockerCredential" });
            var errorResponse = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));

            Assert.NotNull(errorResponse);
            Assert.Equal("error", errorResponse.Value.GetProperty("type").GetString());

            // 連線本身沒有被切斷——同一條連線可以繼續處理下一則請求。
            await WriteMessageAsync(client, new { type = "listPasswordLocker" });
            var nextResponse = await TryReadMessageAsync(client, TimeSpan.FromSeconds(5));
            Assert.NotNull(nextResponse);
        }
        finally
        {
            server.Stop();
        }
    }
}
