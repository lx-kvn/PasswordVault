using System.Text;
using FileLocker.PasswordLocker;

namespace FileLocker.PasswordLocker.Tests;

/// <summary>
/// 對照 RFC 6238 Appendix B 官方公布的測試向量——不是自己編幾組輸入輸出，而是拿公開文件裡
/// 已知答案來驗證演算法實作正確。三組密鑰分別對應 SHA1/SHA256/SHA512（RFC 6238 測試向量的
/// 密鑰刻意跟雜湊輸出長度一致：20/32/64 bytes），T0=0、X=30 秒、輸出固定 8 碼。
/// </summary>
public class TotpGeneratorTests
{
    private const string Sha1Secret = "12345678901234567890";
    private const string Sha256Secret = "12345678901234567890123456789012";
    private const string Sha512Secret = "1234567890123456789012345678901234567890123456789012345678901234";

    private static DateTime FromUnixSeconds(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void GenerateCode_Sha1_MatchesRfc6238TestVectors(long unixSeconds, string expected)
    {
        var code = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha1Secret), "SHA1", 8, 30, FromUnixSeconds(unixSeconds));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(59L, "46119246")]
    [InlineData(1111111109L, "68084774")]
    [InlineData(1111111111L, "67062674")]
    [InlineData(1234567890L, "91819424")]
    [InlineData(2000000000L, "90698825")]
    public void GenerateCode_Sha256_MatchesRfc6238TestVectors(long unixSeconds, string expected)
    {
        var code = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha256Secret), "SHA256", 8, 30, FromUnixSeconds(unixSeconds));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(59L, "90693936")]
    [InlineData(1111111109L, "25091201")]
    [InlineData(1111111111L, "99943326")]
    [InlineData(1234567890L, "93441116")]
    [InlineData(2000000000L, "38618901")]
    public void GenerateCode_Sha512_MatchesRfc6238TestVectors(long unixSeconds, string expected)
    {
        var code = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha512Secret), "SHA512", 8, 30, FromUnixSeconds(unixSeconds));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void GenerateCode_DefaultSixDigits_ProducesSixDigitString()
    {
        var code = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha1Secret), "SHA1", 6, 30, FromUnixSeconds(59));
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void GenerateCode_SamePeriodWindow_ProducesSameCode()
    {
        var a = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha1Secret), "SHA1", 6, 30, FromUnixSeconds(30));
        var b = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha1Secret), "SHA1", 6, 30, FromUnixSeconds(59));
        Assert.Equal(a, b);
    }

    [Fact]
    public void GenerateCode_NextPeriodWindow_ProducesDifferentCode()
    {
        var a = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha1Secret), "SHA1", 6, 30, FromUnixSeconds(29));
        var b = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes(Sha1Secret), "SHA1", 6, 30, FromUnixSeconds(30));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("MFRGG", new byte[] { 0x61, 0x62, 0x63 })] // "abc" 的標準 Base32 編碼
    [InlineData("mfrgg", new byte[] { 0x61, 0x62, 0x63 })] // 小寫也要能解
    [InlineData("MFRGG===", new byte[] { 0x61, 0x62, 0x63 })] // 補零字元要能忽略
    public void DecodeBase32_KnownValues_RoundTripsCorrectly(string base32, byte[] expected)
    {
        var decoded = TotpGenerator.DecodeBase32(base32);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void DecodeBase32_InvalidCharacter_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => TotpGenerator.DecodeBase32("!!!invalid!!!"));
    }

    [Fact]
    public void GenerateCodeFromBase32Secret_MatchesRawBytesEquivalent()
    {
        // "abc" 的 Base32 是 "MFRGG"（見上面 DecodeBase32 測試）——用這個密鑰算出來的碼
        // 要跟直接傳原始位元組算出來的碼一致，確保 Base32 這一層轉換沒有把資料轉壞。
        var viaBase32 = TotpGenerator.GenerateCodeFromBase32Secret("MFRGG", "SHA1", 6, 30, FromUnixSeconds(59));
        var viaRawBytes = TotpGenerator.GenerateCode(Encoding.ASCII.GetBytes("abc"), "SHA1", 6, 30, FromUnixSeconds(59));
        Assert.Equal(viaRawBytes, viaBase32);
    }
}
