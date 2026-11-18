using CalendarBell.Core.Models;

namespace CalendarBell.Core.Interfaces;

/// <summary>How a snooze request treats a target that is no longer in the future.</summary>
public enum SnoozeMode
{
    /// <summary>Reject past targets (Upcoming window, API callers).</summary>
    Strict,

    /// <summary>Toast actions: the toast may have stayed on screen past the chosen target; use the next later future target.</summary>
    NextFutureFallback,
}

public sealed record SnoozeResult(string OccurrenceKey, SnoozeOutcome Outcome, DateTimeOffset? TargetUtc);

/// <summary>A reminder phase that is due now, handed to the notification layer.</summary>
public sealed record DueReminder(
    ReminderRecord Record,
    EventOccurrence Occurrence,
    string CalendarName,
    ReminderKind Kind,
    DateTimeOffset FiredAtUtc);

/// <summary>An occurrence as shown in the Upcoming reminders window.</summary>
public sealed record UpcomingReminder(
    ReminderRecord Record,
    EventOccurrence Occurrence,
    string CalendarName,
    ReminderStatus Status,
    IReadOnlyList<int> AvailableSnoozeTargets);

/// <summary>Receives due reminders (to batch and show) and requests to take down visible toasts.</summary>
public interface IReminderSink
{
    void Enqueue(IReadOnlyList<DueReminder> due);

    void RetractToast(string tag, string group);
}

/// <summary>Spec §7.3, extended with the lifecycle operations the app needs.</summary>
public interface IReminderScheduler
{
    event EventHandler? Changed;

    bool HasStorageError { get; }

    Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, CancellationToken ct);

    Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, CancellationToken ct);

    /// <summary><paramref name="minutesBeforeStart"/> is resolved against each occurrence's own start; it is not a delay.</summary>
    Task<IReadOnlyList<SnoozeResult>> SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, CancellationToken ct);

    Task<IReadOnlyList<SnoozeResult>> SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, SnoozeMode mode, CancellationToken ct);

    Task DismissAsync(IReadOnlyList<string> occurrenceKeys, CancellationToken ct);

    Task RecordToastAsync(IReadOnlyList<string> occurrenceKeys, string tag, string group, CancellationToken ct);

    Task RemoveCalendarDataAsync(Guid calendarId, CancellationToken ct);

    Task RecalculateAsync(CancellationToken ct);

    void UpdateCalendarState(Guid calendarId, string name, bool enabled, DateTimeOffset? lastSuccessUtc);

    void ForgetCalendar(Guid calendarId);

    void SetPausedUntil(DateTimeOffset? pausedUntilUtc);

    bool IsCurrent(string occurrenceKey, long stateVersion);

    IReadOnlyList<UpcomingReminder> GetUpcoming(DateTimeOffset fromUtc, DateTimeOffset toUtc);

    int PendingCount { get; }
}
