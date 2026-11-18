using System.Collections.Concurrent;
using System.Net;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using Microsoft.Extensions.Logging;

namespace CalendarBell.Infrastructure.Tests.Fakes;

internal sealed class FakeUrlProtector : IUrlProtector
{
    public string Protect(string url) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url));

    public string Unprotect(string protectedUrl) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedUrl["enc:".Length..]));
}

internal sealed class InMemoryCalendarRepository : ICalendarRepository
{
    private readonly ConcurrentDictionary<Guid, CalendarSource> _items = new();

    public Task<IReadOnlyList<CalendarSource>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarSource>>(_items.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<CalendarSource?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.TryGetValue(id, out var c) ? c : null);

    public Task SaveAsync(CalendarSource calendar, CancellationToken ct)
    {
        _items[calendar.CalendarId] = calendar;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

internal sealed class InMemorySyncStateStore : ISyncStateStore
{
    public ConcurrentDictionary<Guid, SyncRecord> Items { get; } = new();

    public Task<IReadOnlyList<SyncRecord>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncRecord>>(Items.Values.ToList());

    public Task SaveAsync(SyncRecord state, CancellationToken ct)
    {
        Items[state.CalendarId] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid calendarId, CancellationToken ct)
    {
        Items.TryRemove(calendarId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Records what the sync service hands to the scheduler.</summary>
internal sealed class FakeScheduler : IReminderScheduler
{
    public ConcurrentDictionary<Guid, IReadOnlyList<EventOccurrence>> Reconciled { get; } = new();
    public ConcurrentDictionary<Guid, (string Name, bool Enabled, DateTimeOffset? LastSuccess)> States { get; } = new();
    public ConcurrentBag<Guid> Removed { get; } = [];
    public int ReconcileCalls;

#pragma warning disable CS0067 // unused in tests
    public event EventHandler? Changed;
#pragma warning restore CS0067

    public bool HasStorageError => false;

    public int PendingCount => 0;

    public Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, CancellationToken ct) =>
        ReconcileAsync(calendarId, events, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

    public Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, CancellationToken ct)
    {
        Interlocked.Increment(ref ReconcileCalls);
        Reconciled[calendarId] = events;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SnoozeResult>> SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SnoozeResult>> SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, SnoozeMode mode, CancellationToken ct) => throw new NotSupportedException();

    public Task DismissAsync(IReadOnlyList<string> occurrenceKeys, CancellationToken ct) => throw new NotSupportedException();

    public Task RecordToastAsync(IReadOnlyList<string> occurrenceKeys, string tag, string group, CancellationToken ct) => Task.CompletedTask;

    public Task RemoveCalendarDataAsync(Guid calendarId, CancellationToken ct)
    {
        Removed.Add(calendarId);
        Reconciled.TryRemove(calendarId, out _);
        return Task.CompletedTask;
    }

    public Task RecalculateAsync(CancellationToken ct) => Task.CompletedTask;

    public void UpdateCalendarState(Guid calendarId, string name, bool enabled, DateTimeOffset? lastSuccessUtc) =>
        States[calendarId] = (name, enabled, lastSuccessUtc);

    public void ForgetCalendar(Guid calendarId) => States.TryRemove(calendarId, out _);

    public void SetPausedUntil(DateTimeOffset? pausedUntilUtc)
    {
    }

    public bool IsCurrent(string occurrenceKey, long stateVersion) => true;

    public IReadOnlyList<UpcomingReminder> GetUpcoming(DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];
}

/// <summary>Programmable HTTP endpoint: one responder per URL path, request log with headers.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public int RequestCount => Requests.Count;

    public void Route(string absoluteUrl, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        _routes[absoluteUrl] = responder;

    public void Respond(string absoluteUrl, Func<HttpResponseMessage> response) =>
        Route(absoluteUrl, (_, _) => Task.FromResult(response()));

    public static HttpResponseMessage Ics(string body, string? etag = null, string? lastModified = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/calendar") };
        if (etag is not null)
        {
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        if (lastModified is not null)
        {
            r.Content.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
        }

        return r;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var key = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var exact))
        {
            return exact(request, cancellationToken);
        }

        return _routes.TryGetValue(key, out var byPath)
            ? byPath(request, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public string All => string.Join("\n", Lines);

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
