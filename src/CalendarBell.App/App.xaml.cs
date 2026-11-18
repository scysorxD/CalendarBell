using System.Windows;
using System.Windows.Threading;
using CalendarBell.App.Notifications;
using CalendarBell.App.Services;
using CalendarBell.App.Views;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Windows;
using Microsoft.Extensions.Logging;

namespace CalendarBell.App;

public enum AppRunMode
{
    Main,
    Install,
    Uninstall,
}

/// <summary>
/// WPF application object. In <see cref="AppRunMode.Main"/> it is the invisible resident process: tray icon,
/// on-demand windows, and the AppHost services. Closing a window never exits; only tray → Exit does.
/// </summary>
public partial class App : Application
{
    private readonly AppRunMode _mode;
    private readonly StartupOptions _options;
    private readonly DataPaths _paths;
    private readonly SingleInstance? _instance;
    private AppHost? _host;
    private WindowManager? _windows;
    private TrayIconHost? _tray;
    private ILogger? _logger;
    private bool _exiting;

    public App()
        : this(AppRunMode.Main, new StartupOptions(), DataPaths.Default(), null)
    {
    }

    internal App(AppRunMode mode, StartupOptions options, DataPaths paths, SingleInstance? instance)
    {
        _mode = mode;
        _options = options;
        _paths = paths;
        _instance = instance;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError("Unobserved task exception: {Error}", LogSanitizer.Describe(args.Exception));
            args.SetObserved();
        };

        if (_mode != AppRunMode.Main)
        {
            RunSetup();
            return;
        }

        _host = new AppHost(_paths, _options);
        _host.Initialize();
        _logger = _host.LoggerFactory.CreateLogger<App>();
        _windows = new WindowManager(_host);
        _tray = new TrayIconHost(_host, _windows, () => _ = ExitAsync());
        _host.OpenUpcomingRequested += (_, keys) => Dispatcher.BeginInvoke(() => _windows.ShowUpcoming(keys));
        if (_instance is not null)
        {
            _instance.MessageReceived += (_, message) => Dispatcher.BeginInvoke(() => HandleInstanceMessage(message));
            _instance.StartListening();
        }

        await _host.StartAsync();

        if (_options.OpenSettings)
        {
            _windows.ShowSettings();
        }
        else if (_options.OpenUpcoming)
        {
            _windows.ShowUpcoming(null);
        }
        else if (!_options.IsNotificationActivation && !_host.Settings.FirstRunCompleted && await HasNoCalendarsAsync())
        {
            _windows.ShowSettings(); // first run: open Settings once so the first calendar can be added
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _host is not null)
        {
            _tray?.Dispose();
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }

    private async Task<bool> HasNoCalendarsAsync() =>
        _host?.Calendars is null || (await _host.Calendars.GetAllAsync(CancellationToken.None)).Count == 0;

    private void HandleInstanceMessage(InstanceMessage message)
    {
        switch (message.Command)
        {
            case "open-settings":
                _windows?.ShowSettings();
                break;
            case "open-upcoming":
                _windows?.ShowUpcoming(null);
                break;
            case "notification-action" when _host is not null:
                var inputs = message.Inputs ?? [];
                _ = _host.HandleActivationAsync(new ToastActivation(message.Arguments ?? string.Empty, inputs));
                break;
            case "exit":
                _ = ExitAsync();
                break;
        }
    }

    /// <summary>Tray → Exit: really stops (windows, tray, timers, connections, notification delivery, mutex).</summary>
    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _windows?.CloseAll();
        _tray?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        Shutdown();
    }

    private void RunSetup()
    {
        var window = new SetupWindow(_mode == AppRunMode.Install ? SetupMode.Install : SetupMode.Uninstall, _paths);
        window.Closed += (_, _) =>
        {
            switch (window.Result)
            {
                case SetupResult.Installed:
                    SetupActions.LaunchInstalled();
                    break;
                case SetupResult.RunWithoutInstalling:
                    SetupActions.RelaunchSelf("--no-install-prompt");
                    break;
            }

            Shutdown();
        };
        window.Show();
        window.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failure in one window or action must not take down the resident reminder process.
        _logger?.LogError("Unhandled UI exception: {Error}{NewLine}{Stack}", LogSanitizer.Describe(e.Exception), Environment.NewLine, LogSanitizer.Redact(e.Exception.StackTrace));
        e.Handled = true;
    }

    private void LogFatal(Exception? exception)
    {
        if (exception is not null)
        {
            _logger?.LogCritical("Fatal: {Error}{NewLine}{Stack}", LogSanitizer.Describe(exception), Environment.NewLine, LogSanitizer.Redact(exception.StackTrace));
        }
    }
}
