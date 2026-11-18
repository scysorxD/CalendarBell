using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Resources;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Ics;
using CalendarBell.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarBell.Infrastructure.Sync;

/// <summary>
/// Polls every enabled calendar every 2 minutes (spec §5.1, §6.1): at most 3 downloads at once, one in-flight
/// refresh per calendar ("Check now" joins it), exponential backoff with jitter, Retry-After, and a valid cache
/// that survives failures and invalid content. Successful parses are reconciled into the reminder scheduler.
/// </summary>
public sealed class CalendarSyncService : IAsyncDisposable
{
    private readonly ICalendarRepository _repository;
    private readonly ISyncStateStore _syncStore;
    private readonly ICalendarFeedClient _client;
    private readonly ICalendarParser _parser;
    private readonly IReminderScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly SyncOptions _options;
    private readonly ReminderOptions _reminderOptions;
    private readonly Func<bool> _privacyMode;
    private readonly Func<double> _random;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<Guid, CalendarSource> _calendars = new();
    private readonly ConcurrentDictionary<Guid, SyncRecord> _records = new();
    private readonly ConcurrentDictionary<Guid, CalendarSyncStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, int> _generations = new();
    private readonly Dictionary<Guid, Task<SyncOutcome>> _inFlight = [];
    private readonly object _inFlightLock = new();
    private readonly SemaphoreSlim _downloadSlots;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public CalendarSyncService(
        ICalendarRepository repository,
        ISyncStateStore syncStore,
        ICalendarFeedClient client,
        ICalendarParser parser,
        IReminderScheduler scheduler,
        TimeProvider time,
        SyncOptions options,
        ReminderOptions reminderOptions,
        Func<bool> privacyMode,
        ILogger<CalendarSyncService>? logger = null,
        Func<double>? random = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _syncStore = syncStore ?? throw new ArgumentNullException(nameof(syncStore));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reminderOptions = reminderOptions ?? throw new ArgumentNullException(nameof(reminderOptions));
        _privacyMode = privacyMode ?? throw new ArgumentNullException(nameof(privacyMode));
        _random = random ?? Random.Shared.NextDouble;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _downloadSlots = new SemaphoreSlim(options.MaxConcurrentDownloads, options.MaxConcurrentDownloads);
    }

