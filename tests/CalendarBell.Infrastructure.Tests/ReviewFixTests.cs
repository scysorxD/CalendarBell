using System.IO.Pipes;
using System.Net;
using System.Text;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Ics;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Sync;
using CalendarBell.Infrastructure.Tests.Fakes;
using CalendarBell.Infrastructure.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace CalendarBell.Infrastructure.Tests;

/// <summary>Regression tests for the final code review findings (each failed before its fix).</summary>
public sealed class ReviewFixTests : IAsyncDisposable
{
    private const string UrlA = "https://calendar-a.example/a.ics";
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHttpHandler _http = new();
    private readonly InMemoryCalendarRepository _calendars = new();
    private readonly InMemorySyncStateStore _syncStates = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CalendarBellTests", Guid.NewGuid().ToString("N"));
    private CalendarSyncService? _sync;

    public async ValueTask DisposeAsync()
    {
        if (_sync is not null)
        {
            await _sync.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // includes DirectoryNotFoundException (nothing was created)
        }
    }

    private CalendarSyncService Sync(ICalendarParser? parser = null)
    {
        _calendars.SaveAsync(new CalendarSource(CalendarA, "Calendar A", new FakeUrlProtector().Protect(UrlA), true), Ct).GetAwaiter().GetResult();
        var options = new SyncOptions { MaxStartJitter = TimeSpan.Zero };
        _sync = new CalendarSyncService(_calendars, _syncStates, new CalendarFeedClient(new FakeUrlProtector(), options, _time, _http),
            parser ?? new IcsCalendarParser(), _scheduler, _time, options, ReminderOptions.Default, () => true, random: () => 0.5);
        return _sync;
    }

    private static HttpResponseMessage Feed(string? etag = "\"v1\"") => FakeHttpHandler.Ics(IcsCalendarParserTests.Fixture("calendar-a-basic.ics"), etag);

    // Finding 2: a feed that always answers 304 must still be re-expanded so the 30-day window keeps moving.
    [Fact]
    public async Task Validators_are_dropped_once_the_last_full_parse_is_older_than_12_hours()
    {
        var sync = Sync();
        _http.Route(UrlA, (req, _) => Task.FromResult(req.Headers.IfNoneMatch.Count > 0 ? new HttpResponseMessage(HttpStatusCode.NotModified) : Feed()));
        await sync.RefreshAsync(CalendarA, Ct);
        _time.Advance(TimeSpan.FromHours(6));
        await sync.RefreshAsync(CalendarA, Ct);
        Assert.Equal(1, _scheduler.ReconcileCalls);

        _time.Advance(TimeSpan.FromHours(7));
        await sync.RefreshAsync(CalendarA, Ct);

        Assert.Equal(2, _scheduler.ReconcileCalls);
        Assert.Empty(_http.Requests.Last().Headers.IfNoneMatch);
    }

    // Finding 3: a refresh in flight must not undo a disable / rename / removal made meanwhile.
    [Fact]
    public async Task Disabling_during_an_in_flight_refresh_wins()
    {
        var sync = Sync();
        var gate = new TaskCompletionSource();
        _http.Route(UrlA, async (_, _) =>
        {
            await gate.Task;
            return Feed();
        });
        await sync.StartAsync(Ct);
        var refresh = sync.RefreshAsync(CalendarA, Ct);
        await CalendarFeedClientTests.WaitUntil(() => _http.RequestCount >= 1);

        await _calendars.SaveAsync((await _calendars.GetAsync(CalendarA, Ct))! with { Enabled = false }, Ct);
        await sync.CalendarUpdatedAsync(CalendarA, urlChanged: false, Ct);
        gate.SetResult();
        await refresh;
        await sync.IdleAsync();

        Assert.False(_scheduler.States[CalendarA].Enabled);
        Assert.Equal(CalendarSyncState.Disabled, sync.GetStatuses().Single().State);
    }

    [Fact]
    public async Task Renaming_during_an_in_flight_refresh_keeps_the_new_name()
    {
        var sync = Sync();
        var gate = new TaskCompletionSource();
        _http.Route(UrlA, async (_, _) =>
        {
            await gate.Task;
            return Feed();
        });
        await sync.StartAsync(Ct);
        var refresh = sync.RefreshAsync(CalendarA, Ct);
        await CalendarFeedClientTests.WaitUntil(() => _http.RequestCount >= 1);

        await _calendars.SaveAsync((await _calendars.GetAsync(CalendarA, Ct))! with { Name = "Team Calendar" }, Ct);
        await sync.CalendarUpdatedAsync(CalendarA, urlChanged: false, Ct);
        gate.SetResult();
        await refresh;

        Assert.Equal("Team Calendar", _scheduler.States[CalendarA].Name);
        Assert.Equal("Team Calendar", sync.GetStatuses().Single().Name);
    }

    [Fact]
    public async Task Removing_during_an_in_flight_refresh_leaves_no_reminders_behind()
    {
        var sync = Sync();
        var gate = new TaskCompletionSource();
        _http.Route(UrlA, async (_, _) =>
        {
            await gate.Task;
            return Feed();
        });
        await sync.StartAsync(Ct);
        var refresh = sync.RefreshAsync(CalendarA, Ct);
        await CalendarFeedClientTests.WaitUntil(() => _http.RequestCount >= 1);

        await _calendars.DeleteAsync(CalendarA, Ct);
        await sync.CalendarRemovedAsync(CalendarA, Ct);
        gate.SetResult();
        await refresh;

        Assert.False(_scheduler.Reconciled.ContainsKey(CalendarA));
        Assert.False(_scheduler.States.ContainsKey(CalendarA));
    }

