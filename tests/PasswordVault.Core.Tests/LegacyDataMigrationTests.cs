namespace PasswordVault.Core.Tests;

public class LegacyDataMigrationTests : IDisposable
{
    private readonly DirectoryInfo _oldDir;
    private readonly DirectoryInfo _newDir;

    public LegacyDataMigrationTests()
    {
        _oldDir = Directory.CreateTempSubdirectory("PasswordVaultMigrationOld_");
        // 新路徑刻意不預先建立資料夾——App.xaml.cs 呼叫這個方法之前也還沒建立過
        // PasswordLocker 子資料夾，MigrateIfNeeded 本身要能處理「新路徑資料夾根本不存在」的情況。
        _newDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"PasswordVaultMigrationNew_{Guid.NewGuid():N}"));
    }

    public void Dispose()
    {
        if (_oldDir.Exists)
        {
            _oldDir.Delete(recursive: true);
        }
        if (_newDir.Exists)
        {
            _newDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void OldDirectoryDoesNotExist_DoesNothing()
    {
        var neverCreatedOldDir = Path.Combine(Path.GetTempPath(), $"PasswordVaultMigrationNeverExists_{Guid.NewGuid():N}");

        var migrated = LegacyDataMigration.MigrateIfNeeded(neverCreatedOldDir, _newDir.FullName);

        Assert.False(migrated);
        Assert.False(_newDir.Exists);
    }

    [Fact]
    public void OldDirectoryHasNoCredentialsFile_DoesNothing()
    {
        // 資料夾存在，但裡面沒有 credentials.json（例如只設定過密碼庫但從沒真的存過任何憑證）——
        // 沒有真正的資料好搬，不需要建立新資料夾。
        File.WriteAllText(Path.Combine(_oldDir.FullName, "lockout.json"), "{}");

        var migrated = LegacyDataMigration.MigrateIfNeeded(_oldDir.FullName, _newDir.FullName);

        Assert.False(migrated);
        Assert.False(_newDir.Exists);
    }

    [Fact]
    public void OldHasDataNewIsEmpty_CopiesAllFilesAndKeepsOldFilesIntact()
    {
        File.WriteAllText(Path.Combine(_oldDir.FullName, "credentials.json"), "{\"credentials\":[]}");
        File.WriteAllText(Path.Combine(_oldDir.FullName, "lockout.json"), "{}");

        var migrated = LegacyDataMigration.MigrateIfNeeded(_oldDir.FullName, _newDir.FullName);

        Assert.True(migrated);
        Assert.True(File.Exists(Path.Combine(_newDir.FullName, "credentials.json")));
        Assert.True(File.Exists(Path.Combine(_newDir.FullName, "lockout.json")));
        Assert.Equal("{\"credentials\":[]}", File.ReadAllText(Path.Combine(_newDir.FullName, "credentials.json")));

        // 使用者明確要求：搬移是複製，不是移動——舊檔案原封不動留著，見規劃文件第 7 節這輪定案。
        Assert.True(File.Exists(Path.Combine(_oldDir.FullName, "credentials.json")));
        Assert.True(File.Exists(Path.Combine(_oldDir.FullName, "lockout.json")));
    }

    [Fact]
    public void BothOldAndNewHaveData_NewPathWinsSilently_DoesNotOverwriteOrTouchOldFiles()
    {
        File.WriteAllText(Path.Combine(_oldDir.FullName, "credentials.json"), "{\"credentials\":[\"old\"]}");

        Directory.CreateDirectory(_newDir.FullName);
        File.WriteAllText(Path.Combine(_newDir.FullName, "credentials.json"), "{\"credentials\":[\"new\"]}");

        var migrated = LegacyDataMigration.MigrateIfNeeded(_oldDir.FullName, _newDir.FullName);

        Assert.False(migrated);
        // 新路徑既有內容完全不受影響——這是使用者明確要求的行為：新路徑優先，不覆蓋、不合併。
        Assert.Equal("{\"credentials\":[\"new\"]}", File.ReadAllText(Path.Combine(_newDir.FullName, "credentials.json")));
    }

    [Fact]
    public void NewDirectoryExistsButHasNoCredentialsFile_StillMigrates()
    {
        // 新資料夾可能已經因為其他原因被建立過（例如程式啟動時的 Directory.CreateDirectory
        // 呼叫順序），但還沒有真正的憑證資料——這種情況要當成「新路徑沒有資料」照樣搬移，
        // 不是只看資料夾存不存在。
        Directory.CreateDirectory(_newDir.FullName);
        File.WriteAllText(Path.Combine(_oldDir.FullName, "credentials.json"), "{\"credentials\":[]}");

        var migrated = LegacyDataMigration.MigrateIfNeeded(_oldDir.FullName, _newDir.FullName);

        Assert.True(migrated);
        Assert.True(File.Exists(Path.Combine(_newDir.FullName, "credentials.json")));
    }
}
