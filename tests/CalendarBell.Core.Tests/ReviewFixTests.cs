using System.Globalization;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Notifications;
using CalendarBell.Core.Services;
using CalendarBell.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using static CalendarBell.Core.Tests.ReminderStateMachineTests;

namespace CalendarBell.Core.Tests;

/// <summary>Regression tests for the final code review findings (each failed before its fix).</summary>
public sealed class ReviewFixTests : IAsyncDisposable
{
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(T(10, 0));
    private readonly InMemoryReminderStore _store = new();
    private readonly RecordingSink _sink = new();
    private readonly List<IAsyncDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }
    }

    private async Task<ReminderScheduler> StartedSchedulerAsync(bool registerCalendars = true, IReminderSink? sink = null, TimeProvider? time = null)
    {
        var s = new ReminderScheduler(_store, sink ?? _sink, time ?? _time, ReminderOptions.Default);
        _disposables.Add(s);
        if (registerCalendars)
        {
            s.UpdateCalendarState(CalendarA, "Calendar A", true, _time.GetUtcNow());
            s.UpdateCalendarState(CalendarB, "Calendar B", true, _time.GetUtcNow());
        }

        await s.StartAsync(Ct);
        await s.IdleAsync();
        return s;
    }

    private static ReminderRecord NotifiedRecord(EventOccurrence o, string tag, DateTimeOffset at) =>
        ReminderStateMachine.ApplyFired(ReminderRecord.CreateFor(o, ReminderOptions.Default), at) with { LastToastTag = tag, LastToastGroup = "reminders" };

    // Finding 1: start-up toast reconciliation ran before calendars were registered and removed every pending toast.
    [Fact]
    public async Task Awaiting_toast_tags_are_known_right_after_start_even_before_calendars_register()
    {
        var pending = Occ(T(10, 30), "pending", CalendarA);
        var dismissed = Occ(T(10, 30), "dismissed", CalendarB);
        var expired = Occ(T(9, 30), "expired", CalendarA);
        _store.Records["pending"] = NotifiedRecord(pending, "tag-pending", T(10, 15));
        _store.Records["dismissed"] = ReminderStateMachine.ApplyDismiss(NotifiedRecord(dismissed, "tag-dismissed", T(10, 15)));
        _store.Records["expired"] = NotifiedRecord(expired, "tag-expired", T(9, 15));
        _time.SetUtcNow(T(10, 16));

        var scheduler = await StartedSchedulerAsync(registerCalendars: false);

        Assert.Equal(["tag-pending"], scheduler.AwaitingToastTags());
    }

    // Finding 3: a refresh that completes after a calendar was removed must not resurrect its reminders.
    [Fact]
    public async Task Reconcile_for_a_forgotten_calendar_is_ignored()
    {
        var scheduler = await StartedSchedulerAsync();
        scheduler.ForgetCalendar(CalendarA);

        await scheduler.ReconcileAsync(CalendarA, [Occ(T(10, 30), "a1", CalendarA)], Ct);

        Assert.DoesNotContain(_store.Records.Values, r => r.CalendarId == CalendarA);
    }

    // Finding 7: disabling or removing B must not take down a group toast that still waits for A.
    [Fact]
    public async Task Disabling_one_calendar_keeps_a_group_toast_that_another_calendar_still_needs()
    {
        var scheduler = await StartedSchedulerAsync();
        await scheduler.ReconcileAsync(CalendarA, [Occ(T(10, 30), "a1", CalendarA)], Ct);
        await scheduler.ReconcileAsync(CalendarB, [Occ(T(10, 30), "b1", CalendarB)], Ct);
        await _time.StepToAsync(T(10, 15), scheduler.IdleAsync);
        await scheduler.RecordToastAsync(["a1", "b1"], "group", "reminders", Ct);

        scheduler.UpdateCalendarState(CalendarB, "Calendar B", enabled: false, T(10, 15));
        await scheduler.IdleAsync();
        await scheduler.RemoveCalendarDataAsync(CalendarB, Ct);

        Assert.DoesNotContain(("group", "reminders"), _sink.Retracted);
    }

    [Fact]
    public async Task Disabling_the_only_calendar_of_a_toast_still_retracts_it()
    {
        var scheduler = await StartedSchedulerAsync();
        await scheduler.ReconcileAsync(CalendarB, [Occ(T(10, 30), "b1", CalendarB)], Ct);
        await _time.StepToAsync(T(10, 15), scheduler.IdleAsync);
        await scheduler.RecordToastAsync(["b1"], "solo", "reminders", Ct);

        scheduler.UpdateCalendarState(CalendarB, "Calendar B", enabled: false, T(10, 15));
        await scheduler.IdleAsync();

        Assert.Contains(("solo", "reminders"), _sink.Retracted);
    }

    // Finding 8: turning Privacy mode on must remove titles already stored.
    [Fact]
    public async Task Clearing_titles_removes_them_from_memory_and_storage()
    {
        var scheduler = await StartedSchedulerAsync();
        await scheduler.ReconcileAsync(CalendarA, [Occ(T(10, 30), "a1", CalendarA) with { PrivacySafeTitle = "Planning" }], Ct);

        await scheduler.ClearTitlesAsync(Ct);

        Assert.All(_store.Occurrences.Values, o => Assert.Null(o.PrivacySafeTitle));
        Assert.All(scheduler.GetUpcoming(T(9, 0), T(12, 0)), u => Assert.Null(u.Occurrence.PrivacySafeTitle));
    }

    // Finding 4: a failure to persist the toast batch must not suppress the toast itself.
    [Fact]
    public async Task Toast_is_shown_even_when_saving_its_batch_fails()
    {
        var presenter = new FakePresenter();
        var orchestrator = new NotificationOrchestrator(presenter, _store, _time, ReminderOptions.Default,
            () => new NotificationPreferences(true, true), new DisplayFormatter(TimeZoneInfo.Utc, CultureInfo.InvariantCulture));
        _disposables.Add(orchestrator);
        var scheduler = await StartedSchedulerAsync(sink: orchestrator);
        orchestrator.AttachScheduler(scheduler);
        await scheduler.ReconcileAsync(CalendarA, [Occ(T(10, 30), "a1", CalendarA)], Ct);
        _store.FailBatchSaves = true;

        await _time.StepToAsync(T(10, 15), async () =>
        {
            await scheduler.IdleAsync();
            await orchestrator.IdleAsync();
        });

        Assert.Single(presenter.Shown);
        Assert.False(orchestrator.LastShowFailed);
    }

    // Finding 6: the 8 s spacing must use a monotonic clock; a wall-clock jump backwards must not stall toasts.
    [Fact]
    public async Task Wall_clock_jumping_back_does_not_delay_the_next_toast()
    {
        var skewed = new SkewedTimeProvider(_time);
        var presenter = new FakePresenter();
        var orchestrator = new NotificationOrchestrator(presenter, _store, skewed, ReminderOptions.Default,
            () => new NotificationPreferences(true, true), new DisplayFormatter(TimeZoneInfo.Utc, CultureInfo.InvariantCulture));
        _disposables.Add(orchestrator);
        var scheduler = await StartedSchedulerAsync(sink: orchestrator);
        orchestrator.AttachScheduler(scheduler);
        await scheduler.ReconcileAsync(CalendarA, [Occ(T(10, 30), "a1", CalendarA), Occ(T(10, 31), "a2", CalendarA)], Ct);
        await _time.StepToAsync(T(10, 15), async () =>
        {
            await scheduler.IdleAsync();
            await orchestrator.IdleAsync();
        });
        Assert.Single(presenter.Shown);

        skewed.Skew = TimeSpan.FromHours(-1); // the user (or a bad NTP sync) moves the clock back one hour
        var a2 = scheduler.GetUpcoming(T(9, 0), T(12, 0)).Single(u => u.Record.OccurrenceKey == "a2");
        orchestrator.Enqueue([new DueReminder(a2.Record, a2.Occurrence, "Calendar A", ReminderKind.Initial, skewed.GetUtcNow())]);
        await _time.StepToAsync(T(10, 15, 10), orchestrator.IdleAsync);

        Assert.Equal(2, presenter.Shown.Count);
    }

    /// <summary>Wall clock = inner + Skew; timers and timestamps stay monotonic (like the real system clock).</summary>
    private sealed class SkewedTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        public TimeSpan Skew { get; set; }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Skew;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
}
