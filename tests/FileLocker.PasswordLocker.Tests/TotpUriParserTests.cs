using FileLocker.PasswordLocker;

namespace FileLocker.PasswordLocker.Tests;

public class TotpUriParserTests
{
    [Fact]
    public void TryParse_FullUri_ParsesAllFields()
    {
        var ok = TotpUriParser.TryParse(
            "otpauth://totp/GitHub:octocat?secret=JBSWY3DPEHPK3PXP&issuer=GitHub&algorithm=SHA256&digits=8&period=60",
            out var result);

        Assert.True(ok);
        Assert.Equal("JBSWY3DPEHPK3PXP", result!.Secret);
        Assert.Equal("SHA256", result.Algorithm);
        Assert.Equal(8, result.Digits);
        Assert.Equal(60, result.PeriodSeconds);
        Assert.Equal("GitHub", result.Issuer);
        Assert.Equal("octocat", result.AccountLabel);
    }

    [Fact]
    public void TryParse_MinimalUri_FallsBackToDefaults()
    {
        var ok = TotpUriParser.TryParse("otpauth://totp/example.com?secret=JBSWY3DPEHPK3PXP", out var result);

        Assert.True(ok);
        Assert.Equal("JBSWY3DPEHPK3PXP", result!.Secret);
        Assert.Equal("SHA1", result.Algorithm);
        Assert.Equal(6, result.Digits);
        Assert.Equal(30, result.PeriodSeconds);
        Assert.Null(result.Issuer);
        Assert.Equal("example.com", result.AccountLabel);
    }

    [Fact]
    public void TryParse_LabelWithoutIssuerPrefix_QueryIssuerStillApplied()
    {
        var ok = TotpUriParser.TryParse("otpauth://totp/octocat?secret=ABC&issuer=GitHub", out var result);

        Assert.True(ok);
        Assert.Equal("GitHub", result!.Issuer);
        Assert.Equal("octocat", result.AccountLabel);
    }

    [Fact]
    public void TryParse_QueryIssuerTakesPrecedenceOverLabelIssuer()
    {
        var ok = TotpUriParser.TryParse("otpauth://totp/LabelIssuer:octocat?secret=ABC&issuer=QueryIssuer", out var result);

        Assert.True(ok);
        Assert.Equal("QueryIssuer", result!.Issuer);
    }

    [Fact]
    public void TryParse_MissingSecret_ReturnsFalse()
    {
        var ok = TotpUriParser.TryParse("otpauth://totp/example.com?issuer=GitHub", out var result);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("otpauth://hotp/example.com?secret=ABC")] // 只支援 totp，不支援 hotp（計數器式的另一種 OTP）
    [InlineData("not a uri at all")]
    [InlineData("")]
    public void TryParse_NotAValidTotpUri_ReturnsFalse(string input)
    {
        var ok = TotpUriParser.TryParse(input, out var result);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_UrlEncodedLabel_DecodesCorrectly()
    {
        var ok = TotpUriParser.TryParse("otpauth://totp/GitHub:octocat%40example.com?secret=ABC", out var result);

        Assert.True(ok);
        Assert.Equal("octocat@example.com", result!.AccountLabel);
    }
}
