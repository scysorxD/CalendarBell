namespace CalendarBell.Core.Models;

/// <summary>User-visible state of an occurrence's reminder (Upcoming reminders window).</summary>
public enum ReminderStatus
{
    Pending,
    Notified,
    Snoozed,
    Dismissed,
    Cancelled,
    Expired,
}

/// <summary>Which phase produced a due reminder.</summary>
public enum ReminderKind
{
    Initial,
    Snooze,
}

public enum DueAction
{
    None,
    NotYet,
    Fire,
    Expire,
}

public enum SnoozeOutcome
{
    Scheduled,
    TargetNotInFuture,
    InvalidTarget,
    NotActive,
    NotFound,
}

public enum OccurrenceChange
{
    None,
    DetailsChanged,
    StartChanged,
    Reactivated,
}
