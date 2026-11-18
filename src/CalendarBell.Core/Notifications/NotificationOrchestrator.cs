using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarBell.Core.Notifications;

public sealed record ToastActionResult(bool Handled, string? Message = null);

/// <summary>
/// Receives due reminders, coalesces them, enforces "at most one new toast every 8 s" (everything queued
/// when a slot opens becomes one toast), shows it, and handles toast actions (spec §6.1, §6.5).
/// </summary>
public sealed class NotificationOrchestrator : IReminderSink, IAsyncDisposable
{
    private readonly INotificationPresenter _presenter;
    private readonly IToastBatchStore _batches;
    private readonly TimeProvider _time;
    private readonly ReminderOptions _options;
    private readonly Func<NotificationPreferences> _preferences;
    private readonly NotificationBatcher _batcher;
    private readonly ILogger _logger;
    private readonly SerialWorkQueue _work;
    private readonly object _lock = new();
    private readonly List<DueReminder> _queue = [];
    private readonly ITimer _pumpTimer;
    private IReminderScheduler? _scheduler;
    private DateTimeOffset? _lastEmissionUtc;
    private bool _disposed;

    public NotificationOrchestrator(
        INotificationPresenter presenter,
        IToastBatchStore batches,
        TimeProvider time,
        ReminderOptions options,
        Func<NotificationPreferences> preferences,
        DisplayFormatter? formatter = null,
        ILogger<NotificationOrchestrator>? logger = null)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _batches = batches ?? throw new ArgumentNullException(nameof(batches));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _batcher = new NotificationBatcher(options, formatter ?? new DisplayFormatter());
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _work = new SerialWorkQueue(_logger);
        _pumpTimer = _time.CreateTimer(_ => _work.Schedule(EmitAsync), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised (on a background thread) when the user asks to see reminders: body click or "View reminders".</summary>
    public event EventHandler<IReadOnlyList<string>>? OpenUpcomingRequested;

    /// <summary>Raised when showing a toast fails or recovers (tray/diagnostics).</summary>
    public event EventHandler? DeliveryStatusChanged;

    public bool LastShowFailed { get; private set; }

    public DateTimeOffset? LastShownUtc { get; private set; }

    public void AttachScheduler(IReminderScheduler scheduler) =>
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    public void Enqueue(IReadOnlyList<DueReminder> due)
    {
        ArgumentNullException.ThrowIfNull(due);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _queue.AddRange(due);
        }

        ArmPump();
    }

    public void RetractToast(string tag, string group) =>
        _work.Schedule(async () =>
        {
            await _presenter.RemoveAsync(tag, group, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Toast {Tag} removed", tag);
        });

    public async Task<ToastActionResult> HandleActionAsync(ToastAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind == ToastActionKind.TestAction)
        {
            // Test toasts never touch production reminder state.
            _logger.LogInformation("Test notification action '{Action}' received", action.RawAction);
            await SafeRemoveAsync(action.BatchId, NotificationBatcher.TestGroup, ct).ConfigureAwait(false);
            return new ToastActionResult(true);
        }

        var scheduler = _scheduler ?? throw new InvalidOperationException("Scheduler not attached.");
        var batch = await _batches.GetBatchAsync(action.BatchId, ct).ConfigureAwait(false);
        if (batch is null)
        {
            _logger.LogWarning("Toast action '{Action}' for unknown or expired batch ignored", action.RawAction);
            return new ToastActionResult(false, "Unknown notification");
        }

        try
        {
            switch (action.Kind)
            {
                case ToastActionKind.Snooze:
                    var minutes = action.SnoozeMinutes ?? _options.SnoozeTargetsMinutes[0];
                    var results = await scheduler.SnoozeAsync(batch.OccurrenceKeys, minutes, SnoozeMode.NextFutureFallback, ct).ConfigureAwait(false);
                    _logger.LogInformation("Snooze ({Minutes} min before start) applied to {Count} occurrence(s): {Outcomes}",
                        minutes, results.Count, string.Join(",", results.Select(r => r.Outcome)));
                    await SafeRemoveAsync(batch.BatchId, batch.Group, ct).ConfigureAwait(false);
                    return new ToastActionResult(true);
                case ToastActionKind.Dismiss:
                    await scheduler.DismissAsync(batch.OccurrenceKeys, ct).ConfigureAwait(false);
                    _logger.LogInformation("Dismissed {Count} occurrence(s)", batch.OccurrenceKeys.Count);
                    await SafeRemoveAsync(batch.BatchId, batch.Group, ct).ConfigureAwait(false);
                    return new ToastActionResult(true);
                case ToastActionKind.Open:
                case ToastActionKind.View:
                    OpenUpcomingRequested?.Invoke(this, batch.OccurrenceKeys);
                    return new ToastActionResult(true);
                default:
                    return new ToastActionResult(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Toast action '{Action}' failed", action.RawAction);
            return new ToastActionResult(false, "The action could not be saved.");
        }
    }

    public async Task<NotificationBatch> ShowTestNotificationAsync(CancellationToken ct)
    {
        var batch = _batcher.BuildTest(_time.GetUtcNow(), _preferences(), ToastArguments.NewBatchId());
        await _presenter.ShowAsync(batch, ct).ConfigureAwait(false);
        _logger.LogInformation("Test notification shown");
        return batch;
    }

    internal Task IdleAsync() => _work.IdleAsync();

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _disposed = true;
            _queue.Clear();
        }

        await _pumpTimer.DisposeAsync().ConfigureAwait(false);
        await _work.CloseAsync().ConfigureAwait(false);
    }

