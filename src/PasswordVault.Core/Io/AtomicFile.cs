namespace PasswordVault.Core.Io;

/// <summary>
/// 提供「先寫暫存檔、成功後才原子改名」的寫入方式，取代直接 File.WriteAllText 覆蓋目的檔案。
/// File.Move 在同一個磁碟區內是原子操作（要嘛整個成功、要嘛完全沒發生），不會有「寫到一半」的中間狀態，
/// 避免程式中斷（斷電、當機）或雲端同步用戶端同時讀取時，讀到內容不完整、損毀的檔案。
///
/// 從 FileLocker repo 的 src/FileLocker.Core/Io/AtomicFile.cs 複製過來的獨立副本，
/// 理由同 KeyDerivation.cs 開頭的說明。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 暫存檔刻意放在同一個資料夾（跟目的檔案同一個磁碟區），File.Move 才能是真正的原子操作——
        // 如果暫存檔跨磁碟區，Move 實際上會變成「複製再刪除」，就失去原子性的保證了。
        var tempPath = Path.Combine(
            directory ?? "",
            $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // 寫暫存檔或改名失敗時，盡量清掉殘留的暫存檔，避免留下垃圾檔案。
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { /* 盡力而為，清不掉就算了 */ }
            }
            throw;
        }
    }
}
