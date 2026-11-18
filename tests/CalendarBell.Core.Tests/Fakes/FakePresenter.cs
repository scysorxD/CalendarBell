using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Notifications;

namespace CalendarBell.Core.Tests.Fakes;

internal sealed class FakePresenter : INotificationPresenter
{
    private readonly object _lock = new();

    public List<(NotificationBatch Batch, DateTimeOffset At)> Shown { get; } = [];
    public List<(string Tag, string Group)> Removed { get; } = [];
    public Func<DateTimeOffset>? Clock { get; set; }
    public bool Fail { get; set; }

    public Task ShowAsync(NotificationBatch batch, CancellationToken ct)
    {
        if (Fail)
        {
            throw new InvalidOperationException("simulated toast failure");
        }

        lock (_lock)
        {
            Shown.Add((batch, Clock?.Invoke() ?? DateTimeOffset.MinValue));
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string tag, string group, CancellationToken ct)
    {
        lock (_lock)
        {
            Removed.Add((tag, group));
        }

        return Task.CompletedTask;
    }
}
