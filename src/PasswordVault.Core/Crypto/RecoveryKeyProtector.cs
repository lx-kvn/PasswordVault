using System.Security.Cryptography;
using System.Text;

namespace PasswordVault.Core.Crypto;

/// <summary>
/// 密碼之外的第二條解鎖路徑：啟用當下產生一組高熵值隨機碼，使用者自己抄下來保管，
/// PasswordVault 不會留下任何副本。恢復金鑰本身就是另一把能完全解密內容的鑰匙，
/// 安全性等同密碼——外洩的風險要用管理密碼的謹慎程度去看待，這點要在 GUI 顯示時明確告知使用者。
///
/// 從 FileLocker repo 的 src/FileLocker.Core/Crypto/RecoveryKeyProtector.cs 複製過來的獨立副本，
/// 理由同 KeyDerivation.cs 開頭的說明。HkdfInfo 字串刻意維持舊值不變（見下方常數），跟
/// KeyDerivation.cs 同樣的資料相容性理由。
/// </summary>
public static class RecoveryKeyProtector
{
    private const int KeySizeBytes = 32;
    private const int DisplayGroupSize = 5;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // 維持 "FileLocker/recovery-wrap/v1" 舊字串——這是既有使用者恢復金鑰包裝內容金鑰的
    // 衍生輸入之一，換掉會讓所有舊資料在遷移後無法用恢復金鑰解密。
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("FileLocker/recovery-wrap/v1");

    public static byte[] GenerateRecoveryKeyBytes() => RandomNumberGenerator.GetBytes(KeySizeBytes);

    /// <summary>把原始位元組轉成人類看得懂、方便抄寫的格式，例如 ABCDE-FGHIJ-KLMNO-...（每 5 字元一組）。</summary>
    public static string FormatForDisplay(byte[] keyBytes)
    {
        var raw = Base32Encode(keyBytes);
        var groups = new List<string>();
        for (var i = 0; i < raw.Length; i += DisplayGroupSize)
        {
            groups.Add(raw.Substring(i, Math.Min(DisplayGroupSize, raw.Length - i)));
        }
        return string.Join("-", groups);
    }

    /// <summary>
    /// 解析使用者輸入的恢復金鑰：不分大小寫、允許有沒有分隔線／多餘空白，容忍使用者手動輸入時的格式差異。
    /// 格式不對、長度不對都回傳 null，由呼叫端統一當作「恢復金鑰不正確」處理，不細分原因避免洩漏線索。
    /// </summary>
    public static byte[]? ParseUserInput(string input)
    {
        var cleaned = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var decoded = Base32Decode(cleaned);
        if (decoded is null || decoded.Length != KeySizeBytes)
        {
            return null;
        }
        return decoded;
    }

    public static byte[] DeriveWrappingKey(byte[] recoveryKeyBytes)
    {
        var wrappingKey = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, recoveryKeyBytes, wrappingKey, HkdfInfo);
        return wrappingKey;
    }

    /// <summary>用包裝金鑰把內容金鑰包起來，回傳可以直接存進紀錄的 Base64 字串。</summary>
    public static string WrapContentKey(byte[] wrappingKey, byte[] contentKey)
    {
        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(wrappingKey, contentKey);

        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>解開存起來的包裝內容金鑰。恢復金鑰錯誤會丟出 CryptographicException。</summary>
    public static byte[] UnwrapContentKey(byte[] wrappingKey, string wrappedBase64)
    {
        var combined = Convert.FromBase64String(wrappedBase64);
        var nonce = combined.AsSpan(0, AesGcmCipher.NonceSizeBytes);
        var tag = combined.AsSpan(AesGcmCipher.NonceSizeBytes, AesGcmCipher.TagSizeBytes);
        var ciphertext = combined.AsSpan(AesGcmCipher.NonceSizeBytes + AesGcmCipher.TagSizeBytes);

        return AesGcmCipher.Decrypt(wrappingKey, nonce, ciphertext, tag);
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        var bitBuffer = 0;
        var bitCount = 0;

        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                sb.Append(Base32Alphabet[(bitBuffer >> bitCount) & 0x1F]);
            }
        }

        if (bitCount > 0)
        {
            sb.Append(Base32Alphabet[(bitBuffer << (5 - bitCount)) & 0x1F]);
        }

        return sb.ToString();
    }

    private static byte[]? Base32Decode(string encoded)
    {
        var bytes = new List<byte>();
        var bitBuffer = 0;
        var bitCount = 0;

        foreach (var c in encoded)
        {
            var index = Base32Alphabet.IndexOf(c);
            if (index < 0)
            {
                return null; // 非法字元（不在 Base32 字母表裡）
            }

            bitBuffer = (bitBuffer << 5) | index;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes.Add((byte)((bitBuffer >> bitCount) & 0xFF));
            }
        }

        return bytes.ToArray();
    }
}
