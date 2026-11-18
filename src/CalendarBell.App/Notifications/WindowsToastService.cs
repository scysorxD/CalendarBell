using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Notifications;
using CalendarBell.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;

namespace CalendarBell.App.Notifications;

/// <summary>A click on a toast or one of its buttons, as delivered by Windows (raised on a background thread).</summary>
public sealed record ToastActivation(string Argument, IReadOnlyDictionary<string, string> UserInput);

/// <summary>
/// Windows App SDK adapter (spec §4.1): registers the unpackaged app for activation, shows toasts with stable
/// tag/group and expiration, removes them by tag+group, and keeps Notification Center below the per-app limit.
/// Toast XML comes from <see cref="ToastXmlBuilder"/> (unit-tested in Core).
/// </summary>
public sealed class WindowsToastService : INotificationPresenter, IDisposable
{
    private const int MaxActiveReminderToasts = 15;

    private readonly ILogger _logger;
    private bool _registered;

    public WindowsToastService(ILogger<WindowsToastService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<ToastActivation>? Activated;

    public bool IsRegistered => _registered;

    public string LastRegistrationError { get; private set; } = string.Empty;

    /// <summary>Windows' per-app notification switch (Settings → System → Notifications).</summary>
    public AppNotificationSetting Setting
    {
        get
        {
            try
            {
                return AppNotificationManager.Default.Setting;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return AppNotificationSetting.Unsupported;
            }
        }
    }

    /// <summary>Subscribe first, then Register (required order), so a launch caused by a click is not lost.</summary>
    public void Register(string iconPath)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register("CalendarBell", new Uri(iconPath));
            _registered = true;
            _logger.LogInformation("Notifications registered (setting: {Setting})", Setting);

            // When Windows started this process because a toast was clicked, that first activation is delivered
            // through the launch arguments (after Register), not through NotificationInvoked.
            if (TryGetLaunchActivation() is { } launch)
            {
                _logger.LogInformation("Started by a notification click");
                Activated?.Invoke(this, launch);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            LastRegistrationError = LogSanitizer.Describe(ex);
            _logger.LogError("Notification registration failed: {Error}", LastRegistrationError);
        }
    }

    public Task ShowAsync(NotificationBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_registered)
        {
            throw new InvalidOperationException("Notifications are not registered.");
        }

        var notification = new AppNotification(batch.Xml)
        {
            Tag = batch.Tag,
            Group = batch.Group,
            Expiration = batch.ExpiresUtc,
        };
        AppNotificationManager.Default.Show(notification);
        if (notification.Id == 0)
        {
            // Show() fails silently in elevated processes and when Windows refuses the payload.
            throw new InvalidOperationException("Windows did not accept the notification.");
        }

        _ = PruneAsync();
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(string tag, string group, CancellationToken ct)
    {
        if (_registered)
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group);
        }
    }

    /// <summary>Removes reminder toasts that no longer need attention (start-up reconciliation).</summary>
    public async Task<int> RemoveWhereAsync(Func<string, string, bool> isObsolete)
    {
        if (!_registered)
        {
            return 0;
        }

        var removed = 0;
        try
        {
            foreach (var n in await AppNotificationManager.Default.GetAllAsync())
            {
                if (isObsolete(n.Tag ?? string.Empty, n.Group ?? string.Empty))
                {
                    await AppNotificationManager.Default.RemoveByTagAndGroupAsync(n.Tag, n.Group);
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning("Reconciling active notifications failed: {Error}", LogSanitizer.Describe(ex));
        }

        return removed;
    }

    /// <summary>The notification activation that launched this process, if any. Call after Register.</summary>
    public static ToastActivation? TryGetLaunchActivation()
    {
        try
        {
            var activated = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification &&
                activated.Data is AppNotificationActivatedEventArgs args)
            {
                return new ToastActivation(args.Argument ?? string.Empty, args.UserInput.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidCastException)
        {
            // not available: treat as a normal launch
        }

        return null;
    }

    /// <summary>Uninstall only: removes the COM/AUMID registration so a stale toast cannot relaunch a removed app.</summary>
    public static void UnregisterAll()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // nothing registered
        }
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        try
        {
            // Stops in-process delivery; the registration stays so clicks on remaining toasts relaunch the app.
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogDebug("Unregister: {Error}", ex.GetType().Name);
        }

        _registered = false;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var inputs = args.UserInput.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Activated?.Invoke(this, new ToastActivation(args.Argument ?? string.Empty, inputs));
    }

    private async Task PruneAsync()
    {
        try
        {
            var reminders = (await AppNotificationManager.Default.GetAllAsync())
                .Where(n => n.Group == NotificationBatcher.ReminderGroup)
                .OrderBy(n => n.Id)
                .ToList();
            foreach (var old in reminders.Take(Math.Max(0, reminders.Count - MaxActiveReminderToasts)))
            {
                await AppNotificationManager.Default.RemoveByTagAndGroupAsync(old.Tag, old.Group);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogDebug("Prune skipped: {Error}", ex.GetType().Name);
        }
    }
}
