using System.Net;
using CalendarBell.Core.Models;
using CalendarBell.Infrastructure.Sync;
using CalendarBell.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace CalendarBell.Infrastructure.Tests;

public class CalendarFeedClientTests
{
    private const string Url = "https://calendar-a.example/feed.ics";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeHttpHandler _http = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly CalendarFeedClient _client;

    public CalendarFeedClientTests()
    {
        _client = new CalendarFeedClient(new FakeUrlProtector(), new SyncOptions { AllowHttpLocalhost = false, MaxResponseBytes = 1024 * 1024 }, _time, _http);
    }

    private static string Ics => IcsCalendarParserTests.Fixture("calendar-a-basic.ics");

    [Fact]
    public async Task Ok_response_returns_body_and_validators()
    {
        _http.Respond(Url, () => FakeHttpHandler.Ics(Ics, "\"v1\"", "Wed, 18 Nov 2026 11:00:00 GMT"));

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.Ok, result.Status);
        Assert.Contains("BEGIN:VCALENDAR", result.Body, StringComparison.Ordinal);
        Assert.Equal("\"v1\"", result.ETag);
        Assert.Equal("Wed, 18 Nov 2026 11:00:00 GMT", result.LastModified);
    }

    [Fact]
    public async Task Sends_user_agent_accept_and_conditional_headers()
    {
        _http.Respond(Url, () => new HttpResponseMessage(HttpStatusCode.NotModified));

        var result = await _client.FetchUrlAsync(Url, new ConditionalRequest("\"v1\"", "Wed, 18 Nov 2026 11:00:00 GMT"), Ct);

        Assert.Equal(FetchStatus.NotModified, result.Status);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith("CalendarBell/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Contains("text/calendar", request.Headers.Accept.ToString(), StringComparison.Ordinal);
        Assert.Equal("\"v1\"", request.Headers.IfNoneMatch.Single().Tag);
        Assert.NotNull(request.Headers.IfModifiedSince);
    }

    [Fact]
    public async Task Decrypts_calendar_url_for_fetch()
    {
        _http.Respond(Url, () => FakeHttpHandler.Ics(Ics));
        var calendar = new CalendarSource(Guid.NewGuid(), "Calendar A", new FakeUrlProtector().Protect(Url), true);

        var result = await _client.FetchAsync(calendar, null, Ct);

        Assert.Equal(FetchStatus.Ok, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Http_errors_are_reported_with_status(HttpStatusCode code)
    {
        _http.Respond(Url, () => new HttpResponseMessage(code));

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.HttpError, result.Status);
        Assert.Equal((int)code, result.HttpStatus);
    }

    [Fact]
    public async Task Rate_limit_reports_retry_after()
    {
        _http.Respond(Url, () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return r;
        });

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), result.RetryAfter);
    }

    [Fact]
    public async Task Timeout_after_15_seconds_is_reported()
    {
        _http.Route(Url, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var fetch = _client.FetchUrlAsync(Url, null, Ct);
        await WaitUntil(() => _http.RequestCount == 1);
        _time.Advance(TimeSpan.FromSeconds(16));
        var result = await fetch;

        Assert.Equal(FetchStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task Network_failure_is_reported_without_url()
    {
        _http.Route(Url, (_, _) => throw new HttpRequestException("No such host is known. (" + Url + "?token=SECRET)"));

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.NetworkError, result.Status);
        Assert.DoesNotContain("SECRET", result.ErrorDetail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-a.example", result.ErrorDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Body_over_limit_is_rejected_by_content_length()
    {
        _http.Respond(Url, () => FakeHttpHandler.Ics(new string('x', 2 * 1024 * 1024)));

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task Streamed_body_over_limit_is_rejected()
    {
        _http.Respond(Url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) });

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task Https_redirect_is_followed()
    {
        _http.Respond(Url, () => Redirect("https://cdn.calendar-a.example/feed.ics"));
        _http.Respond("https://cdn.calendar-a.example/feed.ics", () => FakeHttpHandler.Ics(Ics));

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.Ok, result.Status);
        Assert.Equal(2, _http.RequestCount);
    }

    [Fact]
    public async Task Redirect_to_http_is_refused()
    {
        _http.Respond(Url, () => Redirect("http://calendar-a.example/feed.ics"));

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.InsecureRedirect, result.Status);
        Assert.Equal(1, _http.RequestCount);
    }

    [Fact]
    public async Task More_than_three_redirects_are_refused()
    {
        for (var i = 0; i < 5; i++)
        {
            var next = $"https://calendar-a.example/hop{i + 1}.ics";
            _http.Respond(i == 0 ? Url : $"https://calendar-a.example/hop{i}.ics", () => Redirect(next));
        }

        var result = await _client.FetchUrlAsync(Url, null, Ct);

        Assert.Equal(FetchStatus.TooManyRedirects, result.Status);
        Assert.Equal(4, _http.RequestCount);
    }

    [Fact]
    public async Task Invalid_url_is_not_requested()
    {
        var result = await _client.FetchUrlAsync("http://calendar-a.example/feed.ics", null, Ct);

        Assert.Equal(FetchStatus.InvalidUrl, result.Status);
        Assert.Equal(0, _http.RequestCount);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location);
        return r;
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
