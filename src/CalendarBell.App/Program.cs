using System.Globalization;
using CalendarBell.App.Notifications;
using CalendarBell.App.Services;
using CalendarBell.Core.Resources;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Windows;
using Microsoft.Windows.AppNotifications;

namespace CalendarBell.App;

/// <summary>
/// Entry point (WinExe: never a console). Decides between setup dialogs, forwarding to the resident instance, or
/// becoming the resident instance (spec §7.4).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        var paths = DataPaths.Default();
        ApplyUiLanguage(paths);

        if (options.Uninstall)
        {
            if (options.Quiet)
            {
                SetupActions.UninstallAsync(paths, deleteUserData: false, removeInstallDirectory: !options.KeepFiles).GetAwaiter().GetResult();
                return 0;
            }

            return Run(AppRunMode.Uninstall, options, paths, null);
        }

        var installedCopy = SetupActions.CreateInstaller().IsRunningFromInstallDirectory(AppContext.BaseDirectory);
        var offerInstall = options.Install ||
                           (!installedCopy && !options.NoInstallPrompt && !options.Background && !options.IsNotificationActivation && !paths.IsOverridden);
        if (offerInstall)
        {
            if (options.Install && options.Quiet)
            {
                SetupActions.InstallAsync(paths).GetAwaiter().GetResult();
                SetupActions.LaunchInstalled("--background");
                return 0;
            }

            return Run(AppRunMode.Install, options, paths, null);
        }

        using var instance = SingleInstance.Create(paths.Root);
        if (!instance.IsPrimary)
        {
            return ForwardToPrimary(instance, options);
        }

        return Run(AppRunMode.Main, options, paths, instance);
    }

    private static int Run(AppRunMode mode, StartupOptions options, DataPaths paths, SingleInstance? instance)
    {
        var app = new App(mode, options, paths, instance);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>A second launch hands its request to the resident instance and exits without showing anything.</summary>
    private static int ForwardToPrimary(SingleInstance instance, StartupOptions options)
    {
        WindowsEnvironment.AllowAnyProcessToSetForeground();
        if (options.IsNotificationActivation)
        {
            ForwardNotificationActivation(instance);
            return 0;
        }

        if (options.Background)
        {
            return 0; // the sign-in task fired while CalendarBell is already running
        }

        var command = options.OpenUpcoming ? "open-upcoming" : "open-settings";
        instance.SendAsync(new InstanceMessage(command, null, null), TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>
    /// Windows launched this process for a toast click while another instance owns the mutex (start-up race):
    /// receive the activation here and forward it, so the action is processed exactly once by the resident instance.
    /// </summary>
    private static void ForwardNotificationActivation(SingleInstance instance)
    {
        using var received = new ManualResetEventSlim();
        ToastActivation? activation = null;
        AppNotificationManager.Default.NotificationInvoked += (_, e) =>
        {
            activation = new ToastActivation(e.Argument ?? string.Empty, e.UserInput.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
            received.Set();
        };
        AppNotificationManager.Default.Register("CalendarBell", new Uri(Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "calendarbell.png")));
        activation ??= WindowsToastService.TryGetLaunchActivation();
        if ((activation is not null || received.Wait(TimeSpan.FromSeconds(5))) && activation is not null)
        {
            instance.SendAsync(
                new InstanceMessage("notification-action", activation.Argument, activation.UserInput.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)),
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    private static void ApplyUiLanguage(DataPaths paths)
    {
        try
        {
            Strings.Culture = CultureInfo.GetCultureInfo(new SettingsStore(paths).Load().Language);
        }
        catch (Exception ex) when (ex is CultureNotFoundException or IOException or UnauthorizedAccessException)
        {
            Strings.Culture = CultureInfo.GetCultureInfo("en-US");
        }
    }
}
