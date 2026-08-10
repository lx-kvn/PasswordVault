using System.Security.Cryptography;

namespace PasswordVault.Core.Crypto;

/// <summary>
/// Nonce/IV(12 bytes) + Ciphertext + Auth Tag(16 bytes)。直接用 .NET 內建的
/// System.Security.Cryptography.AesGcm，不需要額外套件。RecoveryKeyProtector 的
/// WrapContentKey/UnwrapContentKey 靠這個類別實作，是它唯一的內部依賴。
///
/// 從 FileLocker repo 的 src/FileLocker.Core/Crypto/AesGcmCipher.cs 複製過來的獨立副本，
/// 理由同 KeyDerivation.cs 開頭的說明。
/// </summary>
public static class AesGcmCipher
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return (nonce, ciphertext, tag);
    }

    /// <summary>
    /// 解密並驗證 Auth Tag；Tag 驗證失敗（代表密碼錯誤或密文被竄改）AesGcm 會丟出
    /// CryptographicException，呼叫端要接住並轉換成「密碼錯誤或檔案已損毀」的訊息，不要洩漏細節。
    /// </summary>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);

        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
