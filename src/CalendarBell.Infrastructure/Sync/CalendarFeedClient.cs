using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarBell.Infrastructure.Sync;

/// <summary>
/// Downloads ICS feeds (spec §5.1): one managed HttpClient, 15 s timeout, conditional GET, manual redirects
/// (max 3, HTTPS only), 10 MiB body cap read incrementally, TLS always validated (no bypass exists).
/// </summary>
public sealed class CalendarFeedClient : ICalendarFeedClient, IDisposable
{
    private readonly IUrlProtector _protector;
    private readonly SyncOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public CalendarFeedClient(IUrlProtector protector, SyncOptions options, TimeProvider time, HttpMessageHandler? handler = null, ILogger<CalendarFeedClient>? logger = null)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false,
            UseProxy = true,
            PreAuthenticate = false,
            Credentials = null,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/calendar");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/plain;q=0.5");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*;q=0.1");
    }

    public Task<CalendarFetchResult> FetchAsync(CalendarSource calendar, CancellationToken ct) => FetchAsync(calendar, null, ct);

    public Task<CalendarFetchResult> FetchAsync(CalendarSource calendar, ConditionalRequest? conditional, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        string url;
        try
        {
            url = _protector.Unprotect(calendar.EncryptedUrl);
        }
        catch (UrlProtectionException)
        {
            return Task.FromResult(new CalendarFetchResult(FetchStatus.UrlUnreadable));
        }

        return FetchUrlAsync(url, conditional, ct);
    }

    public async Task<CalendarFetchResult> FetchUrlAsync(string url, ConditionalRequest? conditional, CancellationToken ct)
    {
        var validation = UrlPolicy.Validate(url, _options.AllowHttpLocalhost);
        if (!validation.IsValid)
        {
            return new CalendarFetchResult(FetchStatus.InvalidUrl, ErrorDetail: validation.Error);
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(_options.RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var result = await SendWithRedirectsAsync(validation.Uri!, conditional, linked.Token).ConfigureAwait(false);
            return result with { Elapsed = stopwatch.Elapsed };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new CalendarFetchResult(FetchStatus.Timeout) { Elapsed = stopwatch.Elapsed };
        }
        catch (HttpRequestException ex)
        {
            return new CalendarFetchResult(FetchStatus.NetworkError, ErrorDetail: NetworkDetail(ex), HttpStatus: (int?)ex.StatusCode) { Elapsed = stopwatch.Elapsed };
        }
        catch (AuthenticationException ex)
        {
            return new CalendarFetchResult(FetchStatus.NetworkError, ErrorDetail: "TLS: " + LogSanitizer.Redact(ex.Message)) { Elapsed = stopwatch.Elapsed };
        }
        catch (IOException ex)
        {
            return new CalendarFetchResult(FetchStatus.NetworkError, ErrorDetail: LogSanitizer.Redact(ex.Message)) { Elapsed = stopwatch.Elapsed };
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<CalendarFetchResult> SendWithRedirectsAsync(Uri uri, ConditionalRequest? conditional, CancellationToken ct)
    {
        var redirects = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(conditional?.ETag) && EntityTagHeaderValue.TryParse(conditional.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            if (!string.IsNullOrEmpty(conditional?.LastModified) && DateTimeOffset.TryParse(conditional.LastModified, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var lastModified))
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= _options.MaxRedirects)
                {
                    return new CalendarFetchResult(FetchStatus.TooManyRedirects, HttpStatus: status);
                }

                if (response.Headers.Location is null)
                {
                    return new CalendarFetchResult(FetchStatus.HttpError, HttpStatus: status);
                }

                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                if (!UrlPolicy.IsAllowedScheme(next, _options.AllowHttpLocalhost) || !string.IsNullOrEmpty(next.UserInfo))
                {
                    _logger.LogWarning("Redirect to a non-HTTPS address refused");
                    return new CalendarFetchResult(FetchStatus.InsecureRedirect, HttpStatus: status);
                }

                uri = next;
                redirects++;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new CalendarFetchResult(FetchStatus.NotModified, ETag: response.Headers.ETag?.ToString() ?? conditional?.ETag, LastModified: conditional?.LastModified, HttpStatus: status);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new CalendarFetchResult(FetchStatus.RateLimited, HttpStatus: status, RetryAfter: RetryAfter(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new CalendarFetchResult(FetchStatus.HttpError, HttpStatus: status, RetryAfter: RetryAfter(response));
            }

            var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            if (body is null)
            {
                return new CalendarFetchResult(FetchStatus.TooLarge, HttpStatus: status);
            }

            var lastModifiedHeader = response.Content.Headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                ?? (response.Content.Headers.TryGetValues("Last-Modified", out var values) ? values.FirstOrDefault() : null);
            return new CalendarFetchResult(FetchStatus.Ok, body, response.Headers.ETag?.ToString(), lastModifiedHeader, status);
        }
    }

    private async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > _options.MaxResponseBytes)
        {
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > _options.MaxResponseBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            var encoding = Encoding.UTF8;
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch (ArgumentException)
                {
                    encoding = Encoding.UTF8;
                }
            }

            buffer.Position = 0;
            using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string NetworkDetail(HttpRequestException ex) =>
        LogSanitizer.Redact((ex.InnerException as System.Net.Sockets.SocketException)?.Message ?? ex.Message);
}
