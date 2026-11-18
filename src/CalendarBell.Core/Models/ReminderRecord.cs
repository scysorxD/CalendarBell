using CalendarBell.Core.Services;

namespace CalendarBell.Core.Models;

/// <summary>Persisted reminder state of one occurrence (spec §6.2). Immutable; transitions live in <see cref="ReminderStateMachine"/>.</summary>
public sealed record ReminderRecord(
    Guid CalendarId,
    string OccurrenceKey,
    string RevisionFingerprint,
    DateTimeOffset EventStartUtc,
    DateTimeOffset InitialDueUtc,
    bool InitialNotificationSent,
    DateTimeOffset? SnoozedUntilUtc,
    bool Dismissed,
    bool Cancelled,
    DateTimeOffset? LastNotifiedUtc,
    string? LastToastTag,
    string? LastToastGroup,
    long StateVersion)
{
    /// <summary>The snooze choice (10, 5 or 0 minutes before start) that produced <see cref="SnoozedUntilUtc"/>.</summary>
    public int? SnoozeMinutesBefore { get; init; }

    public static ReminderRecord CreateFor(EventOccurrence occurrence, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(options);
        return new ReminderRecord(
            occurrence.CalendarId,
            occurrence.OccurrenceKey,
            occurrence.RevisionFingerprint,
            occurrence.StartUtc,
            occurrence.StartUtc - options.InitialLead,
            InitialNotificationSent: false,
            SnoozedUntilUtc: null,
            Dismissed: false,
            Cancelled: occurrence.IsCancelled,
            LastNotifiedUtc: null,
            LastToastTag: null,
            LastToastGroup: null,
            StateVersion: 1);
    }
}
