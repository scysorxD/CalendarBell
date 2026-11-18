using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Services;
using CalendarBell.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using static CalendarBell.Core.Tests.ReminderStateMachineTests;

namespace CalendarBell.Core.Tests;

public sealed class ReminderSchedulerTests : IAsyncDisposable
{
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(T(10, 0));
    private readonly InMemoryReminderStore _store = new();
    private readonly RecordingSink _sink = new();
    private ReminderScheduler _scheduler;

    public ReminderSchedulerTests()
    {
        _scheduler = NewScheduler();
    }

    private ReminderScheduler NewScheduler()
    {
        var s = new ReminderScheduler(_store, _sink, _time, ReminderOptions.Default);
        s.UpdateCalendarState(CalendarA, "Calendar A", enabled: true, lastSuccessUtc: _time.GetUtcNow());
        s.UpdateCalendarState(CalendarB, "Calendar B", enabled: true, lastSuccessUtc: _time.GetUtcNow());
        return s;
    }

    private async Task StartAsync() => await _scheduler.StartAsync(Ct);

    private Task AdvanceTo(DateTimeOffset t) => _time.StepToAsync(t, () => _scheduler.IdleAsync());

    private async Task RestartAsync()
    {
        await _scheduler.DisposeAsync();
        _scheduler = NewScheduler();
        await _scheduler.StartAsync(Ct);
        await _scheduler.IdleAsync();
    }

    public async ValueTask DisposeAsync() => await _scheduler.DisposeAsync();

    private static EventOccurrence OccA(DateTimeOffset start, string key = "a1") => Occ(start, key, CalendarA);
    private static EventOccurrence OccB(DateTimeOffset start, string key = "b1", string availability = Availability.Tentative) => Occ(start, key, CalendarB, availability);

