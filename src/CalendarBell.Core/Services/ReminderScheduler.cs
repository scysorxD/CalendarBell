using System.Collections.Concurrent;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarBell.Core.Services;

/// <summary>
/// Keeps every occurrence's reminder state and fires due phases at their exact instant (spec §6.1).
/// Independent from ICS polling: one timer is armed for the earliest due instant; a 60 s drift check
/// re-evaluates everything to survive clock changes and sleep/resume. State is persisted before a
/// reminder is handed to the sink (at-most-once delivery).
/// </summary>
public sealed class ReminderScheduler : IReminderScheduler, IAsyncDisposable
{
    private static readonly TimeSpan MinimumRearmDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly IReminderStore _store;
    private readonly IReminderSink _sink;
    private readonly TimeProvider _time;
    private readonly ReminderOptions _options;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CalendarState> _calendars = new();
    private readonly SerialWorkQueue _work;

    private ITimer? _dueTimer;
    private ITimer? _driftTimer;
    private DateTimeOffset? _pausedUntil;
    private DateTimeOffset _lastPruneUtc;
    private volatile bool _started;
    private volatile bool _disposed;
    private volatile bool _storageError;

    public ReminderScheduler(IReminderStore store, IReminderSink sink, TimeProvider time, ReminderOptions options, ILogger<ReminderScheduler>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _work = new SerialWorkQueue(_logger);
    }

    public event EventHandler? Changed;

    public bool HasStorageError => _storageError;

    public int PendingCount
    {
        get
        {
            var now = _time.GetUtcNow();
            return _entries.Values.Count(e =>
                IsActiveCalendar(e.Record.CalendarId, out _) &&
                ReminderStateMachine.NextDueUtc(e.Record) is not null &&
                ReminderStateMachine.Status(e.Record, now, _options) is not ReminderStatus.Expired);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await _store.LoadAsync(ct).ConfigureAwait(false);
            var occurrences = snapshot.Occurrences.ToDictionary(o => o.OccurrenceKey, StringComparer.Ordinal);
            foreach (var record in snapshot.Records)
            {
                var occurrence = occurrences.TryGetValue(record.OccurrenceKey, out var o)
                    ? o
                    : new EventOccurrence(record.CalendarId, string.Empty, record.OccurrenceKey, record.EventStartUtc, record.EventStartUtc,
                        Availability.Unknown, IsAllDay: false, record.Cancelled, PrivacySafeTitle: null, record.RevisionFingerprint);
                _entries[record.OccurrenceKey] = new Entry(record, occurrence);
            }

            await PruneCoreAsync(ct).ConfigureAwait(false);

            _dueTimer = _time.CreateTimer(_ => Schedule(ProcessDueAsync), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _driftTimer = _time.CreateTimer(_ => Schedule(DriftCheckAsync), null, _options.DriftCheckInterval, _options.DriftCheckInterval);
            _started = true;
            ArmTimer(_time.GetUtcNow(), allowImmediate: true);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        return ReconcileAsync(calendarId, events, now - _options.ExpansionPast, now + _options.ExpansionFuture, ct);
    }

    public async Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        List<(string Tag, string Group)> retract;
        bool changed;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var records = new List<ReminderRecord>();
            var occurrences = new List<EventOccurrence>();
            var touchedTags = new HashSet<(string, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var occurrence in events.Where(e => e.CalendarId == calendarId && !e.IsAllDay))
            {
                if (!seen.Add(occurrence.OccurrenceKey))
                {
                    continue; // duplicate key in one feed: first wins
                }

                if (_entries.TryGetValue(occurrence.OccurrenceKey, out var entry))
                {
                    if (entry.Record.CalendarId != calendarId)
                    {
                        continue;
                    }

                    ReminderRecord updated;
                    if (occurrence.IsCancelled)
                    {
                        updated = ReminderStateMachine.ApplyCancelled(entry.Record);
                        if (!ReferenceEquals(updated, entry.Record))
                        {
                            AddTag(touchedTags, entry.Record);
                        }
                    }
                    else
                    {
                        (updated, var change) = ReminderStateMachine.ApplyOccurrence(entry.Record, occurrence, _options);
                        if (change == OccurrenceChange.StartChanged)
                        {
                            AddTag(touchedTags, entry.Record);
                        }
                    }

                    if (!ReferenceEquals(updated, entry.Record))
                    {
                        records.Add(updated);
                    }

                    if (entry.Occurrence != occurrence)
                    {
                        occurrences.Add(occurrence);
                    }

                    _entries[occurrence.OccurrenceKey] = new Entry(updated, occurrence);
                }
                else if (!occurrence.IsCancelled)
                {
                    var record = ReminderRecord.CreateFor(occurrence, _options);
                    _entries[occurrence.OccurrenceKey] = new Entry(record, occurrence);
                    records.Add(record);
                    occurrences.Add(occurrence);
                }
            }

            // Occurrences that disappeared from a complete, valid feed are cancelled (only inside the expansion window).
            foreach (var entry in _entries.Values.Where(e =>
                         e.Record.CalendarId == calendarId &&
                         !seen.Contains(e.Record.OccurrenceKey) &&
                         e.Record.EventStartUtc >= windowStartUtc &&
                         e.Record.EventStartUtc <= windowEndUtc).ToList())
            {
                var cancelled = ReminderStateMachine.ApplyCancelled(entry.Record);
                if (ReferenceEquals(cancelled, entry.Record))
                {
                    continue;
                }

                AddTag(touchedTags, entry.Record);
                var occurrence = entry.Occurrence with { IsCancelled = true };
                _entries[entry.Record.OccurrenceKey] = new Entry(cancelled, occurrence);
                records.Add(cancelled);
                occurrences.Add(occurrence);
            }

            changed = records.Count > 0 || occurrences.Count > 0;
            if (changed)
            {
                await TryPersistAsync(records, occurrences, ct).ConfigureAwait(false);
            }

            retract = TagsWithNothingAwaiting(touchedTags, now);
            ArmTimer(now, allowImmediate: true);
        }
        finally
        {
            _gate.Release();
        }

        Retract(retract);
        if (changed)
        {
            RaiseChanged();
        }
    }

