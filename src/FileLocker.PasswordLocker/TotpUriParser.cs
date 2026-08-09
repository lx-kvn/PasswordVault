namespace FileLocker.PasswordLocker;

public record TotpUriParseResult(string Secret, string Algorithm, int Digits, int PeriodSeconds, string? Issuer, string? AccountLabel);

/// <summary>
/// 解析 Google Authenticator 訂的事實標準格式：
/// <c>otpauth://totp/{Issuer}:{account}?secret=BASE32&amp;issuer=...&amp;algorithm=SHA1&amp;digits=6&amp;period=30</c>
/// ——這不是 RFC 標準（RFC 6238 只定義演算法本身，不定義密鑰交換格式），但幾乎所有支援 TOTP
/// 的網站產生的 QR code／「無法掃描改用文字」備援連結都遵循這個格式，是事實上的互通標準。
/// 沒有內建 System.Web.HttpUtility 可用（純類別庫、非 ASP.NET 專案），query string 自己手動
/// 解析，不需要额外套件。
/// </summary>
public static class TotpUriParser
{
    public static bool TryParse(string uri, out TotpUriParseResult? result)
    {
        result = null;

        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }
        if (!string.Equals(parsed.Scheme, "otpauth", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Host, "totp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryParams = ParseQuery(parsed.Query);
        if (!queryParams.TryGetValue("secret", out var secret) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var algorithm = queryParams.TryGetValue("algorithm", out var algorithmValue) ? algorithmValue.ToUpperInvariant() : "SHA1";
        var digits = queryParams.TryGetValue("digits", out var digitsValue) && int.TryParse(digitsValue, out var parsedDigits) ? parsedDigits : 6;
        var period = queryParams.TryGetValue("period", out var periodValue) && int.TryParse(periodValue, out var parsedPeriod) ? parsedPeriod : 30;

        // label 格式是 "Issuer:account" 或單純 "account"，出現在路徑段（例：
        // otpauth://totp/GitHub:octocat%40example.com）——query string 裡的 issuer 參數
        // 是後來才加的正規欄位，兩者可能同時存在，query 版本優先（多數產生器兩者一致，
        // 有分歧時 query 版本被視為權威來源）。
        var label = Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
        string? labelIssuer = null;
        string? accountLabel = label;
        var colonIndex = label.IndexOf(':');
        if (colonIndex >= 0)
        {
            labelIssuer = label[..colonIndex];
            accountLabel = label[(colonIndex + 1)..];
        }
        var issuer = queryParams.TryGetValue("issuer", out var issuerValue) ? issuerValue : labelIssuer;

        result = new TotpUriParseResult(secret, algorithm, digits, period, issuer, accountLabel);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }
            var key = Uri.UnescapeDataString(pair[..equalsIndex]);
            var value = Uri.UnescapeDataString(pair[(equalsIndex + 1)..]);
            result[key] = value;
        }
        return result;
    }
}
