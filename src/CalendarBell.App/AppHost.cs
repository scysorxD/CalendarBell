using System.Globalization;
using CalendarBell.App.Notifications;
using CalendarBell.App.Services;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Notifications;
using CalendarBell.Core.Resources;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Ics;
using CalendarBell.Infrastructure.Install;
using CalendarBell.Infrastructure.Logging;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Sync;
using CalendarBell.Infrastructure.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CalendarBell.App;

/// <summary>Aggregated state shown by the tray icon, tooltip and Settings banners.</summary>
public enum AppHealth
{
    Ok,
    Warning,
    Paused,
    StorageError,
}

public sealed record AppStatus(AppHealth Health, string Summary, string StateText, int CalendarCount, bool Paused, DateTimeOffset? PausedUntilUtc);

/// <summary>
/// Composition root and lifecycle of the resident process (spec §7.4): wires storage, scheduler, notifications and
/// sync; applies settings; reacts to time/power changes; routes toast activations. UI-free (windows live in the app).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _settingsLock = new();
    private FileLoggerProvider? _fileLogger;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private AppSettings _settings = new();
    private Timer? _pauseTimer;
    private CalendarFeedClient? _feedClient;

    public AppHost(DataPaths paths, StartupOptions options)
    {
        Paths = paths;
        Options = options;
        SettingsStore = new SettingsStore(paths);
        InstallerPaths = InstallerPaths.Default();
        Installer = new PerUserInstaller(InstallerPaths, SyncOptions.AppVersion);
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<IReadOnlyList<string>>? OpenUpcomingRequested;

    public DataPaths Paths { get; }

    public StartupOptions Options { get; }

    public SettingsStore SettingsStore { get; }

    public InstallerPaths InstallerPaths { get; }

    public PerUserInstaller Installer { get; }

    public ILoggerFactory LoggerFactory { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

    public TimeProvider Time { get; } = TimeProvider.System;

    public AppSettings Settings
    {
        get
        {
            lock (_settingsLock)
            {
                return _settings;
            }
        }
    }

    public bool IsElevated { get; private set; }

    public StorageCorruptedException? StorageError { get; private set; }

    public CalendarBellDatabase? Database { get; private set; }

    public SqliteCalendarRepository? Calendars { get; private set; }

    public IUrlProtector UrlProtector { get; } = new DpapiUrlProtector();

    public ReminderScheduler? Scheduler { get; private set; }

    public NotificationOrchestrator? Orchestrator { get; private set; }

    public WindowsToastService? Toasts { get; private set; }

    public CalendarSyncService? Sync { get; private set; }

    public StartupRegistration? Startup { get; private set; }

    public ReminderOptions ReminderOptions { get; } = ReminderOptions.Default;

    public SyncOptions SyncOptions { get; } = new();

    public bool IsInstalledCopy => Installer.IsRunningFromInstallDirectory(AppContext.BaseDirectory);

    /// <summary>Phase 1 (synchronous, before the tray exists): logging, settings, notification registration.</summary>
    public void Initialize()
    {
        Paths.EnsureCreated();
        _fileLogger = new FileLoggerProvider(Paths.LogsDirectory, Time);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(_fileLogger).SetMinimumLevel(LogLevel.Information));
        _logger = LoggerFactory.CreateLogger<AppHost>();

        _settings = SettingsStore.Load();
        Strings.Culture = TryCulture(_settings.Language);
        IsElevated = WindowsEnvironment.IsElevated();
        _logger.LogInformation("CalendarBell {Version} starting (installed copy: {Installed}, elevated: {Elevated}, background: {Background})",
            SyncOptions.AppVersion, IsInstalledCopy, IsElevated, Options.Background);
        if (IsElevated)
        {
            _logger.LogWarning("Running elevated: Windows does not deliver app notifications to elevated processes");
        }

        Toasts = new WindowsToastService(LoggerFactory.CreateLogger<WindowsToastService>());
        Toasts.Activated += (_, activation) => _ = HandleActivationAsync(activation);
        Toasts.Register(Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "calendarbell.png"));
        Startup = new StartupRegistration(Path.Combine(AppContext.BaseDirectory, InstallerPaths.ExeName), logger: LoggerFactory.CreateLogger<StartupRegistration>());
    }

    /// <summary>Phase 2: storage, scheduler, sync. Never throws for storage problems (degraded mode instead).</summary>
    public async Task StartAsync()
    {
        try
        {
            // Wired regardless of storage, so a later "Reset storage" ends up fully functional.
            ApplyStartupRegistration();
            SystemEvents.TimeChanged += OnTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _pauseTimer = new Timer(_ => CheckPauseExpiry(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

            if (TryOpenDatabase() && await TryStartServicesAsync().ConfigureAwait(false))
            {
                _logger.LogInformation("Started");
            }
        }
        finally
        {
            _ready.TrySetResult();
            RaiseStatusChanged();
        }
    }

    /// <summary>A failure after the database opened (busy, I/O, unreadable row) becomes a visible Storage error.</summary>
    private async Task<bool> TryStartServicesAsync()
    {
        try
        {
            await StartServicesAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StorageError = StorageCorruptedException.ForUnavailable(ex);
            _logger.LogCritical("Starting reminders failed: {Error}", LogSanitizer.Describe(ex));
            return false;
        }
    }

    public AppStatus GetStatus()
    {
        var settings = Settings;
        var now = Time.GetUtcNow();
        var paused = settings.PausedUntilUtc is { } until && until > now;
        if (StorageError is not null)
        {
            return new AppStatus(AppHealth.StorageError, Strings.Get("State_StorageError"), Strings.Get("State_StorageError"), 0, paused, settings.PausedUntilUtc);
        }

        var statuses = Sync?.GetStatuses() ?? [];
        var count = statuses.Count;
        var enabled = statuses.Where(s => s.Enabled).ToList();
        string state;
        var health = AppHealth.Ok;
        if (paused)
        {
            state = Strings.Get("State_Paused");
            health = AppHealth.Paused;
        }
        else if (IsElevated || Toasts is null || !Toasts.IsRegistered || Toasts.Setting != Microsoft.Windows.AppNotifications.AppNotificationSetting.Enabled || Orchestrator?.LastShowFailed == true)
        {
            state = Strings.Get("State_NotificationsOff");
            health = AppHealth.Warning;
        }
        else if (enabled.Any(s => s.State is CalendarSyncState.NetworkError or CalendarSyncState.InvalidCalendar || s.IsStale) || Scheduler?.HasStorageError == true)
        {
            state = Strings.Get("State_SyncWarning");
            health = AppHealth.Warning;
        }
        else if (enabled.Any(s => s.State is CalendarSyncState.Refreshing or CalendarSyncState.NeverSynced))
        {
            state = Strings.Get("State_Refreshing");
        }
        else
        {
            state = Strings.Get("State_Synced");
        }

        var summary = count switch
        {
            0 => Strings.Get("Status_NoCalendars"),
            1 => Strings.Format("Status_OneCalendar", state),
            _ => Strings.Format("Status_Calendars", count, state),
        };
        return new AppStatus(health, summary, state, count, paused, settings.PausedUntilUtc);
    }

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings before;
        AppSettings after;
        lock (_settingsLock)
        {
            before = _settings;
            after = change(before);
            _settings = after;
        }

        try
        {
            SettingsStore.Save(after);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Saving settings failed: {Error}", LogSanitizer.Describe(ex));
        }

        if (before.PrivacyMode != after.PrivacyMode && Sync is not null)
        {
            _ = ApplyPrivacyChangeAsync(after.PrivacyMode);
        }

        if (before.StartWithWindows != after.StartWithWindows)
        {
            ApplyStartupRegistration();
        }

        if (before.PausedUntilUtc != after.PausedUntilUtc)
        {
            Scheduler?.SetPausedUntil(after.PausedUntilUtc);
        }

        RaiseStatusChanged();
    }

    public void Pause(TimeSpan? duration)
    {
        var until = duration is { } d ? Time.GetUtcNow() + d : DateTimeOffset.MaxValue;
        _logger.LogInformation("Reminders paused ({Duration})", duration?.ToString() ?? "until resumed");
        UpdateSettings(s => s with { PausedUntilUtc = until });
    }

    public void Resume()
    {
        _logger.LogInformation("Reminders resumed");
        UpdateSettings(s => s with { PausedUntilUtc = null });
    }

    public async Task CheckNowAsync()
    {
        if (Sync is not null)
        {
            await Sync.RefreshAllAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    public async Task<string> SendTestNotificationAsync()
    {
        if (Orchestrator is null || Toasts is null || !Toasts.IsRegistered)
        {
            return Strings.Format("Test_Failed", Toasts?.LastRegistrationError ?? "not registered");
        }

        try
        {
            await Orchestrator.ShowTestNotificationAsync(_lifetime.Token).ConfigureAwait(false);
            return Strings.Get("Test_Sent");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning("Test notification failed: {Error}", LogSanitizer.Describe(ex));
            return Strings.Format("Test_Failed", LogSanitizer.Redact(ex.Message));
        }
    }

    /// <summary>Explicit user action after a storage error: move the damaged file aside, restore calendars, start.</summary>
    public async Task ResetStorageAsync()
    {
        Database = CalendarBellDatabase.ResetAfterCorruption(Paths);
        StorageError = null;
        _logger.LogWarning("Storage reset by the user");
        await TryStartServicesAsync().ConfigureAwait(false);
        RaiseStatusChanged();
    }

    /// <summary>Privacy on: forget stored titles everywhere (spec §5.4), then re-download to keep data consistent.</summary>
    private async Task ApplyPrivacyChangeAsync(bool privacyOn)
    {
        try
        {
            if (privacyOn && Scheduler is not null)
            {
                await Scheduler.ClearTitlesAsync(_lifetime.Token).ConfigureAwait(false);
            }

            if (Sync is not null)
            {
                await Sync.PrivacyModeChangedAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Applying the privacy change failed: {Error}", LogSanitizer.Describe(ex));
        }
    }

    /// <summary>Forwarded from a second process (pipe) or raised by Windows when a toast is clicked.</summary>
    public async Task HandleActivationAsync(ToastActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        await _ready.Task.ConfigureAwait(false);
        var action = ToastArguments.Parse(activation.Argument, activation.UserInput);
        if (action is null)
        {
            _logger.LogWarning("Ignored a notification activation with unexpected arguments");
            return;
        }

        if (Orchestrator is null)
        {
            if (action.Kind is ToastActionKind.Open or ToastActionKind.View)
            {
                OpenUpcomingRequested?.Invoke(this, []);
            }

            return;
        }

        var result = await Orchestrator.HandleActionAsync(action, _lifetime.Token).ConfigureAwait(false);
        if (!result.Handled && action.Kind is ToastActionKind.Open or ToastActionKind.View)
        {
            OpenUpcomingRequested?.Invoke(this, []);
        }

        RaiseStatusChanged();
    }

    public string BuildDiagnostics()
    {
        var now = Time.GetUtcNow();
        var lines = new List<string>
        {
            $"CalendarBell {SyncOptions.AppVersion}",
            $"{Strings.Get("Diag_Registration")}: {(Toasts?.IsRegistered == true ? Strings.Get("Diag_Registered") : Strings.Get("Diag_NotRegistered") + " " + Toasts?.LastRegistrationError)}",
            $"{Strings.Get("Diag_NotificationSetting")}: {Toasts?.Setting}",
            $"{Strings.Get("Diag_Startup")}: {Startup?.CurrentMethod()?.ToString() ?? "-"}",
            $"{Strings.Get("Diag_Elevated")}: {(IsElevated ? Strings.Get("Diag_Yes") : Strings.Get("Diag_No"))}",
            $"{Strings.Get("Diag_LocalTime")}: {now.ToLocalTime().ToString("F", CultureInfo.CurrentCulture)}",
            $"{Strings.Get("Diag_TimeZone")}: {TimeZoneInfo.Local.Id} ({TimeZoneInfo.Local.DisplayName})",
            $"{Strings.Get("Diag_Pending")}: {Scheduler?.PendingCount ?? 0}",
            $"{Strings.Get("Diag_DataFolder")}: {Paths.Root}",
            $"Status: {GetStatus().Summary}",
            $"Last toast shown (UTC): {Orchestrator?.LastShownUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}; last toast failed: {Orchestrator?.LastShowFailed}",
        };
        if (StorageError is not null)
        {
            lines.Add("Storage error: " + LogSanitizer.Describe(StorageError));
        }

        lines.Add($"{Strings.Get("Diag_Calendars")}:");
        foreach (var s in Sync?.GetStatuses() ?? [])
        {
            lines.Add($"  - {s.Name} [{s.CalendarId:N}] enabled={s.Enabled} state={s.State} stale={s.IsStale} " +
                      $"lastSuccess={s.LastSuccessUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "never"} next={s.NextRefreshUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-"} " +
                      $"http={s.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"} upcoming={s.UpcomingCount} {LogSanitizer.Redact(s.Message)}");
        }

        return LogSanitizer.Redact(string.Join(Environment.NewLine, lines));
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Shutting down");
        await _lifetime.CancelAsync().ConfigureAwait(false);
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_pauseTimer is not null)
        {
            await _pauseTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (Sync is not null)
        {
            await Sync.DisposeAsync().ConfigureAwait(false);
        }

        if (Scheduler is not null)
        {
            await Scheduler.DisposeAsync().ConfigureAwait(false);
        }

        if (Orchestrator is not null)
        {
            await Orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        Toasts?.Dispose();
        _feedClient?.Dispose();
        Database?.Dispose();
        LoggerFactory.Dispose();
        _fileLogger?.Dispose();
        _lifetime.Dispose();
    }

    private bool TryOpenDatabase()
    {
        try
        {
            Database = CalendarBellDatabase.Open(Paths);
            return true;
        }
        catch (StorageCorruptedException ex)
        {
            StorageError = ex;
            _logger.LogCritical("Storage error: {Error}. A copy was preserved; reminders are paused until the user resets storage", LogSanitizer.Describe(ex));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            StorageError = StorageCorruptedException.ForUnavailable(ex);
            _logger.LogCritical("Storage unavailable: {Error}", LogSanitizer.Describe(ex));
            return false;
        }
    }

    private async Task StartServicesAsync()
    {
        var db = Database!;
        Calendars = new SqliteCalendarRepository(db, Paths);
        var restored = await Calendars.RestoreFromBackupIfEmptyAsync(_lifetime.Token).ConfigureAwait(false);
        if (restored > 0)
        {
            _logger.LogWarning("Restored {Count} calendar(s) from the encrypted backup", restored);
        }

        var store = new SqliteReminderStore(db);
        Orchestrator = new NotificationOrchestrator(
            Toasts!, store, Time, ReminderOptions,
            () => new NotificationPreferences(Settings.PlaySound, Settings.PrivacyMode),
            new DisplayFormatter(), LoggerFactory.CreateLogger<NotificationOrchestrator>());
        Orchestrator.OpenUpcomingRequested += (_, keys) => OpenUpcomingRequested?.Invoke(this, keys);
        Orchestrator.DeliveryStatusChanged += (_, _) => RaiseStatusChanged();
        Scheduler = new ReminderScheduler(store, Orchestrator, Time, ReminderOptions, LoggerFactory.CreateLogger<ReminderScheduler>());
        Orchestrator.AttachScheduler(Scheduler);
        Scheduler.Changed += (_, _) => RaiseStatusChanged();
        var paused = Settings.PausedUntilUtc is { } until && until > Time.GetUtcNow() ? Settings.PausedUntilUtc : null;
        Scheduler.SetPausedUntil(paused);

        _feedClient = new CalendarFeedClient(UrlProtector, SyncOptions, Time, logger: LoggerFactory.CreateLogger<CalendarFeedClient>());
        Sync = new CalendarSyncService(Calendars, new SqliteSyncStateStore(db), _feedClient,
            new IcsCalendarParser(LoggerFactory.CreateLogger<IcsCalendarParser>()), Scheduler, Time, SyncOptions, ReminderOptions,
            () => Settings.PrivacyMode, LoggerFactory.CreateLogger<CalendarSyncService>());
        Sync.StatusChanged += (_, _) => RaiseStatusChanged();

        await Scheduler.StartAsync(_lifetime.Token).ConfigureAwait(false);
        await store.PruneBatchesAsync(Time.GetUtcNow() - TimeSpan.FromDays(2), _lifetime.Token).ConfigureAwait(false);
        await Sync.StartAsync(_lifetime.Token).ConfigureAwait(false);
        await ReconcileVisibleToastsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Start-up reconciliation: take down toasts whose occurrences were dismissed, snoozed, cancelled or expired
    /// meanwhile — and keep every toast that still waits for the user (e.g. after a sign-in between T−15 and T).
    /// </summary>
    private async Task ReconcileVisibleToastsAsync()
    {
        if (Toasts is null || Scheduler is null)
        {
            return;
        }

        var awaitingTags = Scheduler.AwaitingToastTags().ToHashSet(StringComparer.Ordinal);
        var removed = await Toasts.RemoveWhereAsync((tag, group) =>
            group == NotificationBatcher.TestGroup || (group == NotificationBatcher.ReminderGroup && !awaitingTags.Contains(tag))).ConfigureAwait(false);
        _logger.LogInformation("Start-up toast reconciliation: kept {Kept} pending toast(s), removed {Removed} obsolete", awaitingTags.Count, removed);
    }

    private void ApplyStartupRegistration()
    {
        if (Startup is null || !IsInstalledCopy)
        {
            return; // never register a development or portable copy for auto-start
        }

        try
        {
            var current = Startup.CurrentMethod();
            if (Settings.StartWithWindows && current is null)
            {
                var method = Startup.Enable();
                UpdateStartupMethod(method.ToString());
            }
            else if (!Settings.StartWithWindows && current is not null)
            {
                Startup.Disable();
                UpdateStartupMethod(null);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogWarning("Updating startup registration failed: {Error}", LogSanitizer.Describe(ex));
        }
    }

    private void UpdateStartupMethod(string? method)
    {
        lock (_settingsLock)
        {
            _settings = _settings with { StartupMethod = method };
        }

        SettingsStore.Save(Settings);
    }

    private void CheckPauseExpiry()
    {
        if (Settings.PausedUntilUtc is { } until && until <= Time.GetUtcNow())
        {
            Resume();
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("System time or time zone changed; recalculating");
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture.ClearCachedData();
        if (Scheduler is not null)
        {
            _ = Scheduler.RecalculateAsync(_lifetime.Token);
        }

        if (Sync is not null)
        {
            _ = Sync.SystemTimeChangedAsync(_lifetime.Token);
        }

        RaiseStatusChanged();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _logger.LogInformation("Resumed from sleep; recalculating reminders and refreshing calendars");
        if (Scheduler is not null)
        {
            _ = Scheduler.RecalculateAsync(_lifetime.Token);
        }

        if (Sync is not null)
        {
            _ = Sync.RefreshAllAsync(_lifetime.Token);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon && Scheduler is not null)
        {
            _ = Scheduler.RecalculateAsync(_lifetime.Token);
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Status handler failed: {Error}", LogSanitizer.Describe(ex));
        }
    }

    private static CultureInfo TryCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US");
        }
    }
}
