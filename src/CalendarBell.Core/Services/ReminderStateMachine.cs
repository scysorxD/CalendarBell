using CalendarBell.Core.Models;

namespace CalendarBell.Core.Services;

public readonly record struct DueEvaluation(DueAction Action, DateTimeOffset? DueUtc, ReminderKind Kind);

/// <summary>
/// Pure transition functions for <see cref="ReminderRecord"/> (spec §6.2). No clocks, no I/O: every
/// function takes "now" explicitly so the behaviour is deterministic and testable.
/// </summary>
public static class ReminderStateMachine
{
    /// <summary>The next instant at which this record wants a notification, or null if none is pending.</summary>
    public static DateTimeOffset? NextDueUtc(ReminderRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Dismissed || record.Cancelled)
        {
            return null;
        }

        if (record.SnoozedUntilUtc is { } snoozed)
        {
            return snoozed;
        }

        return record.InitialNotificationSent ? null : record.InitialDueUtc;
    }

    public static DueEvaluation Evaluate(ReminderRecord record, DateTimeOffset nowUtc, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var due = NextDueUtc(record);
        if (due is null)
        {
            return new DueEvaluation(DueAction.None, null, ReminderKind.Initial);
        }

        var kind = record.SnoozedUntilUtc is null ? ReminderKind.Initial : ReminderKind.Snooze;
        if (due.Value > nowUtc + options.CoalesceWindow)
        {
            return new DueEvaluation(DueAction.NotYet, due, kind);
        }

        // Never send obsolete alerts: from start + 60 s on, the reminder expires silently.
        return nowUtc >= record.EventStartUtc + options.StartGrace
            ? new DueEvaluation(DueAction.Expire, due, kind)
            : new DueEvaluation(DueAction.Fire, due, kind);
    }

    public static ReminderStatus Status(ReminderRecord record, DateTimeOffset nowUtc, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(options);
        if (record.Cancelled)
        {
            return ReminderStatus.Cancelled;
        }

        if (record.Dismissed)
        {
            return ReminderStatus.Dismissed;
        }

        if (nowUtc >= record.EventStartUtc + options.StartGrace)
        {
            return ReminderStatus.Expired;
        }

        if (record.SnoozedUntilUtc is not null)
        {
            return ReminderStatus.Snoozed;
        }

        return record.InitialNotificationSent ? ReminderStatus.Notified : ReminderStatus.Pending;
    }

    /// <summary>The current phase was shown to the user.</summary>
    public static ReminderRecord ApplyFired(ReminderRecord record, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            InitialNotificationSent = true,
            SnoozedUntilUtc = null,
            SnoozeMinutesBefore = null,
            LastNotifiedUtc = nowUtc,
            StateVersion = record.StateVersion + 1,
        };
    }

    /// <summary>The current phase is consumed without a toast (expired, or due while paused).</summary>
    public static ReminderRecord ApplyConsumed(ReminderRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            InitialNotificationSent = true,
            SnoozedUntilUtc = null,
            SnoozeMinutesBefore = null,
            StateVersion = record.StateVersion + 1,
        };
    }

    public static (ReminderRecord Record, SnoozeOutcome Outcome) ApplySnooze(
        ReminderRecord record, int minutesBeforeStart, DateTimeOffset nowUtc, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!SnoozeTargets.IsValidChoice(minutesBeforeStart, options))
        {
            return (record, SnoozeOutcome.InvalidTarget);
        }

        if (record.Dismissed || record.Cancelled)
        {
            return (record, SnoozeOutcome.NotActive);
        }

        if (!SnoozeTargets.IsFuture(record.EventStartUtc, minutesBeforeStart, nowUtc, options))
        {
            return (record, SnoozeOutcome.TargetNotInFuture);
        }

        var target = SnoozeTargets.TargetUtc(record.EventStartUtc, minutesBeforeStart);
        if (record.SnoozedUntilUtc == target && record.InitialNotificationSent)
        {
            return (record, SnoozeOutcome.Scheduled); // repeated click: no change
        }

        return (record with
        {
            InitialNotificationSent = true,
            SnoozedUntilUtc = target,
            SnoozeMinutesBefore = minutesBeforeStart,
            StateVersion = record.StateVersion + 1,
        }, SnoozeOutcome.Scheduled);
    }

    public static ReminderRecord ApplyDismiss(ReminderRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Dismissed && record.SnoozedUntilUtc is null)
        {
            return record;
        }

        return record with
        {
            Dismissed = true,
            SnoozedUntilUtc = null,
            SnoozeMinutesBefore = null,
            StateVersion = record.StateVersion + 1,
        };
    }

    public static ReminderRecord ApplyCancelled(ReminderRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Cancelled)
        {
            return record;
        }

        return record with
        {
            Cancelled = true,
            SnoozedUntilUtc = null,
            SnoozeMinutesBefore = null,
            StateVersion = record.StateVersion + 1,
        };
    }

    /// <summary>
    /// Reconciles a record with the latest occurrence from the feed. A real start-time change starts a new
    /// temporal revision (back to Pending, even after Dismiss); other changes keep the reminder state.
    /// </summary>
    public static (ReminderRecord Record, OccurrenceChange Change) ApplyOccurrence(
        ReminderRecord record, EventOccurrence occurrence, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(options);

        if (occurrence.StartUtc != record.EventStartUtc)
        {
            return (record with
            {
                RevisionFingerprint = occurrence.RevisionFingerprint,
                EventStartUtc = occurrence.StartUtc,
                InitialDueUtc = occurrence.StartUtc - options.InitialLead,
                InitialNotificationSent = false,
                SnoozedUntilUtc = null,
                SnoozeMinutesBefore = null,
                Dismissed = false,
                Cancelled = false,
                StateVersion = record.StateVersion + 1,
            }, OccurrenceChange.StartChanged);
        }

        if (record.Cancelled)
        {
            return (record with
            {
                RevisionFingerprint = occurrence.RevisionFingerprint,
                Cancelled = false,
                StateVersion = record.StateVersion + 1,
            }, OccurrenceChange.Reactivated);
        }

        if (!string.Equals(record.RevisionFingerprint, occurrence.RevisionFingerprint, StringComparison.Ordinal))
        {
            return (record with
            {
                RevisionFingerprint = occurrence.RevisionFingerprint,
                StateVersion = record.StateVersion + 1,
            }, OccurrenceChange.DetailsChanged);
        }

        return (record, OccurrenceChange.None);
    }
}
