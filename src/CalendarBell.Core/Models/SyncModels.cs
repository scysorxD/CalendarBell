namespace CalendarBell.Core.Models;

public enum FetchStatus
{
    Ok,
    NotModified,
    HttpError,
    RateLimited,
    Timeout,
    NetworkError,
    TooLarge,
    InvalidUrl,
    InsecureRedirect,
    TooManyRedirects,
    UrlUnreadable,
}

/// <summary>Validators for an HTTP conditional GET.</summary>
public sealed record ConditionalRequest(string? ETag, string? LastModified);

/// <summary>Outcome of one download. <see cref="ErrorDetail"/> is sanitized (no URL, no query string).</summary>
public sealed record CalendarFetchResult(
    FetchStatus Status,
    string? Body = null,
    string? ETag = null,
    string? LastModified = null,
    int? HttpStatus = null,
    TimeSpan? RetryAfter = null,
    string? ErrorDetail = null)
{
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Persisted per-calendar sync metadata (no URL, no content).</summary>
public sealed record SyncRecord(Guid CalendarId)
{
    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? NextRefreshUtc { get; init; }

    public string? LastState { get; init; }

    public string? LastError { get; init; }

    public int? LastHttpStatus { get; init; }

    public int UpcomingCount { get; init; }
}
