using CalendarBell.Core.Models;
using CalendarBell.Core.Services;

namespace CalendarBell.Core.Tests;

public class ReminderStateMachineTests
{
    private static readonly ReminderOptions Options = ReminderOptions.Default;
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    internal static DateTimeOffset T(int hour, int minute, double second = 0) =>
        new DateTimeOffset(2026, 11, 18, hour, minute, 0, TimeSpan.Zero).AddSeconds(second);

    internal static EventOccurrence Occ(DateTimeOffset start, string key = "k1", Guid? calendarId = null, string availability = Availability.Busy, int durationMinutes = 30) =>
        new(calendarId ?? CalendarA, "uid-" + key, key, start, start.AddMinutes(durationMinutes), availability,
            IsAllDay: false, IsCancelled: false, PrivacySafeTitle: null, RevisionFingerprint: "fp-" + start.ToUnixTimeSeconds() + availability + durationMinutes);

    private static ReminderRecord NewRecord(DateTimeOffset start) => ReminderRecord.CreateFor(Occ(start), Options);

    [Fact]
    public void T01_initial_reminder_is_due_15_minutes_before_start()
    {
        var record = NewRecord(T(10, 30));

        Assert.Equal(T(10, 15), record.InitialDueUtc);
        Assert.Equal(T(10, 15), ReminderStateMachine.NextDueUtc(record));
        Assert.Equal(ReminderStatus.Pending, ReminderStateMachine.Status(record, T(10, 0), Options));
    }

    [Fact]
    public void Evaluate_before_due_is_not_yet()
    {
        var record = NewRecord(T(10, 30));

        var evaluation = ReminderStateMachine.Evaluate(record, T(10, 14, 58), Options);

        Assert.Equal(DueAction.NotYet, evaluation.Action);
        Assert.Equal(T(10, 15), evaluation.DueUtc);
    }

    [Fact]
    public void Evaluate_at_due_fires_initial()
    {
        var record = NewRecord(T(10, 30));

        var evaluation = ReminderStateMachine.Evaluate(record, T(10, 15), Options);

        Assert.Equal(DueAction.Fire, evaluation.Action);
        Assert.Equal(ReminderKind.Initial, evaluation.Kind);
    }

    [Fact]
    public void Evaluate_within_coalescing_window_fires()
    {
        var record = NewRecord(T(10, 30));

        Assert.Equal(DueAction.Fire, ReminderStateMachine.Evaluate(record, T(10, 14, 59.2), Options).Action);
    }

    [Fact]
    public void Fired_initial_is_not_due_again()
    {
        var record = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        Assert.True(record.InitialNotificationSent);
        Assert.Equal(T(10, 15), record.LastNotifiedUtc);
        Assert.Null(ReminderStateMachine.NextDueUtc(record));
        Assert.Equal(ReminderStatus.Notified, ReminderStateMachine.Status(record, T(10, 16), Options));
    }

    [Fact]
    public void T03_snooze_10_at_1016_targets_1020_not_1026()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        var (record, outcome) = ReminderStateMachine.ApplySnooze(notified, 10, T(10, 16), Options);