    public event EventHandler? StatusChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        _loop ??= Task.Run(() => RunLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    public IReadOnlyList<CalendarSyncStatus> GetStatuses() =>
        _statuses.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Refreshes one calendar now. Concurrent callers share the in-flight operation.</summary>
    public async Task<SyncOutcome> RefreshAsync(Guid calendarId, CancellationToken ct)
    {
        if (_calendars.IsEmpty && _statuses.IsEmpty)
        {
            await LoadAsync(ct).ConfigureAwait(false);
        }

        return await StartRefresh(calendarId, jitter: false).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>"Check now": refreshes all enabled calendars without duplicating in-flight work.</summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        var tasks = _calendars.Values.Where(c => c.Enabled).Select(c => StartRefresh(c.CalendarId, jitter: false)).ToList();
        await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task CalendarAddedAsync(Guid calendarId, CancellationToken ct)
    {
        var calendar = await _repository.GetAsync(calendarId, ct).ConfigureAwait(false);
        if (calendar is null)
        {
            return;
        }

        _calendars[calendarId] = calendar;
        _records.TryAdd(calendarId, new SyncRecord(calendarId));
        _scheduler.UpdateCalendarState(calendarId, calendar.Name, calendar.Enabled, null);
        SetStatus(calendarId, s => s with { Name = calendar.Name, Enabled = calendar.Enabled, State = calendar.Enabled ? CalendarSyncState.NeverSynced : CalendarSyncState.Disabled });
        if (calendar.Enabled)
        {
            _ = StartRefresh(calendarId, jitter: false);
        }
    }

    public async Task CalendarUpdatedAsync(Guid calendarId, bool urlChanged, CancellationToken ct)
    {
        var calendar = await _repository.GetAsync(calendarId, ct).ConfigureAwait(false);
        if (calendar is null)
        {
            return;
        }

        var wasEnabled = _calendars.TryGetValue(calendarId, out var previous) && previous.Enabled;
        _calendars[calendarId] = calendar;
        if (urlChanged)
        {
            // A different URL is a different feed: drop this calendar's cache, validators and reminder state.
            _generations.AddOrUpdate(calendarId, 1, (_, g) => g + 1);
            await _scheduler.RemoveCalendarDataAsync(calendarId, ct).ConfigureAwait(false);
            await _syncStore.DeleteAsync(calendarId, ct).ConfigureAwait(false);
            _records[calendarId] = new SyncRecord(calendarId);
            SetStatus(calendarId, s => s with { State = CalendarSyncState.NeverSynced, LastSuccessUtc = null, LastAttemptUtc = null, HttpStatus = null, Message = null, UpcomingCount = 0, IsStale = false });
        }

        var lastSuccess = _records.TryGetValue(calendarId, out var record) ? record.LastSuccessUtc : null;
        _scheduler.UpdateCalendarState(calendarId, calendar.Name, calendar.Enabled, lastSuccess);
        SetStatus(calendarId, s => s with
        {
            Name = calendar.Name,
            Enabled = calendar.Enabled,
            State = calendar.Enabled ? (s.State == CalendarSyncState.Disabled ? CalendarSyncState.NeverSynced : s.State) : CalendarSyncState.Disabled,
        });

        if (calendar.Enabled && (urlChanged || !wasEnabled))
        {
            _ = StartRefresh(calendarId, jitter: false);
        }

        Wake();
    }

    public async Task CalendarRemovedAsync(Guid calendarId, CancellationToken ct)
    {
        _generations.AddOrUpdate(calendarId, 1, (_, g) => g + 1);
        _calendars.TryRemove(calendarId, out _);
        _records.TryRemove(calendarId, out _);
        _statuses.TryRemove(calendarId, out _);
        await _scheduler.RemoveCalendarDataAsync(calendarId, ct).ConfigureAwait(false);
        _scheduler.ForgetCalendar(calendarId);
        await _syncStore.DeleteAsync(calendarId, ct).ConfigureAwait(false);
        RaiseStatusChanged();
    }

    /// <summary>Privacy mode or the time zone changed: force full downloads (titles/floating times must be re-derived).</summary>
    public async Task PrivacyModeChangedAsync(CancellationToken ct) => await ForceFullRefreshAsync(ct).ConfigureAwait(false);

    public async Task SystemTimeChangedAsync(CancellationToken ct) => await ForceFullRefreshAsync(ct).ConfigureAwait(false);

    public async Task<TestConnectionResult> TestConnectionAsync(string url, CancellationToken ct)
    {
        var validation = UrlPolicy.Validate(url, _options.AllowHttpLocalhost);
        if (!validation.IsValid)
        {
            return new TestConnectionResult(false, validation.Error!, 0, null);
        }

        var result = await _client.FetchUrlAsync(validation.Uri!.AbsoluteUri, null, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (result.Status != FetchStatus.Ok)
        {
            _logger.LogInformation("Test connection failed: {Status} (HTTP {Http})", result.Status, result.HttpStatus);
            return new TestConnectionResult(false, MessageFor(result), 0, now);
        }

        try
        {
            var occurrences = _parser.Parse(result.Body!, Guid.Empty, now - _reminderOptions.ExpansionPast, now + _reminderOptions.ExpansionFuture);
            var upcoming = occurrences.Count(o => !o.IsCancelled && o.StartUtc >= now);
            _logger.LogInformation("Test connection succeeded in {Elapsed} ms ({Count} upcoming)", (long)result.Elapsed.TotalMilliseconds, upcoming);
            return new TestConnectionResult(true, Strings.Format("Test_Connected", upcoming), upcoming, now);
        }
        catch (IcsFormatException ex)
        {
            _logger.LogInformation("Test connection: invalid iCalendar content ({Reason})", LogSanitizer.Redact(ex.Message));
            return new TestConnectionResult(false, Strings.Get("Sync_InvalidIcs"), 0, now);
        }
    }

    /// <summary>Completes when every refresh started so far has finished.</summary>
    public async Task IdleAsync()
    {
        Task[] running;
        lock (_inFlightLock)
        {
            running = [.. _inFlight.Values];
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // individual refresh failures are already recorded in their status
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await IdleAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _downloadSlots.Dispose();
        _wake.Dispose();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var calendars = await _repository.GetAllAsync(ct).ConfigureAwait(false);
        var records = (await _syncStore.GetAllAsync(ct).ConfigureAwait(false)).ToDictionary(r => r.CalendarId);
        var now = _time.GetUtcNow();
        foreach (var calendar in calendars)
        {
            _calendars[calendar.CalendarId] = calendar;
            var record = records.TryGetValue(calendar.CalendarId, out var r) ? r : new SyncRecord(calendar.CalendarId);
            if (record.ConsecutiveFailures == 0 || record.NextRefreshUtc is null || record.NextRefreshUtc < now)
            {
                record = record with { NextRefreshUtc = now }; // refresh right after start-up
            }

            _records[calendar.CalendarId] = record;
            var state = !calendar.Enabled ? CalendarSyncState.Disabled
                : Enum.TryParse<CalendarSyncState>(record.LastState, out var s) && s != CalendarSyncState.Refreshing ? s
                : CalendarSyncState.NeverSynced;
            _statuses[calendar.CalendarId] = new CalendarSyncStatus(calendar.CalendarId, calendar.Name, calendar.Enabled, state,
                record.LastAttemptUtc, record.LastSuccessUtc, record.NextRefreshUtc, record.LastHttpStatus, record.LastError,
                record.UpcomingCount, IsStale(record.LastSuccessUtc, now));
            _scheduler.UpdateCalendarState(calendar.CalendarId, calendar.Name, calendar.Enabled, record.LastSuccessUtc);
        }

        RaiseStatusChanged();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            DateTimeOffset? next = null;
            foreach (var calendar in _calendars.Values.Where(c => c.Enabled))
            {
                bool running;
                lock (_inFlightLock)
                {
                    running = _inFlight.ContainsKey(calendar.CalendarId);
                }

                if (running)
                {
                    continue;
                }

                var due = _records.TryGetValue(calendar.CalendarId, out var record) ? record.NextRefreshUtc ?? now : now;
                if (due <= now)
                {
                    _ = StartRefresh(calendar.CalendarId, jitter: true);
                }
                else if (next is null || due < next)
                {
                    next = due;
                }
            }

            RefreshStaleness(now);
            var delay = next is null ? TimeSpan.FromMinutes(5) : next.Value - now;
            delay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay;
            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sleep = Task.Delay(delay, _time, delayCts.Token);
                var woken = _wake.WaitAsync(delayCts.Token);
                await Task.WhenAny(sleep, woken).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private Task<SyncOutcome> StartRefresh(Guid calendarId, bool jitter)
    {
        lock (_inFlightLock)
        {
            if (_inFlight.TryGetValue(calendarId, out var existing))
            {
                return existing;
            }

            var task = Task.Run(() => RefreshCoreAsync(calendarId, jitter, _lifetime.Token), CancellationToken.None);
            _inFlight[calendarId] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_inFlightLock)
                    {
                        _inFlight.Remove(calendarId);
                    }

                    Wake();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task<SyncOutcome> RefreshCoreAsync(Guid calendarId, bool jitter, CancellationToken ct)
    {
        if (!_calendars.TryGetValue(calendarId, out var calendar))
        {
            return new SyncOutcome(calendarId, CalendarSyncState.NeverSynced, null);
        }

        if (!calendar.Enabled)
        {
            SetStatus(calendarId, s => s with { State = CalendarSyncState.Disabled });
            return new SyncOutcome(calendarId, CalendarSyncState.Disabled, null);
        }

        var generation = _generations.GetValueOrDefault(calendarId);
        if (jitter && _options.MaxStartJitter > TimeSpan.Zero)
        {
            await Task.Delay(_options.MaxStartJitter * _random(), _time, ct).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        CalendarFetchResult result;
        var record = _records.GetOrAdd(calendarId, id => new SyncRecord(id));
        await _downloadSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetStatus(calendarId, s => s with { State = CalendarSyncState.Refreshing, LastAttemptUtc = _time.GetUtcNow() });
            var conditional = record.ETag is null && record.LastModified is null ? null : new ConditionalRequest(record.ETag, record.LastModified);
            result = await _client.FetchAsync(calendar, conditional, ct).ConfigureAwait(false);
        }
        finally
        {
            _downloadSlots.Release();
        }

        if (_generations.GetValueOrDefault(calendarId) != generation || !_calendars.ContainsKey(calendarId))
        {
            return new SyncOutcome(calendarId, CalendarSyncState.NeverSynced, null); // URL changed or calendar removed meanwhile
        }

        var now = _time.GetUtcNow();
        CalendarSyncState state;
        string? message = null;
        var upcoming = record.UpcomingCount;
        switch (result.Status)
        {
            case FetchStatus.NotModified:
                state = CalendarSyncState.Synced;
                record = record with { ETag = result.ETag ?? record.ETag, LastSuccessUtc = now, ConsecutiveFailures = 0, NextRefreshUtc = now + _options.PollInterval };
                break;

            case FetchStatus.Ok:
                var windowStart = now - _reminderOptions.ExpansionPast;
                var windowEnd = now + _reminderOptions.ExpansionFuture;
                IReadOnlyList<EventOccurrence>? occurrences = null;
                try
                {
                    occurrences = _parser.Parse(result.Body!, calendarId, windowStart, windowEnd);
                }
                catch (IcsFormatException ex)
                {
                    _logger.LogWarning("Calendar {CalendarId} ({Name}): invalid iCalendar content ({Reason}); keeping the last valid data", calendarId, calendar.Name, LogSanitizer.Redact(ex.Message));
                }

                if (occurrences is null)
                {
                    state = CalendarSyncState.InvalidCalendar;
                    message = Strings.Get("Sync_InvalidIcs");
                    record = Failed(record, now, null) with { ETag = null, LastModified = null };
                    break;
                }

                if (_privacyMode())
                {
                    occurrences = occurrences.Select(o => o.PrivacySafeTitle is null ? o : o with { PrivacySafeTitle = null }).ToList();
                }

                await _scheduler.ReconcileAsync(calendarId, occurrences, windowStart, windowEnd, ct).ConfigureAwait(false);
                upcoming = occurrences.Count(o => !o.IsCancelled && o.StartUtc >= now);
                state = CalendarSyncState.Synced;
                record = record with
                {
                    ETag = result.ETag,
                    LastModified = result.LastModified,
                    LastSuccessUtc = now,
                    ConsecutiveFailures = 0,
                    NextRefreshUtc = now + _options.PollInterval,
                };
                break;

            case FetchStatus.InvalidUrl:
            case FetchStatus.UrlUnreadable:
                state = CalendarSyncState.InvalidCalendar;
                message = MessageFor(result);
                record = Failed(record, now, result.RetryAfter);
                break;

            default:
                state = CalendarSyncState.NetworkError;
                message = MessageFor(result);
                record = Failed(record, now, result.RetryAfter);
                break;
        }

        record = record with
        {
            LastAttemptUtc = now,
            LastState = state.ToString(),
            LastError = message,
            LastHttpStatus = result.HttpStatus,
            UpcomingCount = upcoming,
        };
        _records[calendarId] = record;
        try
        {
            await _syncStore.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Saving sync state for calendar {CalendarId} failed: {Error}", calendarId, LogSanitizer.Describe(ex));
        }

        _scheduler.UpdateCalendarState(calendarId, calendar.Name, calendar.Enabled, record.LastSuccessUtc);
        SetStatus(calendarId, s => s with
        {
            Name = calendar.Name,
            Enabled = calendar.Enabled,
            State = state,
            LastAttemptUtc = now,
            LastSuccessUtc = record.LastSuccessUtc,
            NextRefreshUtc = record.NextRefreshUtc,
            HttpStatus = result.HttpStatus,
            Message = message,
            UpcomingCount = upcoming,
            IsStale = IsStale(record.LastSuccessUtc, now),
        });

        var level = state == CalendarSyncState.Synced ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(level, "Calendar {CalendarId} ({Name}) sync: {State} fetch={Fetch} HTTP={Http} in {Elapsed} ms{Detail}",
            calendarId, calendar.Name, state, result.Status, result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-",
            stopwatch.ElapsedMilliseconds, result.ErrorDetail is null ? string.Empty : " (" + LogSanitizer.Redact(result.ErrorDetail) + ")");
        return new SyncOutcome(calendarId, state, message);
    }

    private SyncRecord Failed(SyncRecord record, DateTimeOffset now, TimeSpan? retryAfter)
    {
        var failures = record.ConsecutiveFailures + 1;
        var baseDelay = _options.Backoff[Math.Min(failures, _options.Backoff.Count) - 1];
        var factor = 1 + (((_random() * 2) - 1) * _options.BackoffJitter);
        var delay = TimeSpan.FromTicks((long)(baseDelay.Ticks * factor));
        if (retryAfter is { } wait)
        {
            wait = wait > _options.MaxRetryAfter ? _options.MaxRetryAfter : wait;
            delay = wait > delay ? wait : delay;
        }

        return record with { ConsecutiveFailures = failures, NextRefreshUtc = now + delay };
    }

    private async Task ForceFullRefreshAsync(CancellationToken ct)
    {
        foreach (var (id, record) in _records)
        {
            var cleared = record with { ETag = null, LastModified = null };
            _records[id] = cleared;
            try
            {
                await _syncStore.SaveAsync(cleared, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Clearing validators failed: {Error}", LogSanitizer.Describe(ex));
            }
        }

        await RefreshAllAsync(ct).ConfigureAwait(false);
    }

    private void RefreshStaleness(DateTimeOffset now)
    {
        var changed = false;
        foreach (var (id, status) in _statuses)
        {
            var stale = IsStale(status.LastSuccessUtc, now);
            if (stale != status.IsStale)
            {
                _statuses[id] = status with { IsStale = stale };
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    private bool IsStale(DateTimeOffset? lastSuccessUtc, DateTimeOffset now) =>
        lastSuccessUtc is { } success && now - success > _reminderOptions.MaxCacheAge;

    private void SetStatus(Guid calendarId, Func<CalendarSyncStatus, CalendarSyncStatus> update)
    {
        _statuses.AddOrUpdate(
            calendarId,
            id => update(new CalendarSyncStatus(id, _calendars.TryGetValue(id, out var c) ? c.Name : string.Empty, c?.Enabled ?? false,
                CalendarSyncState.NeverSynced, null, null, null, null, null, 0, false)),
            (_, existing) => update(existing));
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("StatusChanged handler failed: {Error}", LogSanitizer.Describe(ex));
        }
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string MessageFor(CalendarFetchResult result) => result.Status switch
    {
        FetchStatus.HttpError when result.HttpStatus is 401 or 403 => Strings.Format("Sync_AccessDenied", result.HttpStatus),
        FetchStatus.HttpError when result.HttpStatus is 404 or 410 => Strings.Get("Sync_NotFound"),
        FetchStatus.HttpError => Strings.Format("Sync_ServerError", result.HttpStatus),
        FetchStatus.RateLimited => Strings.Get("Sync_RateLimited"),
        FetchStatus.Timeout => Strings.Get("Sync_Timeout"),
        FetchStatus.NetworkError => Strings.Format("Sync_Network", LogSanitizer.Redact(result.ErrorDetail)),
        FetchStatus.TooLarge => Strings.Format("Sync_TooLarge", _options.MaxResponseBytes / (1024 * 1024)),
        FetchStatus.InsecureRedirect => Strings.Get("Sync_InsecureRedirect"),
        FetchStatus.TooManyRedirects => Strings.Get("Sync_TooManyRedirects"),
        FetchStatus.UrlUnreadable => Strings.Get("Sync_UrlUnreadable"),
        FetchStatus.InvalidUrl => result.ErrorDetail ?? Strings.Get("Sync_InvalidUrl"),
        _ => Strings.Get("Sync_InvalidIcs"),
    };
}