    [Fact]
    public async Task T01_meeting_at_1030_fires_once_at_1015()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);

        await AdvanceTo(T(10, 14, 58));
        Assert.Empty(_sink.AllFired);

        await AdvanceTo(T(10, 15));
        var fired = Assert.Single(_sink.AllFired);
        Assert.Equal("a1", fired.Record.OccurrenceKey);
        Assert.Equal("Calendar A", fired.CalendarName);
        Assert.Equal(ReminderKind.Initial, fired.Kind);

        await AdvanceTo(T(10, 29));
        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task T34_fires_within_two_seconds_of_due()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);

        await AdvanceTo(T(10, 15));

        var fired = Assert.Single(_sink.AllFired);
        Assert.InRange(fired.FiredAtUtc, T(10, 14, 58), T(10, 15, 2));
    }

    [Fact]
    public async Task T02_polling_at_1014_and_1016_does_not_move_or_repeat_the_1015_alert()
    {
        await StartAsync();
        _time.SetUtcNow(T(10, 14));
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);

        await AdvanceTo(T(10, 15));
        Assert.Single(_sink.AllFired);

        _time.SetUtcNow(T(10, 16));
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.IdleAsync();

        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task T03_T05_T06_snoozes_fire_at_absolute_targets()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));

        _time.SetUtcNow(T(10, 16));
        var result = await _scheduler.SnoozeAsync(["a1"], 10, Ct);
        Assert.Equal(SnoozeOutcome.Scheduled, Assert.Single(result).Outcome);

        await AdvanceTo(T(10, 19, 58));
        Assert.Single(_sink.AllFired);
        await AdvanceTo(T(10, 20));
        Assert.Equal(2, _sink.AllFired.Count);
        Assert.Equal(ReminderKind.Snooze, _sink.AllFired[1].Kind);

        _time.SetUtcNow(T(10, 22));
        await _scheduler.SnoozeAsync(["a1"], 5, Ct);
        await AdvanceTo(T(10, 25));
        Assert.Equal(3, _sink.AllFired.Count);

        _time.SetUtcNow(T(10, 28));
        await _scheduler.SnoozeAsync(["a1"], 0, Ct);
        await AdvanceTo(T(10, 30));
        Assert.Equal(4, _sink.AllFired.Count);
        Assert.Equal(T(10, 30), _sink.AllFired[3].FiredAtUtc);
    }

    [Fact]
    public async Task T04_snooze_to_past_target_is_rejected_and_never_alerts_instantly()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));

        _time.SetUtcNow(T(10, 22));
        var result = await _scheduler.SnoozeAsync(["a1"], 10, Ct);
        await _scheduler.IdleAsync();

        Assert.Equal(SnoozeOutcome.TargetNotInFuture, Assert.Single(result).Outcome);
        await AdvanceTo(T(10, 29));
        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task Snooze_from_toast_with_stale_target_uses_next_future_target()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));

        _time.SetUtcNow(T(10, 22));
        var result = await _scheduler.SnoozeAsync(["a1"], 10, SnoozeMode.NextFutureFallback, Ct);

        var r = Assert.Single(result);
        Assert.Equal(SnoozeOutcome.Scheduled, r.Outcome);
        Assert.Equal(T(10, 25), r.TargetUtc);
    }

    [Fact]
    public async Task T07_dismiss_survives_restart_and_refresh()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        _time.SetUtcNow(T(10, 16));
        await _scheduler.SnoozeAsync(["a1"], 5, Ct);
        await _scheduler.DismissAsync(["a1"], Ct);

        await RestartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 25));
        await AdvanceTo(T(10, 30));

        Assert.Single(_sink.AllFired);
        Assert.Equal(ReminderStatus.Dismissed, _scheduler.GetUpcoming(T(10, 0), T(11, 0)).Single().Status);
    }

    [Fact]
    public async Task Snooze_survives_restart()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        _time.SetUtcNow(T(10, 16));
        await _scheduler.SnoozeAsync(["a1"], 5, Ct);

        await RestartAsync();
        await AdvanceTo(T(10, 25));

        Assert.Equal(2, _sink.AllFired.Count);
    }

    [Fact]
    public async Task T08_closing_toast_without_action_never_repeats_initial_phase()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));

        for (var m = 17; m <= 29; m += 2)
        {
            _time.SetUtcNow(T(10, m));
            await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        }

        await RestartAsync();
        await AdvanceTo(T(10, 29, 30));
        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task T09_two_calendars_same_start_fire_in_one_batch_with_independent_state()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.ReconcileAsync(CalendarB, [OccB(T(10, 30))], Ct);

        await AdvanceTo(T(10, 15));

        var batch = Assert.Single(_sink.Enqueued);
        Assert.Equal(2, batch.Count);
        Assert.Equal(["Calendar A", "Calendar B"], batch.Select(d => d.CalendarName).Order());

        await _scheduler.DismissAsync(["a1"], Ct);
        var upcoming = _scheduler.GetUpcoming(T(10, 0), T(11, 0));
        Assert.Equal(ReminderStatus.Dismissed, upcoming.Single(u => u.Record.OccurrenceKey == "a1").Status);
        Assert.Equal(ReminderStatus.Notified, upcoming.Single(u => u.Record.OccurrenceKey == "b1").Status);
    }

    [Fact]
    public async Task T10_T11_group_actions_touch_only_group_members_each_with_own_start()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)), OccA(T(11, 0), "a2")], Ct);
        await _scheduler.ReconcileAsync(CalendarB, [OccB(T(10, 30, 40))], Ct);
        await AdvanceTo(T(10, 15));

        _time.SetUtcNow(T(10, 16));
        var results = await _scheduler.SnoozeAsync(["a1", "b1"], 5, Ct);

        Assert.Equal(T(10, 25), results.Single(r => r.OccurrenceKey == "a1").TargetUtc);
        Assert.Equal(T(10, 25, 40), results.Single(r => r.OccurrenceKey == "b1").TargetUtc);
        var a2 = _scheduler.GetUpcoming(T(10, 0), T(12, 0)).Single(u => u.Record.OccurrenceKey == "a2");
        Assert.Equal(ReminderStatus.Pending, a2.Status);

        await _scheduler.DismissAsync(["a1", "b1"], Ct);
        var after = _scheduler.GetUpcoming(T(10, 0), T(12, 0));
        Assert.All(after.Where(u => u.Record.OccurrenceKey != "a2"), u => Assert.Equal(ReminderStatus.Dismissed, u.Status));
        Assert.Equal(ReminderStatus.Pending, after.Single(u => u.Record.OccurrenceKey == "a2").Status);
    }

    [Fact]
    public async Task T12_hundred_simultaneous_reminders_arrive_in_one_batch_without_loss()
    {
        await StartAsync();
        var many = Enumerable.Range(0, 100).Select(i => OccA(T(10, 30), "m" + i)).ToList();
        await _scheduler.ReconcileAsync(CalendarA, many, Ct);

        await AdvanceTo(T(10, 15));

        var batch = Assert.Single(_sink.Enqueued);
        Assert.Equal(100, batch.Select(d => d.Record.OccurrenceKey).Distinct().Count());
    }

    [Fact]
    public async Task T13_same_uid_in_two_calendars_is_two_independent_occurrences()
    {
        await StartAsync();
        var a = OccA(T(10, 30), "keyA") with { Uid = "shared-uid" };
        var b = OccB(T(10, 30), "keyB") with { Uid = "shared-uid" };
        await _scheduler.ReconcileAsync(CalendarA, [a], Ct);
        await _scheduler.ReconcileAsync(CalendarB, [b], Ct);
        await AdvanceTo(T(10, 15));
        await _scheduler.DismissAsync(["keyA"], Ct);

        var upcoming = _scheduler.GetUpcoming(T(10, 0), T(11, 0));
        Assert.Equal(2, upcoming.Count);
        Assert.Equal(ReminderStatus.Notified, upcoming.Single(u => u.Record.CalendarId == CalendarB).Status);
    }

    [Fact]
    public async Task Reconcile_ignores_events_of_another_calendar()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccB(T(10, 30))], Ct);

        Assert.Empty(_scheduler.GetUpcoming(T(10, 0), T(11, 0)));
    }

    [Fact]
    public async Task T14_identical_refresh_changes_nothing()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        var before = _store.Records["a1"];

        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);

        Assert.Equal(before.StateVersion, _store.Records["a1"].StateVersion);
        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task T15_start_moved_from_1030_to_1130_retracts_old_toast_and_reschedules()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        await _scheduler.RecordToastAsync(["a1"], "tag1", "reminders", Ct);

        _time.SetUtcNow(T(10, 18));
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(11, 30))], Ct);

        Assert.Contains(("tag1", "reminders"), _sink.Retracted);
        await AdvanceTo(T(10, 30));
        Assert.Single(_sink.AllFired);
        await AdvanceTo(T(11, 15));
        Assert.Equal(2, _sink.AllFired.Count);
        Assert.Equal(T(11, 30), _sink.AllFired[1].Occurrence.StartUtc);
    }

    [Fact]
    public async Task T16_removed_event_is_cancelled_and_never_fires()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)), OccA(T(10, 45), "a2")], Ct);

        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 45), "a2")], Ct);
        await AdvanceTo(T(10, 15));

        Assert.Empty(_sink.AllFired);
        Assert.Equal(ReminderStatus.Cancelled, _scheduler.GetUpcoming(T(10, 0), T(11, 0)).Single(u => u.Record.OccurrenceKey == "a1").Status);
    }

    [Fact]
    public async Task T16_explicitly_cancelled_occurrence_retracts_visible_toast()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        await _scheduler.RecordToastAsync(["a1"], "tag1", "reminders", Ct);

        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)) with { IsCancelled = true }], Ct);

        Assert.Contains(("tag1", "reminders"), _sink.Retracted);
    }

    [Fact]
    public async Task Empty_valid_feed_cancels_everything_in_window_only()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.ReconcileAsync(CalendarB, [OccB(T(10, 30))], Ct);

        await _scheduler.ReconcileAsync(CalendarA, [], Ct);
        await AdvanceTo(T(10, 15));

        Assert.Equal("Calendar B", Assert.Single(_sink.AllFired).CalendarName);
    }

    [Fact]
    public async Task T17_waking_at_T_minus_7_sends_late_alert_once()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.DisposeAsync(); // "asleep": no timers run

        _time.SetUtcNow(T(10, 23));
        _scheduler = NewScheduler();
        await _scheduler.StartAsync(Ct);
        await _scheduler.IdleAsync();

        var fired = Assert.Single(_sink.AllFired);
        Assert.Equal(T(10, 23), fired.FiredAtUtc);
        await AdvanceTo(T(10, 29));
        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task T18_waking_after_start_sends_nothing()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.DisposeAsync();

        _time.SetUtcNow(T(10, 40));
        _scheduler = NewScheduler();
        await _scheduler.StartAsync(Ct);
        await _scheduler.IdleAsync();

        Assert.Empty(_sink.AllFired);
        Assert.Equal(ReminderStatus.Expired, _scheduler.GetUpcoming(T(10, 0), T(11, 0)).Single().Status);
    }

    [Fact]
    public async Task Recalculate_after_clock_jump_fires_late_or_expires()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)), OccA(T(10, 20), "early")], Ct);

        _time.SetUtcNow(T(10, 22));
        await _scheduler.RecalculateAsync(Ct);
        await _scheduler.IdleAsync();

        var fired = Assert.Single(_sink.AllFired);
        Assert.Equal("a1", fired.Record.OccurrenceKey);
        Assert.Equal(ReminderStatus.Expired, _scheduler.GetUpcoming(T(10, 0), T(11, 0)).Single(u => u.Record.OccurrenceKey == "early").Status);
    }

    [Fact]
    public async Task T29_pause_suppresses_alerts_and_resume_does_not_replay()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)), OccA(T(11, 30), "later")], Ct);
        _scheduler.SetPausedUntil(T(11, 0));

        await AdvanceTo(T(10, 15));
        await AdvanceTo(T(11, 1));
        Assert.Empty(_sink.AllFired);

        await AdvanceTo(T(11, 15));
        Assert.Equal("later", Assert.Single(_sink.AllFired).Record.OccurrenceKey);
    }

    [Fact]
    public async Task T30_renaming_calendar_keeps_state_and_uses_new_name()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)), OccA(T(11, 0), "a2")], Ct);
        await AdvanceTo(T(10, 15));

        _scheduler.UpdateCalendarState(CalendarA, "Team Calendar", enabled: true, lastSuccessUtc: T(10, 15));
        await AdvanceTo(T(10, 45));

        Assert.Equal(2, _sink.AllFired.Count);
        Assert.Equal("Team Calendar", _sink.AllFired[1].CalendarName);
    }

    [Fact]
    public async Task T31_url_change_invalidates_only_that_calendar()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.ReconcileAsync(CalendarB, [OccB(T(10, 30))], Ct);

        await _scheduler.RemoveCalendarDataAsync(CalendarA, Ct);
        await AdvanceTo(T(10, 15));

        Assert.Equal("Calendar B", Assert.Single(_sink.AllFired).CalendarName);
        Assert.DoesNotContain(_store.Records.Values, r => r.CalendarId == CalendarA);
    }

    [Fact]
    public async Task Disabled_calendar_does_not_alert_and_its_toasts_are_retracted()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30)), OccA(T(11, 0), "a2")], Ct);
        await AdvanceTo(T(10, 15));
        await _scheduler.RecordToastAsync(["a1"], "tag1", "reminders", Ct);

        _scheduler.UpdateCalendarState(CalendarA, "Calendar A", enabled: false, lastSuccessUtc: T(10, 15));
        await AdvanceTo(T(10, 45));

        Assert.Single(_sink.AllFired);
        Assert.Contains(("tag1", "reminders"), _sink.Retracted);
    }

    [Fact]
    public async Task T36_stale_calendar_is_held_until_it_syncs_again()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        _scheduler.UpdateCalendarState(CalendarA, "Calendar A", enabled: true, lastSuccessUtc: T(10, 0) - TimeSpan.FromHours(25));

        await AdvanceTo(T(10, 15));
        Assert.Empty(_sink.AllFired);

        _time.SetUtcNow(T(10, 20));
        _scheduler.UpdateCalendarState(CalendarA, "Calendar A", enabled: true, lastSuccessUtc: T(10, 20));
        await _scheduler.IdleAsync();

        Assert.Equal(T(10, 20), Assert.Single(_sink.AllFired).FiredAtUtc);
    }

    [Fact]
    public async Task Dismiss_of_one_group_member_keeps_toast_while_another_still_awaits()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await _scheduler.ReconcileAsync(CalendarB, [OccB(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        await _scheduler.RecordToastAsync(["a1", "b1"], "g", "reminders", Ct);

        await _scheduler.DismissAsync(["a1"], Ct);
        Assert.Empty(_sink.Retracted);

        await _scheduler.DismissAsync(["b1"], Ct);
        Assert.Contains(("g", "reminders"), _sink.Retracted);
    }

    [Fact]
    public async Task IsCurrent_detects_state_changed_after_firing()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        var fired = Assert.Single(_sink.AllFired);
        Assert.True(_scheduler.IsCurrent(fired.Record.OccurrenceKey, fired.Record.StateVersion));

        await _scheduler.DismissAsync(["a1"], Ct);

        Assert.False(_scheduler.IsCurrent(fired.Record.OccurrenceKey, fired.Record.StateVersion));
    }

    [Fact]
    public async Task Storage_failure_still_notifies_and_reports_error()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        _store.FailSaves = true;

        await AdvanceTo(T(10, 15));

        Assert.Single(_sink.AllFired);
        Assert.True(_scheduler.HasStorageError);
        await AdvanceTo(T(10, 29));
        Assert.Single(_sink.AllFired);
    }

    [Fact]
    public async Task Snooze_reports_storage_failure_instead_of_claiming_success()
    {
        await StartAsync();
        await _scheduler.ReconcileAsync(CalendarA, [OccA(T(10, 30))], Ct);
        await AdvanceTo(T(10, 15));
        _store.FailSaves = true;

        _time.SetUtcNow(T(10, 16));
        await Assert.ThrowsAsync<IOException>(() => _scheduler.SnoozeAsync(["a1"], 5, Ct));
    }

    [Fact]
    public async Task Old_records_are_pruned_after_30_days()
    {
        _store.Records["old"] = ReminderRecord.CreateFor(OccA(T(10, 30) - TimeSpan.FromDays(31), "old"), ReminderOptions.Default);

        await StartAsync();

        Assert.False(_store.Records.ContainsKey("old"));
    }
}
