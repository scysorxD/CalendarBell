using System.Text;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Sync;

namespace CalendarBell.Infrastructure.Tests;

public class UrlAndSecurityTests
{
    private const string SecretUrl = "https://calendar-a.example/owa/calendar/abc123/reachcalendar.ics?token=SECRET-TOKEN-42";

    [Theory]
    [InlineData("https://calendar-a.example/cal.ics", true)]
    [InlineData("https://calendar-a.example/published?id=1", true)]
    [InlineData("  https://calendar-a.example/cal.ics  ", true)]
    [InlineData("http://calendar-a.example/cal.ics", false)]
    [InlineData("ftp://calendar-a.example/cal.ics", false)]
    [InlineData("calendar-a.example/cal.ics", false)]
    [InlineData("", false)]
    [InlineData("https://user:pass@calendar-a.example/cal.ics", false)]
    [InlineData("http://localhost:8080/cal.ics", false)]
    public void Only_https_urls_are_accepted(string url, bool valid)
    {
        Assert.Equal(valid, UrlPolicy.Validate(url, allowHttpLocalhost: false).IsValid);
    }

    [Theory]
    [InlineData("http://localhost:8080/cal.ics")]
    [InlineData("http://127.0.0.1:8080/cal.ics")]
    [InlineData("http://[::1]:8080/cal.ics")]
    public void Http_localhost_is_allowed_only_in_test_mode(string url)
    {
        Assert.True(UrlPolicy.Validate(url, allowHttpLocalhost: true).IsValid);
        Assert.False(UrlPolicy.Validate("http://calendar-a.example/cal.ics", allowHttpLocalhost: true).IsValid);
    }

    [Fact]
    public void Webcal_links_are_normalized_to_https()
    {
        var result = UrlPolicy.Validate("webcal://calendar-a.example/cal.ics", allowHttpLocalhost: false);

        Assert.True(result.IsValid);
        Assert.Equal("https://calendar-a.example/cal.ics", result.Uri!.AbsoluteUri);
    }

    [Fact]
    public void Urls_longer_than_4096_characters_are_rejected()
    {
        var url = "https://calendar-a.example/" + new string('a', 4096);

        Assert.False(UrlPolicy.Validate(url, allowHttpLocalhost: false).IsValid);
    }

    [Fact]
    public void Validation_errors_never_echo_the_url()
    {
        var result = UrlPolicy.Validate("http://calendar-a.example/x.ics?token=SECRET", allowHttpLocalhost: false);

        Assert.DoesNotContain("SECRET", result.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-a.example", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void T26_dpapi_round_trips_and_ciphertext_hides_the_url()
    {
        var protector = new DpapiUrlProtector();

        var cipher = protector.Protect(SecretUrl);

        Assert.DoesNotContain("SECRET", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-a", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(Convert.FromBase64String(cipher)), StringComparison.Ordinal);
        Assert.Equal(SecretUrl, protector.Unprotect(cipher));
    }

    [Fact]
    public void Tampered_ciphertext_fails_safely()
    {
        var protector = new DpapiUrlProtector();
        var cipher = Convert.FromBase64String(protector.Protect(SecretUrl));
        cipher[^1] ^= 0xFF;

        Assert.Throws<UrlProtectionException>(() => protector.Unprotect(Convert.ToBase64String(cipher)));
    }

    [Theory]
    [InlineData("Connection to https://calendar-a.example/cal.ics?token=SECRET failed")]
    [InlineData("webcal://calendar-a.example/x?SECRET")]
    [InlineData("see http://localhost:8080/feed.ics?SECRET=1")]
    public void T27_sanitizer_removes_urls_and_queries(string message)
    {
        var redacted = LogSanitizer.Redact(message);

        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-a.example", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted-url]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizer_describes_exceptions_without_secrets()
    {
        var ex = new HttpRequestException("No such host is known. (" + SecretUrl + ")");

        var text = LogSanitizer.Describe(ex);

        Assert.StartsWith("HttpRequestException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }
}
