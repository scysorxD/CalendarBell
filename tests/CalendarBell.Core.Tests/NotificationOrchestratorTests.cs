using System.Globalization;
using CalendarBell.Core.Models;
using CalendarBell.Core.Notifications;
using CalendarBell.Core.Services;
using CalendarBell.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using static CalendarBell.Core.Tests.ReminderStateMachineTests;

namespace CalendarBell.Core.Tests;

public sealed class NotificationOrchestratorTests : IAsyncDisposable
{
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(T(10, 0));
    private readonly InMemoryReminderStore _store = new();
    private readonly FakePresenter _presenter = new();
    private readonly NotificationOrchestrator _orchestrator;
    private readonly ReminderScheduler _scheduler;
    private NotificationPreferences _prefs = new(PlaySound: true, PrivacyMode: true);

    public NotificationOrchestratorTests()
    {
        _presenter.Clock = _time.GetUtcNow;
        _orchestrator = new NotificationOrchestrator(
            _presenter, _store, _time, ReminderOptions.Default, () => _prefs,
            new DisplayFormatter(TimeZoneInfo.Utc, CultureInfo.InvariantCulture));
        _scheduler = new ReminderScheduler(_store, _orchestrator, _time, ReminderOptions.Default);
        _orchestrator.AttachScheduler(_scheduler);
        _scheduler.UpdateCalendarState(CalendarA, "Calendar A", true, T(10, 0));
        _scheduler.UpdateCalendarState(CalendarB, "Calendar B", true, T(10, 0));
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        await _orchestrator.DisposeAsync();
    }

    private Task AdvanceTo(DateTimeOffset t) => _time.StepToAsync(t, async () =>
    {
        await _scheduler.IdleAsync();
        await _orchestrator.IdleAsync();
    });

    private async Task SetupAsync(params EventOccurrence[] occurrences)
    {
        await _scheduler.StartAsync(Ct);
        foreach (var group in occurrences.GroupBy(o => o.CalendarId))
        {
            await _scheduler.ReconcileAsync(group.Key, group.ToList(), Ct);
        }
    }

    private ReminderStatus StatusOf(string key) =>
        _scheduler.GetUpcoming(T(9, 0), T(13, 0)).Single(u => u.Record.OccurrenceKey == key).Status;

