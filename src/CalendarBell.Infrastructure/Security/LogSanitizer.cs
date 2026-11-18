using System.Text.RegularExpressions;

namespace CalendarBell.Infrastructure.Security;

/// <summary>
/// Removes anything that looks like a URL (with its query string) from text that may reach logs, diagnostics or
/// the UI. Applied by the file logger to every line and exception, and by code that describes errors.
/// </summary>
public static partial class LogSanitizer
{
    public const string Placeholder = "[redacted-url]";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = UrlRegex().Replace(text, Placeholder);
        return SecretParamRegex().Replace(redacted, "$1=[redacted]");
    }

    /// <summary>"ExceptionType: sanitized message" — safe to log or show.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var root = exception;
        while (root is AggregateException or HttpRequestException && root.InnerException is { } inner)
        {
            root = inner;
        }

        var message = Redact(root.Message);
        return root == exception
            ? $"{exception.GetType().Name}: {message}"
            : $"{exception.GetType().Name} ({root.GetType().Name}): {message}";
    }

    [GeneratedRegex(@"\b(?:https?|webcals?|wss?|ftp)://[^\s""'<>()]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // Defense in depth for bare query fragments such as "token=abc" that are not part of a full URL.
    [GeneratedRegex(@"\b(token|key|sig|signature|secret|password|pwd|auth|code)=[^\s&""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParamRegex();
}
