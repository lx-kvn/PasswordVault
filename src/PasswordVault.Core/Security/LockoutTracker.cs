using System.Text.Json;
using PasswordVault.Core.Io;

namespace PasswordVault.Core.Security;

public record LockoutState(int FailedAttempts, DateTimeOffset? LockedUntilUtc);

public record LockoutStatus(bool IsLockedOut, TimeSpan? RemainingLockout);

/// <summary>
/// 密碼錯誤鎖定機制：只套用在密碼這條路徑——恢復金鑰是 256-bit 高熵值（暴力破解在數學上不可行），
/// 不需要額外鎖定，人類自選的密碼熵值遠低於此，才是真正需要防護暴力猜測的地方。
///
/// 狀態存在本機一個獨立檔案，是「這台裝置現在鎖住了沒」的安全狀態，重開程式不會清空重來，
/// 換一台裝置也不會繼承。達到門檻次數後鎖定，鎖定時間隨累積失敗次數遞增（30 秒、60 秒、120 秒...，
/// 上限 1 小時），拖慢持續嘗試的攻擊者；成功解鎖一次會清掉這個項目的失敗紀錄，重新歸零。
///
/// 從 FileLocker repo 的 src/FileLocker.Core/Security/LockoutTracker.cs 複製過來的獨立副本，
/// 理由同 KeyDerivation.cs 開頭的說明。
/// </summary>
public class LockoutTracker
{
    private const int ThresholdAttempts = 5;
    private const int BaseLockoutSeconds = 30;
    private const int MaxLockoutSeconds = 3600;

    private static readonly object WriteLock = new();
    private readonly string _filePath;

    public LockoutTracker(string filePath)
    {
        _filePath = filePath;
    }

    public LockoutStatus CheckStatus(string uuid)
    {
        lock (WriteLock)
        {
            var all = LoadAll();
            if (all.TryGetValue(uuid, out var state) && state.LockedUntilUtc is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
            {
                return new LockoutStatus(true, lockedUntil - DateTimeOffset.UtcNow);
            }
            return new LockoutStatus(false, null);
        }
    }

    public void RecordFailedAttempt(string uuid)
    {
        lock (WriteLock)
        {
            var all = LoadAll();
            var current = all.TryGetValue(uuid, out var existing) ? existing : new LockoutState(0, null);
            var newAttempts = current.FailedAttempts + 1;

            DateTimeOffset? lockedUntil = null;
            if (newAttempts >= ThresholdAttempts)
            {
                var exponent = Math.Min(newAttempts - ThresholdAttempts, 10);
                var lockoutSeconds = Math.Min(BaseLockoutSeconds * (1 << exponent), MaxLockoutSeconds);
                lockedUntil = DateTimeOffset.UtcNow.AddSeconds(lockoutSeconds);
            }

            all[uuid] = new LockoutState(newAttempts, lockedUntil);
            SaveAll(all);
        }
    }

    /// <summary>成功解鎖一次後，清掉這個項目的失敗紀錄——不是「原諒」之前的失敗次數，
    /// 是因為既然真的驗證通過了，代表操作這個項目的人合法擁有密碼，沒有理由繼續限制他。</summary>
    public void RecordSuccess(string uuid)
    {
        lock (WriteLock)
        {
            var all = LoadAll();
            if (all.Remove(uuid))
            {
                SaveAll(all);
            }
        }
    }

    private Dictionary<string, LockoutState> LoadAll()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, LockoutState>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, LockoutState>>(json) ?? new Dictionary<string, LockoutState>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, LockoutState>();
        }
    }

    private void SaveAll(Dictionary<string, LockoutState> all)
    {
        var json = JsonSerializer.Serialize(all);
        AtomicFile.WriteAllText(_filePath, json);
    }
}