    private void ArmPump()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_queue.Count == 0 || _disposed)
            {
                return;
            }

            var nextSlot = _lastEmissionUtc is { } last ? last + _options.MinToastInterval : now;
            if (nextSlot <= now)
            {
                _work.Schedule(EmitAsync);
            }
            else
            {
                _pumpTimer.Change(nextSlot - now, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task EmitAsync()
    {
        var scheduler = _scheduler;
        if (scheduler is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        List<DueReminder> current;
        lock (_lock)
        {
            if (_queue.Count == 0 || _disposed)
            {
                return;
            }

            if (_lastEmissionUtc is { } last && now - last < _options.MinToastInterval)
            {
                _pumpTimer.Change(last + _options.MinToastInterval - now, Timeout.InfiniteTimeSpan);
                return;
            }

            // Drop items whose state changed since they fell due (dismissed/snoozed from Upcoming, cancelled...).
            current = _queue
                .Where(d => scheduler.IsCurrent(d.Record.OccurrenceKey, d.Record.StateVersion))
                .GroupBy(d => d.Record.OccurrenceKey, StringComparer.Ordinal)
                .Select(g => g.Last())
                .ToList();
            _queue.Clear();
            if (current.Count == 0)
            {
                return;
            }

            _lastEmissionUtc = now;
        }

        var batch = _batcher.Build(current, now, _preferences(), ToastArguments.NewBatchId());
        var keys = batch.Members.Select(m => m.Record.OccurrenceKey).ToList();
        try
        {
            await _batches.SaveBatchAsync(new ToastBatchRecord(batch.BatchId, batch.Group, keys, now, batch.ExpiresUtc), CancellationToken.None).ConfigureAwait(false);
            await _presenter.ShowAsync(batch, CancellationToken.None).ConfigureAwait(false);
            await scheduler.RecordToastAsync(keys, batch.Tag, batch.Group, CancellationToken.None).ConfigureAwait(false);
            LastShownUtc = now;
            SetFailed(false);
            _logger.LogInformation("Toast shown: kind={Kind} members={Count}", batch.Kind, keys.Count);
        }
        catch (Exception ex)
        {
            SetFailed(true);
            _logger.LogError(ex, "Showing toast failed (kind={Kind}, members={Count})", batch.Kind, keys.Count);
        }
    }

    private async Task SafeRemoveAsync(string tag, string group, CancellationToken ct)
    {
        try
        {
            await _presenter.RemoveAsync(tag, group, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Removing toast failed");
        }
    }

    private void SetFailed(bool failed)
    {
        if (LastShowFailed == failed)
        {
            return;
        }

        LastShowFailed = failed;
        DeliveryStatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