    [Fact]
    public async Task Due_reminder_is_shown_and_toast_tag_recorded()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA));

        await AdvanceTo(T(10, 15));

        var (batch, at) = Assert.Single(_presenter.Shown);
        Assert.Equal(T(10, 15), at);
        Assert.Equal(batch.BatchId, _store.Records["a1"].LastToastTag);
        Assert.Equal(NotificationBatcher.ReminderGroup, _store.Records["a1"].LastToastGroup);
        Assert.NotNull(await _store.GetBatchAsync(batch.BatchId, Ct));
    }

    [Fact]
    public async Task Rate_limit_allows_one_new_toast_per_eight_seconds_and_merges_queued_items()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA), Occ(T(10, 30, 3), "b1", CalendarB), Occ(T(10, 30, 5), "a2", CalendarA));

        await AdvanceTo(T(10, 15));
        await AdvanceTo(T(10, 15, 3));
        await AdvanceTo(T(10, 15, 5));
        Assert.Single(_presenter.Shown);

        await AdvanceTo(T(10, 15, 8));

        Assert.Equal(2, _presenter.Shown.Count);
        Assert.Equal(T(10, 15, 8), _presenter.Shown[1].At);
        Assert.Equal(["a2", "b1"], _presenter.Shown[1].Batch.Members.Select(m => m.Record.OccurrenceKey).Order());
    }

    [Fact]
    public async Task Queued_item_dismissed_before_its_slot_is_not_shown()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA), Occ(T(10, 30, 3), "b1", CalendarB));
        await AdvanceTo(T(10, 15));
        await AdvanceTo(T(10, 15, 3));

        await _scheduler.DismissAsync(["b1"], Ct);
        await AdvanceTo(T(10, 15, 10));

        Assert.Single(_presenter.Shown);
    }

    [Fact]
    public async Task T11_group_snooze_from_toast_moves_each_member_to_its_own_target()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA), Occ(T(10, 30, 0.8), "b1", CalendarB), Occ(T(11, 0), "a2", CalendarA));
        await AdvanceTo(T(10, 15));
        var batch = Assert.Single(_presenter.Shown).Batch;

        _time.SetUtcNow(T(10, 16));
        var result = await _orchestrator.HandleActionAsync(
            ToastArguments.Parse($"action=snooze&b={batch.BatchId}", new Dictionary<string, string> { ["snoozeTarget"] = "5" })!, Ct);

        Assert.True(result.Handled);
        var upcoming = _scheduler.GetUpcoming(T(9, 0), T(13, 0));
        Assert.Equal(T(10, 25), upcoming.Single(u => u.Record.OccurrenceKey == "a1").Record.SnoozedUntilUtc);
        Assert.Equal(T(10, 25, 0.8), upcoming.Single(u => u.Record.OccurrenceKey == "b1").Record.SnoozedUntilUtc);
        Assert.Equal(ReminderStatus.Pending, StatusOf("a2"));
        Assert.Contains((batch.BatchId, NotificationBatcher.ReminderGroup), _presenter.Removed);
    }

    [Fact]
    public async Task T10_group_dismiss_from_toast_dismisses_only_members()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA), Occ(T(10, 30), "b1", CalendarB), Occ(T(11, 0), "a2", CalendarA));
        await AdvanceTo(T(10, 15));
        var batch = Assert.Single(_presenter.Shown).Batch;

        await _orchestrator.HandleActionAsync(ToastArguments.Parse($"action=dismiss&b={batch.BatchId}", new Dictionary<string, string>())!, Ct);

        Assert.Equal(ReminderStatus.Dismissed, StatusOf("a1"));
        Assert.Equal(ReminderStatus.Dismissed, StatusOf("b1"));
        Assert.Equal(ReminderStatus.Pending, StatusOf("a2"));
    }

    [Fact]
    public async Task Snoozed_reminder_fires_again_with_sound_and_new_text()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA));
        await AdvanceTo(T(10, 15));
        var batch = _presenter.Shown[0].Batch;
        _time.SetUtcNow(T(10, 16));
        await _orchestrator.HandleActionAsync(ToastArguments.Parse($"action=snooze&b={batch.BatchId}", new Dictionary<string, string> { ["snoozeTarget"] = "10" })!, Ct);

        await AdvanceTo(T(10, 20));

        Assert.Equal(2, _presenter.Shown.Count);
        Assert.Equal("Calendar A · Meeting in 10 min", _presenter.Shown[1].Batch.Title);
        Assert.True(_presenter.Shown[1].Batch.PlaySound);
    }

    [Fact]
    public async Task Body_click_requests_upcoming_window_filtered_to_batch()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA), Occ(T(10, 30), "b1", CalendarB));
        await AdvanceTo(T(10, 15));
        var batch = _presenter.Shown[0].Batch;
        IReadOnlyList<string>? requested = null;
        _orchestrator.OpenUpcomingRequested += (_, keys) => requested = keys;

        await _orchestrator.HandleActionAsync(ToastArguments.Parse($"action=open&b={batch.BatchId}", new Dictionary<string, string>())!, Ct);

        Assert.Equal(["a1", "b1"], requested!.Order());
        Assert.Equal(ReminderStatus.Notified, StatusOf("a1"));
    }

    [Fact]
    public async Task Test_notification_actions_never_touch_real_reminders()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA));
        await AdvanceTo(T(10, 15));
        var versionBefore = _store.Records["a1"].StateVersion;

        var test = await _orchestrator.ShowTestNotificationAsync(Ct);
        await _orchestrator.HandleActionAsync(ToastArguments.Parse($"action=test-dismiss&b={test.BatchId}", new Dictionary<string, string>())!, Ct);

        Assert.Equal(NotificationBatcher.TestGroup, test.Group);
        Assert.Equal(versionBefore, _store.Records["a1"].StateVersion);
        Assert.Equal(ReminderStatus.Notified, StatusOf("a1"));
    }

    [Fact]
    public async Task Unknown_batch_is_ignored_safely()
    {
        await SetupAsync();

        var result = await _orchestrator.HandleActionAsync(ToastArguments.Parse("action=dismiss&b=ffffffffffff", new Dictionary<string, string>())!, Ct);

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task Sound_preference_is_read_at_emission_time()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA));
        _prefs = _prefs with { PlaySound = false };

        await AdvanceTo(T(10, 15));

        Assert.False(_presenter.Shown[0].Batch.PlaySound);
    }

    [Fact]
    public async Task Presenter_failure_is_reported_not_thrown()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA));
        _presenter.Fail = true;

        await AdvanceTo(T(10, 15));

        Assert.True(_orchestrator.LastShowFailed);
    }

    [Fact]
    public async Task Retract_removes_toast_by_tag_and_group()
    {
        await SetupAsync(Occ(T(10, 30), "a1", CalendarA));
        await AdvanceTo(T(10, 15));
        var batch = _presenter.Shown[0].Batch;

        await _scheduler.ReconcileAsync(CalendarA, [], Ct);
        await _orchestrator.IdleAsync();

        Assert.Contains((batch.BatchId, NotificationBatcher.ReminderGroup), _presenter.Removed);
    }
}