    // Finding 5: any unexpected parser failure must end as "Invalid calendar" with backoff, never a tight retry loop.
    [Fact]
    public async Task Unexpected_parser_exception_becomes_invalid_calendar_with_backoff()
    {
        var sync = Sync(new ThrowingParser());
        _http.Respond(UrlA, () => Feed());

        var outcome = await sync.RefreshAsync(CalendarA, Ct);

        Assert.Equal(CalendarSyncState.InvalidCalendar, outcome.State);
        var status = sync.GetStatuses().Single();
        Assert.Equal(CalendarSyncState.InvalidCalendar, status.State);
        Assert.True(status.NextRefreshUtc >= _time.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    // Finding 9: removing a calendar must delete its encrypted URL from every backup copy.
    [Fact]
    public async Task Removing_a_calendar_removes_its_ciphertext_from_backups()
    {
        var paths = new DataPaths(_root);
        using var db = CalendarBellDatabase.Open(paths);
        var repo = new SqliteCalendarRepository(db, paths);
        await repo.SaveAsync(new CalendarSource(CalendarA, "Calendar A", "CIPHERTEXT-OF-A", true), Ct);
        await repo.SaveAsync(new CalendarSource(Guid.NewGuid(), "Calendar B", "CIPHERTEXT-OF-B", true), Ct);

        await repo.DeleteAsync(CalendarA, Ct);

        foreach (var file in Directory.EnumerateFiles(_root, "calendars.backup.json*"))
        {
            Assert.DoesNotContain("CIPHERTEXT-OF-A", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        }
    }

    // Finding 11: if the pipe cannot be created (name taken), the listener must back off instead of spinning.
    [Fact]
    public async Task Pipe_listener_backs_off_when_its_name_is_taken()
    {
        using var instance = SingleInstance.Create("test-" + Guid.NewGuid().ToString("N"));
        using var squatter = new NamedPipeServerStream(instance.PipeName, PipeDirection.In, 1);

        instance.StartListening();
        await Task.Delay(1500, Ct);

        Assert.InRange(instance.ListenFailures, 1, 10);
    }

    // Finding 12: a "(UTC±hh:mm)" fallback from one feed must not shadow another feed's VTIMEZONE (DST).
    [Fact]
    public void Offset_fallback_from_one_feed_does_not_shadow_another_feeds_vtimezone_in_summer()
    {
        const string tzid = "(UTC+01:00) Contoso Standard Time";
        var parser = new IcsCalendarParser();
        var window = (Start: new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero), End: new DateTimeOffset(2027, 7, 31, 0, 0, 0, TimeSpan.Zero));
        var withoutVtimezone = Calendar($"BEGIN:VEVENT\r\nUID:x1@calendar-b.example\r\nDTSTART;TZID=\"{tzid}\":20270715T103000\r\nDTEND;TZID=\"{tzid}\":20270715T110000\r\nEND:VEVENT\r\n");
        var withVtimezone = Calendar(EuVtimezone(tzid) + $"BEGIN:VEVENT\r\nUID:x2@calendar-b.example\r\nDTSTART;TZID=\"{tzid}\":20270715T103000\r\nDTEND;TZID=\"{tzid}\":20270715T110000\r\nEND:VEVENT\r\n");

        parser.Parse(withoutVtimezone, CalendarA, window.Start, window.End);
        var summer = Assert.Single(parser.Parse(withVtimezone, CalendarA, window.Start, window.End));

        Assert.Equal(new DateTimeOffset(2027, 7, 15, 8, 30, 0, TimeSpan.Zero), summer.StartUtc); // 10:30 CEST = 08:30 UTC
    }

    [Fact]
    public void Outlook_customized_time_zone_applies_daylight_saving_in_summer()
    {
        var ics = IcsCalendarParserTests.Fixture("calendar-b-outlook-tz.ics")
            .Replace("DTSTART;TZID=Customized Time Zone:20261119T103000", "DTSTART;TZID=Customized Time Zone:20270715T103000", StringComparison.Ordinal)
            .Replace("DTEND;TZID=Customized Time Zone:20261119T110000", "DTEND;TZID=Customized Time Zone:20270715T110000", StringComparison.Ordinal);

        var all = new IcsCalendarParser().Parse(ics, CalendarA, new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 7, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2027, 7, 15, 8, 30, 0, TimeSpan.Zero), all.Single(o => o.Uid == "b-custom-201@calendar-b.example").StartUtc);
    }

    private static string Calendar(string body) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CalendarBell//Test//EN\r\n" + body + "END:VCALENDAR\r\n";

    private static string EuVtimezone(string tzid) => new StringBuilder()
        .Append("BEGIN:VTIMEZONE\r\nTZID:").Append(tzid.Replace(",", "\\,", StringComparison.Ordinal)).Append("\r\n")
        .Append("BEGIN:STANDARD\r\nDTSTART:16010101T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nRRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=10\r\nEND:STANDARD\r\n")
        .Append("BEGIN:DAYLIGHT\r\nDTSTART:16010101T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nRRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=3\r\nEND:DAYLIGHT\r\n")
        .Append("END:VTIMEZONE\r\n").ToString();

    private sealed class ThrowingParser : ICalendarParser
    {
        public IReadOnlyList<EventOccurrence> Parse(string ics, Guid calendarId, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc) =>
            throw new NullReferenceException("simulated library bug");
    }
}
