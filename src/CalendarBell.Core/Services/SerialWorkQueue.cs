using Microsoft.Extensions.Logging;

namespace CalendarBell.Core.Services;

/// <summary>
/// Runs background operations one after another on the thread pool (timer callbacks must never block).
/// <see cref="IdleAsync"/> lets tests and shutdown wait until everything queued so far has finished.
/// </summary>
internal sealed class SerialWorkQueue
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;
    private bool _closed;

    public SerialWorkQueue(ILogger logger)
    {
        _logger = logger;
    }

    public void Schedule(Func<Task> operation)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _tail = _tail.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await operation().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // shutting down
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background operation failed");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    public async Task IdleAsync()
    {
        while (true)
        {
            Task tail;
            lock (_lock)
            {
                tail = _tail;
            }

            await tail.ConfigureAwait(false);
            lock (_lock)
            {
                if (ReferenceEquals(tail, _tail))
                {
                    return;
                }
            }
        }
    }

    public async Task CloseAsync()
    {
        lock (_lock)
        {
            _closed = true;
        }

        await IdleAsync().ConfigureAwait(false);
    }
}
