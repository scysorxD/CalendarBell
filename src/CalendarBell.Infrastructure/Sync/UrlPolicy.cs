using CalendarBell.Core.Resources;

namespace CalendarBell.Infrastructure.Sync;

public sealed record UrlValidationResult(bool IsValid, Uri? Uri, string? Error)
{
    public static UrlValidationResult Fail(string resourceKey) => new(false, null, Strings.Get(resourceKey));
}

/// <summary>
/// Calendar URL rules (spec §3.2, §5.1): required, ≤ 4096 chars, absolute HTTPS; webcal(s):// is accepted and
/// normalized to https://; http:// only for localhost when test mode allows it; no embedded credentials.
/// Error messages never contain the URL.
/// </summary>
public static class UrlPolicy
{
    public const int MaxLength = 4096;

    public static UrlValidationResult Validate(string? url, bool allowHttpLocalhost)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return UrlValidationResult.Fail("Url_Required");
        }

        if (text.Length > MaxLength)
        {
            return UrlValidationResult.Fail("Url_TooLong");
        }

        if (text.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text["webcals://".Length..];
        }
        else if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text["webcal://".Length..];
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return UrlValidationResult.Fail("Url_Invalid");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return UrlValidationResult.Fail("Url_NoCredentials");
        }

        return IsAllowedScheme(uri, allowHttpLocalhost)
            ? new UrlValidationResult(true, uri, null)
            : UrlValidationResult.Fail("Url_HttpsRequired");
    }

    public static bool IsAllowedScheme(Uri uri, bool allowHttpLocalhost)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return allowHttpLocalhost && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}
