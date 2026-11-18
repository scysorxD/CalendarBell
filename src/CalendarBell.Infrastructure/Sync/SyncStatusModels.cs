namespace CalendarBell.Infrastructure.Sync;

/// <summary>States shown in Settings (spec §3.2).</summary>
public enum CalendarSyncState
{
    NeverSynced,
    Refreshing,
    Synced,
    NetworkError,
    InvalidCalendar,
    Disabled,
}

public sealed record CalendarSyncStatus(
    Guid CalendarId,
    string Name,
    bool Enabled,
    CalendarSyncState State,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextRefreshUtc,
    int? HttpStatus,
    string? Message,
    int UpcomingCount,
    bool IsStale);

public sealed record SyncOutcome(Guid CalendarId, CalendarSyncState State, string? Message);

public sealed record TestConnectionResult(bool Success, string Message, int UpcomingCount, DateTimeOffset? CheckedAtUtc);
