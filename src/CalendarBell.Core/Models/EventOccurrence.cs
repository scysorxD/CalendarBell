namespace CalendarBell.Core.Models;

/// <summary>
/// One concrete (expanded) occurrence of an ICS event.
/// <see cref="OccurrenceKey"/> is a stable hash of (CalendarId, UID, RECURRENCE-ID) and never contains the current start time.
/// </summary>
public sealed record EventOccurrence(
    Guid CalendarId,
    string Uid,
    string OccurrenceKey,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Availability, // Busy|Tentative|OutOfOffice|Unknown
    bool IsAllDay,
    bool IsCancelled,
    string? PrivacySafeTitle,
    string RevisionFingerprint);
