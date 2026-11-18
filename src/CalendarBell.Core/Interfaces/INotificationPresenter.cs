using CalendarBell.Core.Notifications;

namespace CalendarBell.Core.Interfaces;

/// <summary>Shows and removes native toasts (Windows App SDK adapter in the app; fakes in tests).</summary>
public interface INotificationPresenter
{
    Task ShowAsync(NotificationBatch batch, CancellationToken ct);

    Task RemoveAsync(string tag, string group, CancellationToken ct);
}
