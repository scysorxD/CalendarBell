using System.Net;
using CalendarBell.Core.Models;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Ics;
using CalendarBell.Infrastructure.Sync;
using CalendarBell.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CalendarBell.Infrastructure.Tests;

public sealed class CalendarSyncServiceTests : IAsyncDisposable
{
    private const string UrlA = "https://calendar-a.example/a.ics?token=SECRET-A";
    private const string UrlB = "https://calendar-b.example/b.ics?token=SECRET-B";
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHttpHandler _http = new();
    private readonly InMemoryCalendarRepository _calendars = new();
    private readonly InMemorySyncStateStore _syncStates = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CalendarSyncService _sync;
    private bool _privacy = true;

    public CalendarSyncServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        var protector = new FakeUrlProtector();
        _calendars.SaveAsync(new CalendarSource(CalendarA, "Calendar A", protector.Protect(UrlA), true), Ct).GetAwaiter().GetResult();
        _calendars.SaveAsync(new CalendarSource(CalendarB, "Calendar B", protector.Protect(UrlB), true), Ct).GetAwaiter().GetResult();
        var options = new SyncOptions { MaxStartJitter = TimeSpan.Zero };
        var client = new CalendarFeedClient(protector, options, _time, _http, _loggerFactory.CreateLogger<CalendarFeedClient>());
        _sync = new CalendarSyncService(_calendars, _syncStates, client, new IcsCalendarParser(), _scheduler, _time, options, ReminderOptions.Default,
            () => _privacy, _loggerFactory.CreateLogger<CalendarSyncService>(), random: () => 0.5);
        _http.Respond(UrlA, () => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("calendar-a-basic.ics"), "\"a1\""));
        _http.Respond(UrlB, () => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("calendar-b-outlook-tz.ics"), "\"b1\""));
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.DisposeAsync();
        _loggerFactory.Dispose();
    }

    private CalendarSyncStatus Status(Guid id) => _sync.GetStatuses().Single(s => s.CalendarId == id);

    [Fact]
    public async Task Successful_sync_reconciles_and_reports_synced()
    {
        var outcome = await _sync.RefreshAsync(CalendarA, Ct);

        Assert.Equal(CalendarSyncState.Synced, outcome.State);
        Assert.NotEmpty(_scheduler.Reconciled[CalendarA]);
        var status = Status(CalendarA);
        Assert.Equal(CalendarSyncState.Synced, status.State);
        Assert.Equal(_time.GetUtcNow(), status.LastSuccessUtc);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(2), status.NextRefreshUtc);
        Assert.True(status.UpcomingCount > 0);
        Assert.Equal(_time.GetUtcNow(), _scheduler.States[CalendarA].LastSuccess);
        Assert.Equal("\"a1\"", _syncStates.Items[CalendarA].ETag);
    }

    [Fact]
    public async Task Privacy_mode_strips_titles_before_they_reach_the_scheduler()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        Assert.All(_scheduler.Reconciled[CalendarA], o => Assert.Null(o.PrivacySafeTitle));

        _privacy = false;
        await _sync.PrivacyModeChangedAsync(Ct);
        await _sync.RefreshAsync(CalendarA, Ct);

        Assert.Contains(_scheduler.Reconciled[CalendarA], o => o.PrivacySafeTitle == "Planning");
    }

    [Fact]
    public async Task T14_not_modified_keeps_state_and_does_not_reconcile()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        _http.Route(UrlA, (req, _) => Task.FromResult(new HttpResponseMessage(
            req.Headers.IfNoneMatch.Any(t => t.Tag == "\"a1\"") ? HttpStatusCode.NotModified : HttpStatusCode.OK)));
        _time.Advance(TimeSpan.FromMinutes(2));

        var outcome = await _sync.RefreshAsync(CalendarA, Ct);

        Assert.Equal(CalendarSyncState.Synced, outcome.State);
        Assert.Equal(1, _scheduler.ReconcileCalls);
        Assert.Equal(_time.GetUtcNow(), Status(CalendarA).LastSuccessUtc);
    }

    [Fact]
    public async Task T20_unreachable_feed_keeps_cache_and_other_calendars_sync()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        var cached = _scheduler.Reconciled[CalendarA];
        _http.Respond(UrlA, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var a = await _sync.RefreshAsync(CalendarA, Ct);
        var b = await _sync.RefreshAsync(CalendarB, Ct);

        Assert.Equal(CalendarSyncState.NetworkError, a.State);
        Assert.Same(cached, _scheduler.Reconciled[CalendarA]);
        Assert.Equal(CalendarSyncState.Synced, b.State);
        Assert.Equal(500, Status(CalendarA).HttpStatus);
    }

    [Fact]
    public async Task T21_invalid_body_keeps_cache_and_reports_invalid_calendar()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        var cached = _scheduler.Reconciled[CalendarA];
        _http.Respond(UrlA, () => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("invalid-html.ics"), "\"a2\""));

        var outcome = await _sync.RefreshAsync(CalendarA, Ct);

        Assert.Equal(CalendarSyncState.InvalidCalendar, outcome.State);
        Assert.Same(cached, _scheduler.Reconciled[CalendarA]);
        Assert.Null(_syncStates.Items[CalendarA].ETag); // force a full download next time
    }

    [Fact]
    public async Task Valid_empty_feed_is_reconciled_as_empty()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        _http.Respond(UrlA, () => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("empty-valid.ics")));

        await _sync.RefreshAsync(CalendarA, Ct);

        Assert.Empty(_scheduler.Reconciled[CalendarA]);
    }

    [Fact]
    public async Task T22_concurrent_refreshes_of_one_calendar_share_one_request()
    {
        var gate = new TaskCompletionSource();
        _http.Route(UrlA, async (_, _) =>
        {
            await gate.Task;
            return FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("calendar-a-basic.ics"));
        });

        var refreshes = Enumerable.Range(0, 3).Select(_ => _sync.RefreshAsync(CalendarA, Ct)).ToList();
        await CalendarFeedClientTests.WaitUntil(() => _http.RequestCount >= 1);
        gate.SetResult();
        await Task.WhenAll(refreshes);

        Assert.Equal(1, _http.RequestCount);
        Assert.Equal(1, _scheduler.ReconcileCalls);
    }

    [Fact]
    public async Task Failures_back_off_2_4_8_16_30_minutes()
    {
        _http.Respond(UrlA, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            await _sync.RefreshAsync(CalendarA, Ct);
            delays.Add(Status(CalendarA).NextRefreshUtc!.Value - _time.GetUtcNow());
        }

        Assert.Equal([2, 4, 8, 16, 30, 30], delays.Select(d => (int)Math.Round(d.TotalMinutes)));
    }

    [Fact]
    public async Task Rate_limit_respects_retry_after()
    {
        _http.Respond(UrlA, () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return r;
        });

        await _sync.RefreshAsync(CalendarA, Ct);

        Assert.True(Status(CalendarA).NextRefreshUtc >= _time.GetUtcNow() + TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task T36_feed_without_success_for_24h_is_reported_stale()
    {
        _syncStates.Items[CalendarA] = new SyncRecord(CalendarA) { LastSuccessUtc = _time.GetUtcNow() - TimeSpan.FromHours(25) };
        _http.Respond(UrlA, () => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await _sync.StartAsync(Ct);

        await _sync.RefreshAsync(CalendarA, Ct);

        var status = Status(CalendarA);
        Assert.True(status.IsStale);
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromHours(25), _scheduler.States[CalendarA].LastSuccess);
    }

    [Fact]
    public async Task T27_logs_never_contain_urls_tokens_or_titles()
    {
        _privacy = false;
        await _sync.RefreshAsync(CalendarA, Ct);
        _http.Route(UrlA, (_, _) => throw new HttpRequestException("Connection refused (" + UrlA + ")"));
        await _sync.RefreshAsync(CalendarA, Ct);
        _http.Respond(UrlB, () => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await _sync.RefreshAsync(CalendarB, Ct);

        var all = _logs.All;
        Assert.NotEmpty(all);
        Assert.DoesNotContain("SECRET", all, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-a.example", all, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-b.example", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Planning", all, StringComparison.Ordinal);
        Assert.DoesNotContain("token", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disabled_calendar_is_not_synced()
    {
        var a = (await _calendars.GetAsync(CalendarA, Ct))!;
        await _calendars.SaveAsync(a with { Enabled = false }, Ct);
        await _sync.StartAsync(Ct);

        var outcome = await _sync.RefreshAsync(CalendarA, Ct);

        Assert.Equal(CalendarSyncState.Disabled, outcome.State);
        Assert.Equal(0, _http.Requests.Count(r => r.RequestUri!.AbsoluteUri == UrlA));
        Assert.False(_scheduler.States[CalendarA].Enabled);
    }

    [Fact]
    public async Task T31_url_change_clears_only_that_calendars_data_and_resyncs()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        await _sync.RefreshAsync(CalendarB, Ct);
        const string newUrl = "https://calendar-a.example/new.ics";
        _http.Respond(newUrl, () => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("calendar-a-recurring.ics")));
        var a = (await _calendars.GetAsync(CalendarA, Ct))!;
        await _calendars.SaveAsync(a with { EncryptedUrl = new FakeUrlProtector().Protect(newUrl) }, Ct);

        await _sync.CalendarUpdatedAsync(CalendarA, urlChanged: true, Ct);
        await CalendarFeedClientTests.WaitUntil(() => _http.Requests.Any(r => r.RequestUri!.AbsoluteUri == newUrl));
        await _sync.IdleAsync();

        Assert.Contains(CalendarA, _scheduler.Removed);
        Assert.DoesNotContain(CalendarB, _scheduler.Removed);
        Assert.NotNull(_scheduler.Reconciled[CalendarB]);
        Assert.Contains(_scheduler.Reconciled[CalendarA], o => o.Uid == "a-weekly-100@calendar-a.example");
    }

    [Fact]
    public async Task Rename_only_updates_scheduler_name()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        var a = (await _calendars.GetAsync(CalendarA, Ct))!;
        await _calendars.SaveAsync(a with { Name = "Team Calendar" }, Ct);

        await _sync.CalendarUpdatedAsync(CalendarA, urlChanged: false, Ct);

        Assert.Equal("Team Calendar", _scheduler.States[CalendarA].Name);
        Assert.DoesNotContain(CalendarA, _scheduler.Removed);
    }

    [Fact]
    public async Task Removing_a_calendar_forgets_its_data()
    {
        await _sync.RefreshAsync(CalendarA, Ct);
        await _calendars.DeleteAsync(CalendarA, Ct);

        await _sync.CalendarRemovedAsync(CalendarA, Ct);

        Assert.Contains(CalendarA, _scheduler.Removed);
        Assert.False(_syncStates.Items.ContainsKey(CalendarA));
        Assert.DoesNotContain(_sync.GetStatuses(), s => s.CalendarId == CalendarA);
    }

    [Fact]
    public async Task Background_loop_polls_every_two_minutes()
    {
        var t0 = _time.GetUtcNow();
        await _sync.StartAsync(Ct);
        await CalendarFeedClientTests.WaitUntil(() => _sync.GetStatuses().Any(s => s.CalendarId == CalendarA && s.State == CalendarSyncState.Synced && s.NextRefreshUtc == t0 + TimeSpan.FromMinutes(2)));
        Assert.Equal(1, _http.Requests.Count(r => r.RequestUri!.AbsoluteUri == UrlA));

        _time.Advance(TimeSpan.FromMinutes(2));

        await CalendarFeedClientTests.WaitUntil(() => _http.Requests.Count(r => r.RequestUri!.AbsoluteUri == UrlA) == 2);
    }

    [Fact]
    public async Task Test_connection_reports_count_without_persisting()
    {
        var result = await _sync.TestConnectionAsync(UrlA, Ct);

        Assert.True(result.Success);
        Assert.True(result.UpcomingCount > 0);
        Assert.Empty(_syncStates.Items);
        Assert.Equal(0, _scheduler.ReconcileCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Access denied")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task Test_connection_explains_failures_without_the_url(HttpStatusCode code, string expected)
    {
        _http.Respond(UrlA, () => new HttpResponseMessage(code));

        var result = await _sync.TestConnectionAsync(UrlA, Ct);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_rejects_invalid_ics()
    {
        _http.Respond(UrlA, () => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("invalid-html.ics")));

        var result = await _sync.TestConnectionAsync(UrlA, Ct);

        Assert.False(result.Success);
        Assert.Contains("iCalendar", result.Message, StringComparison.Ordinal);
    }
}