    public Task<IReadOnlyList<SnoozeResult>> SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, CancellationToken ct) =>
        SnoozeAsync(occurrenceKeys, minutesBeforeStart, SnoozeMode.Strict, ct);

    public async Task<IReadOnlyList<SnoozeResult>> SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, SnoozeMode mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(occurrenceKeys);
        var results = new List<SnoozeResult>();
        List<(string Tag, string Group)> retract;
        Exception? storageFailure;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var updates = new List<ReminderRecord>();
            var tags = new HashSet<(string, string)>();
            foreach (var key in occurrenceKeys.Distinct(StringComparer.Ordinal))
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    results.Add(new SnoozeResult(key, SnoozeOutcome.NotFound, null));
                    continue;
                }

                var minutes = minutesBeforeStart;
                if (mode == SnoozeMode.NextFutureFallback && SnoozeTargets.IsValidChoice(minutesBeforeStart, _options))
                {
                    var fallback = SnoozeTargets.ChosenOrNextFuture(entry.Record.EventStartUtc, minutesBeforeStart, now, _options);
                    if (fallback is null)
                    {
                        results.Add(new SnoozeResult(key, SnoozeOutcome.TargetNotInFuture, null));
                        continue;
                    }

                    minutes = fallback.Value;
                }

                var (updated, outcome) = ReminderStateMachine.ApplySnooze(entry.Record, minutes, now, _options);
                results.Add(new SnoozeResult(key, outcome, outcome == SnoozeOutcome.Scheduled ? updated.SnoozedUntilUtc : null));
                if (!ReferenceEquals(updated, entry.Record))
                {
                    AddTag(tags, entry.Record);
                    updates.Add(updated);
                    _entries[key] = entry with { Record = updated };
                }
            }

            storageFailure = updates.Count > 0 ? await TryPersistAsync(updates, [], ct).ConfigureAwait(false) : null;
            retract = TagsWithNothingAwaiting(tags, now);
            ArmTimer(now, allowImmediate: true);
        }
        finally
        {
            _gate.Release();
        }

        Retract(retract);
        RaiseChanged();
        if (storageFailure is not null)
        {
            throw storageFailure;
        }

        return results;
    }

    public async Task DismissAsync(IReadOnlyList<string> occurrenceKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(occurrenceKeys);
        List<(string Tag, string Group)> retract;
        Exception? storageFailure;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var updates = new List<ReminderRecord>();
            var tags = new HashSet<(string, string)>();
            foreach (var key in occurrenceKeys.Distinct(StringComparer.Ordinal))
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    continue;
                }

                AddTag(tags, entry.Record);
                var updated = ReminderStateMachine.ApplyDismiss(entry.Record);
                if (!ReferenceEquals(updated, entry.Record))
                {
                    updates.Add(updated);
                    _entries[key] = entry with { Record = updated };
                }
            }

            storageFailure = updates.Count > 0 ? await TryPersistAsync(updates, [], ct).ConfigureAwait(false) : null;
            retract = TagsWithNothingAwaiting(tags, now);
            ArmTimer(now, allowImmediate: true);
        }
        finally
        {
            _gate.Release();
        }

        Retract(retract);
        RaiseChanged();
        if (storageFailure is not null)
        {
            throw storageFailure;
        }
    }

    public async Task RecordToastAsync(IReadOnlyList<string> occurrenceKeys, string tag, string group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(occurrenceKeys);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updates = new List<ReminderRecord>();
            foreach (var key in occurrenceKeys)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    var updated = entry.Record with { LastToastTag = tag, LastToastGroup = group };
                    _entries[key] = entry with { Record = updated };
                    updates.Add(updated);
                }
            }

            if (updates.Count > 0)
            {
                await TryPersistAsync(updates, [], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveCalendarDataAsync(Guid calendarId, CancellationToken ct)
    {
        var retract = new List<(string Tag, string Group)>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in _entries.Values.Where(e => e.Record.CalendarId == calendarId).ToList())
            {
                if (entry.Record.LastToastTag is { } tag && entry.Record.LastToastGroup is { } group && !retract.Contains((tag, group)))
                {
                    retract.Add((tag, group));
                }

                _entries.TryRemove(entry.Record.OccurrenceKey, out _);
            }

            try
            {
                await _store.DeleteCalendarDataAsync(calendarId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _storageError = true;
                _logger.LogError(ex, "Failed to delete stored data for calendar {CalendarId}", calendarId);
                throw;
            }

            ArmTimer(_time.GetUtcNow(), allowImmediate: true);
        }
        finally
        {
            _gate.Release();
        }

        Retract(retract);
        RaiseChanged();
    }

    public Task RecalculateAsync(CancellationToken ct) => ProcessDueAsync();

    public void UpdateCalendarState(Guid calendarId, string name, bool enabled, DateTimeOffset? lastSuccessUtc)
    {
        var wasEnabled = _calendars.TryGetValue(calendarId, out var previous) && previous.Enabled;
        _calendars[calendarId] = new CalendarState(name, enabled, lastSuccessUtc);
        if (wasEnabled && !enabled)
        {
            Schedule(() => RetractCalendarToastsAsync(calendarId));
        }

        Schedule(ProcessDueAsync);
        RaiseChanged();
    }

    public void ForgetCalendar(Guid calendarId)
    {
        _calendars.TryRemove(calendarId, out _);
        RaiseChanged();
    }

    public void SetPausedUntil(DateTimeOffset? pausedUntilUtc)
    {
        _pausedUntil = pausedUntilUtc;
        Schedule(ProcessDueAsync);
        RaiseChanged();
    }

    public bool IsCurrent(string occurrenceKey, long stateVersion) =>
        _entries.TryGetValue(occurrenceKey, out var entry) && entry.Record.StateVersion == stateVersion;

    public IReadOnlyList<UpcomingReminder> GetUpcoming(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var now = _time.GetUtcNow();
        return _entries.Values
            .Where(e => e.Record.EventStartUtc >= fromUtc && e.Record.EventStartUtc <= toUtc)
            .Select(e =>
            {
                var hasCalendar = _calendars.TryGetValue(e.Record.CalendarId, out var cal);
                return (Entry: e, HasCalendar: hasCalendar, Calendar: cal);
            })
            .Where(x => x.HasCalendar && x.Calendar!.Enabled)
            .OrderBy(x => x.Entry.Record.EventStartUtc)
            .ThenBy(x => x.Calendar!.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x =>
            {
                var status = ReminderStateMachine.Status(x.Entry.Record, now, _options);
                var targets = status is ReminderStatus.Dismissed or ReminderStatus.Cancelled or ReminderStatus.Expired
                    ? []
                    : SnoozeTargets.Available(x.Entry.Record.EventStartUtc, now, _options);
                return new UpcomingReminder(x.Entry.Record, x.Entry.Occurrence, x.Calendar!.Name, status, targets);
            })
            .ToList();
    }

    /// <summary>Completes when all background work queued so far (timer callbacks, calendar updates) has run.</summary>
    internal Task IdleAsync() => _work.IdleAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dueTimer is not null)
        {
            await _dueTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_driftTimer is not null)
        {
            await _driftTimer.DisposeAsync().ConfigureAwait(false);
        }

        await _work.CloseAsync().ConfigureAwait(false);
    }

    private async Task ProcessDueAsync()
    {
        if (!_started || _disposed)
        {
            return;
        }

        var fire = new List<DueReminder>();
        var updates = new List<ReminderRecord>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var paused = _pausedUntil is { } until && now < until;
            foreach (var entry in _entries.Values)
            {
                if (!IsActiveCalendar(entry.Record.CalendarId, out var calendar))
                {
                    continue;
                }

                var evaluation = ReminderStateMachine.Evaluate(entry.Record, now, _options);
                ReminderRecord updated;
                switch (evaluation.Action)
                {
                    case DueAction.Fire when !IsFresh(calendar, now):
                        continue; // stale feed: hold, fire late if it recovers before start
                    case DueAction.Fire when paused:
                        updated = ReminderStateMachine.ApplyConsumed(entry.Record);
                        break;
                    case DueAction.Fire:
                        updated = ReminderStateMachine.ApplyFired(entry.Record, now);
                        fire.Add(new DueReminder(updated, entry.Occurrence, calendar.Name, evaluation.Kind, now));
                        break;
                    case DueAction.Expire:
                        updated = ReminderStateMachine.ApplyConsumed(entry.Record);
                        break;
                    default:
                        continue;
                }

                updates.Add(updated);
                _entries[entry.Record.OccurrenceKey] = entry with { Record = updated };
            }

            if (updates.Count > 0)
            {
                // Persist before handing to the notification layer; a storage failure must not swallow a reminder.
                await TryPersistAsync(updates, [], CancellationToken.None).ConfigureAwait(false);
            }

            ArmTimer(now, allowImmediate: false);
        }
        finally
        {
            _gate.Release();
        }

        if (fire.Count > 0)
        {
            _logger.LogInformation("{Count} reminder(s) due", fire.Count);
            _sink.Enqueue(fire);
        }

        if (updates.Count > 0)
        {
            RaiseChanged();
        }
    }

    private async Task DriftCheckAsync()
    {
        if (!_started || _disposed)
        {
            return;
        }

        if (_time.GetUtcNow() - _lastPruneUtc > TimeSpan.FromDays(1))
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await PruneCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        await ProcessDueAsync().ConfigureAwait(false);
    }

    private async Task PruneCoreAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var cutoff = now - Retention;
        foreach (var entry in _entries.Values.Where(e => e.Record.EventStartUtc < cutoff).ToList())
        {
            _entries.TryRemove(entry.Record.OccurrenceKey, out _);
        }

        try
        {
            await _store.PruneAsync(cutoff, ct).ConfigureAwait(false);
            _lastPruneUtc = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pruning old reminder state failed");
        }
    }

    private async Task RetractCalendarToastsAsync(Guid calendarId)
    {
        if (!_started || _disposed)
        {
            return;
        }

        List<(string Tag, string Group)> retract;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            retract = _entries.Values
                .Where(e => e.Record.CalendarId == calendarId && e.Record.LastToastTag is not null && e.Record.LastToastGroup is not null)
                .Select(e => (e.Record.LastToastTag!, e.Record.LastToastGroup!))
                .Distinct()
                .ToList();
        }
        finally
        {
            _gate.Release();
        }

        Retract(retract);
    }

    private async Task<Exception?> TryPersistAsync(IReadOnlyList<ReminderRecord> records, IReadOnlyList<EventOccurrence> occurrences, CancellationToken ct)
    {
        try
        {
            await _store.SaveAsync(records, occurrences, ct).ConfigureAwait(false);
            _storageError = false;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _storageError = true;
            _logger.LogError(ex, "Persisting reminder state failed ({Count} record(s))", records.Count);
            return ex;
        }
    }

    private void ArmTimer(DateTimeOffset now, bool allowImmediate)
    {
        if (_dueTimer is null || _disposed)
        {
            return;
        }

        DateTimeOffset? next = null;
        foreach (var entry in _entries.Values)
        {
            if (!IsActiveCalendar(entry.Record.CalendarId, out var calendar) || !IsFresh(calendar, now))
            {
                continue;
            }

            if (ReminderStateMachine.NextDueUtc(entry.Record) is { } due && (next is null || due < next))
            {
                next = due;
            }
        }

        if (next is null)
        {
            _dueTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var delay = next.Value - now;
        if (delay <= TimeSpan.Zero && allowImmediate)
        {
            _dueTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Schedule(ProcessDueAsync);
            return;
        }

        _dueTimer.Change(delay < MinimumRearmDelay && !allowImmediate ? MinimumRearmDelay : (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), Timeout.InfiniteTimeSpan);
    }

    private List<(string Tag, string Group)> TagsWithNothingAwaiting(HashSet<(string Tag, string Group)> tags, DateTimeOffset now)
    {
        var result = new List<(string, string)>();
        foreach (var (tag, group) in tags)
        {
            var stillAwaiting = _entries.Values.Any(e =>
                string.Equals(e.Record.LastToastTag, tag, StringComparison.Ordinal) &&
                IsAwaitingUser(e.Record, now));
            if (!stillAwaiting)
            {
                result.Add((tag, group));
            }
        }

        return result;
    }

    private bool IsAwaitingUser(ReminderRecord record, DateTimeOffset now) =>
        record.InitialNotificationSent && !record.Dismissed && !record.Cancelled &&
        record.SnoozedUntilUtc is null && now < record.EventStartUtc + _options.StartGrace;

    private static void AddTag(HashSet<(string, string)> tags, ReminderRecord record)
    {
        if (record.LastToastTag is { } tag && record.LastToastGroup is { } group)
        {
            tags.Add((tag, group));
        }
    }

    private void Retract(List<(string Tag, string Group)> tags)
    {
        foreach (var (tag, group) in tags)
        {
            try
            {
                _sink.RetractToast(tag, group);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retracting toast failed");
            }
        }
    }

    private bool IsActiveCalendar(Guid calendarId, out CalendarState calendar)
    {
        if (_calendars.TryGetValue(calendarId, out var c) && c.Enabled)
        {
            calendar = c;
            return true;
        }

        calendar = default!;
        return false;
    }

    private bool IsFresh(CalendarState calendar, DateTimeOffset now) =>
        calendar.LastSuccessUtc is { } success && now - success <= _options.MaxCacheAge;

    private void Schedule(Func<Task> operation)
    {
        if (!_disposed)
        {
            _work.Schedule(operation);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changed handler failed");
        }
    }

    private sealed record Entry(ReminderRecord Record, EventOccurrence Occurrence);

    private sealed record CalendarState(string Name, bool Enabled, DateTimeOffset? LastSuccessUtc);
}