        Assert.Equal(SnoozeOutcome.Scheduled, outcome);
        Assert.Equal(T(10, 20), record.SnoozedUntilUtc);
        Assert.Equal(T(10, 20), ReminderStateMachine.NextDueUtc(record));
        Assert.Equal(10, record.SnoozeMinutesBefore);
    }

    [Fact]
    public void T04_snooze_10_at_1022_is_rejected_without_instant_alert()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        var (record, outcome) = ReminderStateMachine.ApplySnooze(notified, 10, T(10, 22), Options);

        Assert.Equal(SnoozeOutcome.TargetNotInFuture, outcome);
        Assert.Same(notified, record);
        Assert.Null(ReminderStateMachine.NextDueUtc(record));
    }

    [Fact]
    public void T05_snooze_5_at_1022_targets_1025()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        var (record, _) = ReminderStateMachine.ApplySnooze(notified, 5, T(10, 22), Options);

        Assert.Equal(T(10, 25), record.SnoozedUntilUtc);
    }

    [Fact]
    public void T06_snooze_at_start_at_1028_targets_1030()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        var (record, _) = ReminderStateMachine.ApplySnooze(notified, 0, T(10, 28), Options);

        Assert.Equal(T(10, 30), record.SnoozedUntilUtc);
    }

    [Theory]
    [InlineData(10, 15, 0, new[] { 10, 5, 0 })]
    [InlineData(10, 22, 0, new[] { 5, 0 })]
    [InlineData(10, 29, 57, new[] { 0 })]
    [InlineData(10, 29, 58.5, new int[0])]
    [InlineData(10, 30, 0, new int[0])]
    public void Available_targets_are_strictly_future_with_two_second_margin(int h, int m, double s, int[] expected)
    {
        Assert.Equal(expected, SnoozeTargets.Available(T(10, 30), T(h, m, s), Options));
    }

    [Fact]
    public void Snooze_value_outside_10_5_0_is_rejected()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        Assert.Equal(SnoozeOutcome.InvalidTarget, ReminderStateMachine.ApplySnooze(notified, 7, T(10, 16), Options).Outcome);
    }

    [Fact]
    public void Repeated_identical_snooze_is_idempotent()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));
        var (first, _) = ReminderStateMachine.ApplySnooze(notified, 5, T(10, 16), Options);

        var (second, outcome) = ReminderStateMachine.ApplySnooze(first, 5, T(10, 16, 1), Options);

        Assert.Equal(SnoozeOutcome.Scheduled, outcome);
        Assert.Equal(first.SnoozedUntilUtc, second.SnoozedUntilUtc);
        Assert.Equal(first.StateVersion, second.StateVersion);
    }

    [Fact]
    public void Snooze_fires_once_and_then_is_cleared()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));
        var (snoozed, _) = ReminderStateMachine.ApplySnooze(notified, 5, T(10, 16), Options);

        var evaluation = ReminderStateMachine.Evaluate(snoozed, T(10, 25), Options);
        var fired = ReminderStateMachine.ApplyFired(snoozed, T(10, 25));

        Assert.Equal(DueAction.Fire, evaluation.Action);
        Assert.Equal(ReminderKind.Snooze, evaluation.Kind);
        Assert.Null(fired.SnoozedUntilUtc);
        Assert.Null(ReminderStateMachine.NextDueUtc(fired));
    }

    [Fact]
    public void Snooze_before_initial_skips_the_initial_phase()
    {
        var (snoozed, _) = ReminderStateMachine.ApplySnooze(NewRecord(T(10, 30)), 10, T(10, 0), Options);

        Assert.Equal(T(10, 20), ReminderStateMachine.NextDueUtc(snoozed));
        Assert.Equal(ReminderStatus.Snoozed, ReminderStateMachine.Status(snoozed, T(10, 1), Options));
    }

    [Fact]
    public void Dismiss_cancels_snooze_and_any_further_reminder()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));
        var (snoozed, _) = ReminderStateMachine.ApplySnooze(notified, 5, T(10, 16), Options);

        var dismissed = ReminderStateMachine.ApplyDismiss(snoozed);

        Assert.True(dismissed.Dismissed);
        Assert.Null(dismissed.SnoozedUntilUtc);
        Assert.Null(ReminderStateMachine.NextDueUtc(dismissed));
        Assert.Equal(ReminderStatus.Dismissed, ReminderStateMachine.Status(dismissed, T(10, 20), Options));
        Assert.Equal(SnoozeOutcome.NotActive, ReminderStateMachine.ApplySnooze(dismissed, 0, T(10, 20), Options).Outcome);
    }

    [Fact]
    public void Late_record_before_start_fires_immediately()
    {
        var record = NewRecord(T(10, 30));

        var evaluation = ReminderStateMachine.Evaluate(record, T(10, 18), Options);

        Assert.Equal(DueAction.Fire, evaluation.Action);
    }

    [Fact]
    public void Within_first_minute_after_start_fires_starting_now_once()
    {
        var record = NewRecord(T(10, 30));

        Assert.Equal(DueAction.Fire, ReminderStateMachine.Evaluate(record, T(10, 30, 30), Options).Action);
    }

    [Fact]
    public void T18_waking_ten_minutes_after_start_expires_without_alert()
    {
        var record = NewRecord(T(10, 30));

        Assert.Equal(DueAction.Expire, ReminderStateMachine.Evaluate(record, T(10, 40), Options).Action);
        Assert.Equal(DueAction.Expire, ReminderStateMachine.Evaluate(record, T(10, 31), Options).Action);
        Assert.Equal(ReminderStatus.Expired, ReminderStateMachine.Status(record, T(10, 40), Options));
    }

    [Fact]
    public void Expired_snooze_is_consumed_not_fired()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));
        var (snoozed, _) = ReminderStateMachine.ApplySnooze(notified, 0, T(10, 16), Options);

        Assert.Equal(DueAction.Expire, ReminderStateMachine.Evaluate(snoozed, T(10, 35), Options).Action);

        var consumed = ReminderStateMachine.ApplyConsumed(snoozed);
        Assert.Null(ReminderStateMachine.NextDueUtc(consumed));
    }

    [Fact]
    public void Start_change_resets_to_pending_even_if_dismissed()
    {
        var dismissed = ReminderStateMachine.ApplyDismiss(ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15)));

        var (updated, change) = ReminderStateMachine.ApplyOccurrence(dismissed, Occ(T(11, 30)), Options);

        Assert.Equal(OccurrenceChange.StartChanged, change);
        Assert.False(updated.Dismissed);
        Assert.False(updated.InitialNotificationSent);
        Assert.Equal(T(11, 15), updated.InitialDueUtc);
        Assert.Equal(T(11, 30), updated.EventStartUtc);
    }

    [Fact]
    public void Cosmetic_change_keeps_state()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));
        var changedEnd = Occ(T(10, 30), durationMinutes: 45);

        var (updated, change) = ReminderStateMachine.ApplyOccurrence(notified, changedEnd, Options);

        Assert.Equal(OccurrenceChange.DetailsChanged, change);
        Assert.True(updated.InitialNotificationSent);
        Assert.Equal(changedEnd.RevisionFingerprint, updated.RevisionFingerprint);
    }

    [Fact]
    public void Identical_occurrence_is_no_change()
    {
        var notified = ReminderStateMachine.ApplyFired(NewRecord(T(10, 30)), T(10, 15));

        var (updated, change) = ReminderStateMachine.ApplyOccurrence(notified, Occ(T(10, 30)), Options);

        Assert.Equal(OccurrenceChange.None, change);
        Assert.Same(notified, updated);
    }

    [Fact]
    public void Cancelled_record_never_fires_and_reports_cancelled()
    {
        var cancelled = ReminderStateMachine.ApplyCancelled(NewRecord(T(10, 30)));

        Assert.Null(ReminderStateMachine.NextDueUtc(cancelled));
        Assert.Equal(ReminderStatus.Cancelled, ReminderStateMachine.Status(cancelled, T(10, 0), Options));
    }

    [Fact]
    public void Cancelled_occurrence_reappearing_is_reactivated()
    {
        var cancelled = ReminderStateMachine.ApplyCancelled(NewRecord(T(10, 30)));

        var (updated, change) = ReminderStateMachine.ApplyOccurrence(cancelled, Occ(T(10, 30)), Options);

        Assert.Equal(OccurrenceChange.Reactivated, change);
        Assert.False(updated.Cancelled);
        Assert.Equal(T(10, 15), ReminderStateMachine.NextDueUtc(updated));
    }

    [Fact]
    public void Every_mutation_increments_state_version()
    {
        var record = NewRecord(T(10, 30));
        var fired = ReminderStateMachine.ApplyFired(record, T(10, 15));
        var (snoozed, _) = ReminderStateMachine.ApplySnooze(fired, 5, T(10, 16), Options);
        var dismissed = ReminderStateMachine.ApplyDismiss(snoozed);

        Assert.True(fired.StateVersion > record.StateVersion);
        Assert.True(snoozed.StateVersion > fired.StateVersion);
        Assert.True(dismissed.StateVersion > snoozed.StateVersion);
    }
}
