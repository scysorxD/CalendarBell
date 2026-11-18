using System.Reflection;

namespace CalendarBell.Infrastructure.Sync;

/// <summary>HTTP and polling limits (spec §1.3, §5.1).</summary>
public sealed record SyncOptions
{
    public const string AllowHttpLocalhostVariable = "CALENDARBELL_ALLOW_HTTP_LOCALHOST";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public long MaxResponseBytes { get; init; } = 10L * 1024 * 1024;

    public int MaxRedirects { get; init; } = 3;

    public int MaxConcurrentDownloads { get; init; } = 3;

    /// <summary>Scheduled refreshes start after a random delay up to this, so feeds are not hit all at once.</summary>
    public TimeSpan MaxStartJitter { get; init; } = TimeSpan.FromSeconds(1.5);

    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(30)];

    /// <summary>±10 % randomization of backoff delays.</summary>
    public double BackoffJitter { get; init; } = 0.10;

    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromHours(6);

    public bool AllowHttpLocalhost { get; init; } = DefaultAllowHttpLocalhost;

    public string UserAgent { get; init; } = "CalendarBell/" + AppVersion;

    /// <summary>http://localhost feeds are only for development: DEBUG builds or CALENDARBELL_ALLOW_HTTP_LOCALHOST=1.</summary>
    public static bool DefaultAllowHttpLocalhost =>
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable(AllowHttpLocalhostVariable) == "1";
#endif

    public static string AppVersion =>
        typeof(SyncOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
}
