using System.Security.Cryptography;
using PasswordVault.Cli;
using PasswordVault.Core;
using PasswordVault.Core.Security;

// 密碼庫資料目錄跟 PasswordVault.App 用同一個預設路徑（%AppData%\PasswordVault\PasswordLocker），
// 用環境變數覆寫的方式比照 FileLocker.Cli 的 FILELOCKER_VAULT_PATH 慣例，方便指到非預設位置。
var dataDir = Environment.GetEnvironmentVariable("PASSWORDVAULT_DATA_PATH");
if (string.IsNullOrWhiteSpace(dataDir))
{
    dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasswordVault", "PasswordLocker");
}
Directory.CreateDirectory(dataDir);

var store = new PasswordLockerStore(Path.Combine(dataDir, "credentials.json"));
var lockoutTracker = new LockoutTracker(Path.Combine(dataDir, "lockout.json"));
var service = new PasswordLockerService(store, lockoutTracker);

if (args.Length < 1)
{
    PrintUsage();
    return;
}

switch (args[0])
{
    case "--list":
        await ListCommandAsync();
        break;
    case "--get":
        if (args.Length < 2)
        {
            Console.WriteLine("錯誤：--get 需要帶一個憑證 Id（用 --list 查）");
            PrintUsage();
            Environment.Exit(1);
            return;
        }
        await GetCommandAsync(args[1]);
        break;
    default:
        PrintUsage();
        break;
}

async Task ListCommandAsync()
{
    // 清單本身不含密碼，不需要驗證身份就能查（跟 App/瀏覽器擴充功能清單頁同一個既有原則，
    // 見 PasswordCredentialMetadata 上的說明）。
    var entries = await service.ListCredentialsMetadataAsync();
    if (entries.Count == 0)
    {
        Console.WriteLine("密碼庫目前是空的（或還沒設定過）。");
        return;
    }

    foreach (var entry in entries)
    {
        foreach (var line in CliHelpers.FormatCredentialLines(entry))
        {
            Console.WriteLine(line);
        }
        Console.WriteLine();
    }
}

async Task GetCommandAsync(string id)
{
    if (!service.IsConfigured)
    {
        Console.WriteLine("密碼庫還沒設定過——請先在 PasswordVault App 裡完成首次設定。");
        Environment.Exit(1);
        return;
    }

    // 驗證方式限定只能互動式輸入主密碼（見 PasswordVault_獨立化_規劃.md 第 12 節這輪定案）：
    // 不提供 --password-stdin／--password-file 這類具名參數或環境變數傳密碼的方式——那些
    // 會被寫進 shell 歷史記錄，或被同一台機器上其他行程讀到，等於讓「不經過驗證就能查到密碼」
    // 的攻擊面實質存在。這代表 CLI 沒辦法支援完全無人值守的自動化查詢（例如排程工作半夜自動
    // 跑），每次查詢都需要有人在旁邊互動輸入一次主密碼——這是刻意的安全性取捨，不是遺漏。
    // Passkey 同樣刻意不在 CLI 提供，理由跟 FileLocker.Cli 一致：WinRT KeyCredentialManager
    // 會跳出 Windows Hello 系統 UI，這是無 GUI 環境的存在意義相衝突的功能。
    Console.Write("請輸入主密碼：");
    var password = CliHelpers.ReadPasswordMasked();
    Console.WriteLine();

    var verifyResult = await service.VerifyAsync(password, IntPtr.Zero, tryPasskeyFirst: false);
    if (!verifyResult.Success || verifyResult.MasterKey is null)
    {
        Console.WriteLine($"驗證失敗：{verifyResult.ErrorMessage}");
        Environment.Exit(1);
        return;
    }

    try
    {
        var passwordResult = await service.GetDecryptedPasswordAsync(id, verifyResult.MasterKey);
        if (!passwordResult.Success)
        {
            Console.WriteLine($"查詢失敗：{passwordResult.ErrorMessage}");
            Environment.Exit(1);
            return;
        }

        Console.WriteLine(passwordResult.Password);
    }
    finally
    {
        // 主金鑰用完立刻清掉，不留在記憶體裡比必要的時間更久——跟 GUI／瀏覽器擴充功能那幾條
        // 驗證路徑同一個既有原則。
        CryptographicOperations.ZeroMemory(verifyResult.MasterKey);
    }
}

void PrintUsage()
{
    Console.WriteLine("用法：");
    Console.WriteLine("  PasswordVault.Cli --list           列出所有憑證（標題／分類／關聯網站，不含密碼）");
    Console.WriteLine("  PasswordVault.Cli --get <id>       查詢單筆憑證的密碼（互動輸入主密碼）");
    Console.WriteLine();
    Console.WriteLine("環境變數 PASSWORDVAULT_DATA_PATH 可以覆寫預設資料目錄（未設定時跟 App 共用同一個預設路徑）。");
    Console.WriteLine();
    Console.WriteLine("這支 CLI 只支援互動式輸入主密碼，不提供任何具名參數／環境變數傳密碼的方式，");
    Console.WriteLine("因此不支援無人值守的自動化查詢（例如排程工作半夜自動跑）——這是刻意的安全性取捨。");
}
