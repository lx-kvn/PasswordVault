using System.Security.Cryptography;

namespace PasswordVault.Core;

/// <summary>
/// TOTP（Time-based One-Time Password，RFC 6238，建構在 RFC 4226 HOTP 之上）動態驗證碼產生器
/// ＋ Base32（RFC 4648）密鑰編解碼——.NET 沒有內建這兩樣，純函式、跟 GeneratePassword／
/// EstimateStrength 一樣不依賴任何 I/O，方便直接拿 RFC 6238 官方公布的測試向量驗證正確性
/// （見規劃討論：不是自己編幾組輸入輸出，而是對照公開文件的已知答案）。
/// </summary>
public static class TotpGenerator
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>RFC 4648 Base32 解碼，忽略大小寫、空白與補零用的 '=' 字元——密鑰通常是
    /// 使用者從網站的 2FA 設定頁複製貼上，實務上常見夾雜空白或忘記轉大寫的情況，這裡直接
    /// 容忍，不要求呼叫端事先清理。</summary>
    public static byte[] DecodeBase32(string base32)
    {
        var cleaned = base32.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        if (cleaned.Length == 0)
        {
            return [];
        }

        var bits = new List<byte>(cleaned.Length * 5 / 8 + 1);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var c in cleaned)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"不是合法的 Base32 字元：'{c}'");
            }

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bits.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }

        return bits.ToArray();
    }

    /// <summary>密鑰用 ASCII 位元組直接算（TOTP 測試向量、或使用者手動輸入非 Base32 的原始
    /// 位元組時用這個）。真正的使用情境（密鑰是 Base32 字串）走
    /// <see cref="GenerateCodeFromBase32Secret"/>。</summary>
    public static string GenerateCode(byte[] secretBytes, string algorithm, int digits, int periodSeconds, DateTime? now = null)
    {
        var unixTimeSeconds = new DateTimeOffset(now ?? DateTime.UtcNow).ToUnixTimeSeconds();
        var counter = unixTimeSeconds / periodSeconds;

        // RFC 4226 要求 8-byte、big-endian 的計數器——BitConverter 在 little-endian 機器
        // （x86/x64 都是）預設輸出的位元組順序相反，這裡手動轉正。
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using HMAC hmac = algorithm.ToUpperInvariant() switch
        {
            "SHA1" => new HMACSHA1(secretBytes),
            "SHA256" => new HMACSHA256(secretBytes),
            "SHA512" => new HMACSHA512(secretBytes),
            _ => throw new ArgumentException($"不支援的 TOTP 演算法：{algorithm}", nameof(algorithm))
        };
        var hash = hmac.ComputeHash(counterBytes);

        // RFC 4226 動態截斷（dynamic truncation）：用雜湊最後一個位元組的低 4 位當偏移量，
        // 從該偏移量取 4 個位元組、把最高位元清零（避免被誤判成負數）組成一個 31-bit 整數。
        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var otp = binaryCode % (int)Math.Pow(10, digits);
        return otp.ToString().PadLeft(digits, '0');
    }

    public static string GenerateCodeFromBase32Secret(string base32Secret, string algorithm, int digits, int periodSeconds, DateTime? now = null)
        => GenerateCode(DecodeBase32(base32Secret), algorithm, digits, periodSeconds, now);
}
