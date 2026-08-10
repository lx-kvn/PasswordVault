using System.Text.Json;
using FileLocker.Core.Io;

namespace FileLocker.PasswordLocker;

/// <summary>
/// 對應規劃文件：憑證資料獨立於 Vault 之外的本機儲存層。純粹是檔案系統存取，跟 FolderGuardStore
/// 對資料夾防護的定位一致——不做加解密（PasswordLockerService 的事）也不做業務規則判斷，
/// 方便獨立做單元測試。
/// </summary>
public class PasswordLockerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Load-modify-save 這個模式本身沒有鎖就不是執行緒安全的——現在有兩條各自獨立的 IPC 通道
    // 會同時打進來（WebView2 主視窗 + 瀏覽器擴充功能透過 Named Pipe），兩邊都可能在幾乎同一
    // 時間 Load() 一份舊資料、各自修改、Save() 回去，後寫的會整份覆蓋掉先寫的，等於靜默丟失
    // 對方剛寫入的那筆變更（2026-08-09 這輪稽核發現的缺口）。跟 LockoutTracker 的 WriteLock
    // 同一個道理、同一種鎖法——這裡不需要更複雜的機制（例如檔案層級鎖、樂觀併發版本號），
    // 密碼庫的讀寫頻率低、單一行程內的鎖就足夠涵蓋目前唯一的併發來源。
    private static readonly object WriteLock = new();

    private readonly string _filePath;

    public PasswordLockerStore(string filePath)
    {
        _filePath = filePath;
    }

    public PasswordLockerData Load()
    {
        lock (WriteLock)
        {
            return LoadUnlocked();
        }
    }

    private PasswordLockerData LoadUnlocked()
    {
        if (!File.Exists(_filePath))
        {
            return new PasswordLockerData();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<PasswordLockerData>(json) ?? new PasswordLockerData();
        }
        catch (JsonException)
        {
            return new PasswordLockerData();
        }
    }

    public void Save(PasswordLockerData data)
    {
        lock (WriteLock)
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            AtomicFile.WriteAllText(_filePath, json);
        }
    }

    /// <summary>Load → 修改 → Save 這個序列本身要是原子的，光是 Load()／Save() 各自加鎖只能防止
    /// 兩個檔案 I/O 互相踩到，防不了「兩邊各自 Load 到同一份舊資料、各自修改、後寫的蓋掉先寫的」
    /// 這種邏輯層級的競態——PasswordLockerService 目前絕大多數方法都是「先 Load 一次、算完新值
    /// 才 Save」，這個方法讓那整段（含業務邏輯）都在同一把鎖底下執行，呼叫端只需要把「怎麼從
    /// 舊資料算出新資料」包成一個委派傳進來，不用自己管鎖。</summary>
    public void Mutate(Action<PasswordLockerData> mutate)
    {
        lock (WriteLock)
        {
            var data = LoadUnlocked();
            mutate(data);
            var json = JsonSerializer.Serialize(data, JsonOptions);
            AtomicFile.WriteAllText(_filePath, json);
        }
    }
}
