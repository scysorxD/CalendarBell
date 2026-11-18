using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CalendarBell.App.Mvvm;
using CalendarBell.App.Services;
using CalendarBell.App.Views;
using CalendarBell.Core.Models;
using CalendarBell.Core.Resources;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Sync;
using Microsoft.Windows.AppNotifications;

namespace CalendarBell.App.ViewModels;

public enum StatusTone
{
    Neutral,
    Success,
    Caution,
    Critical,
}

public sealed class CalendarRowViewModel : ObservableObject
{
    private readonly Func<CalendarRowViewModel, Task> _toggle;
    private string _name;
    private bool _enabled;
    private string _statusText = string.Empty;
    private StatusTone _tone;
    private string? _detail;

    public CalendarRowViewModel(CalendarSource source, Func<CalendarRowViewModel, Task> toggle)
    {
        Source = source;
        _name = source.Name;
        _enabled = source.Enabled;
        _toggle = toggle;
    }

    public CalendarSource Source { get; set; }

    public Guid Id => Source.CalendarId;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
            {
                _ = _toggle(this);
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public StatusTone Tone
    {
        get => _tone;
        set => Set(ref _tone, value);
    }

    /// <summary>Sanitized error detail (tooltip); never contains the URL.</summary>
    public string? Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    internal void SetEnabledSilently(bool value)
    {
        _enabled = value;
        Raise(nameof(Enabled));
    }
}

/// <summary>Settings window (spec §3.2): calendar list with live status, general options and banners.</summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly WindowManager _windows;
    private string _info = string.Empty;
    private bool _loading;

    public SettingsViewModel(AppHost host, WindowManager windows)
    {
        _host = host;
        _windows = windows;
        AddCalendarCommand = new AsyncRelayCommand(_ => EditAsync(null), _ => _host.Calendars is not null);
        EditCommand = new AsyncRelayCommand(p => EditAsync(p as CalendarRowViewModel));
        CheckNowCommand = new AsyncRelayCommand(p => CheckNowAsync(p as CalendarRowViewModel));
        RemoveCommand = new AsyncRelayCommand(p => RemoveAsync(p as CalendarRowViewModel));
        SendTestCommand = new AsyncRelayCommand(async _ => Info = await _host.SendTestNotificationAsync());
        OpenUpcomingCommand = new RelayCommand(_ => _windows.ShowUpcoming(null));
        OpenAboutCommand = new RelayCommand(_ => _windows.ShowAbout());
        OpenNotificationSettingsCommand = new RelayCommand(_ => WindowsEnvironment.OpenNotificationSettings());
        ResumeCommand = new RelayCommand(_ => _host.Resume());
        ResetStorageCommand = new AsyncRelayCommand(_ => ResetStorageAsync());
        _host.StatusChanged += OnHostStatusChanged;
        _ = LoadAsync();
    }

    public ObservableCollection<CalendarRowViewModel> Calendars { get; } = [];

    public ICommand AddCalendarCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand CheckNowCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand SendTestCommand { get; }

    public ICommand OpenUpcomingCommand { get; }

    public ICommand OpenAboutCommand { get; }

    public ICommand OpenNotificationSettingsCommand { get; }

    public ICommand ResumeCommand { get; }

    public ICommand ResetStorageCommand { get; }

    public bool StartWithWindows
    {
        get => _host.Settings.StartWithWindows;
        set => _host.UpdateSettings(s => s with { StartWithWindows = value });
    }

    public bool PlaySound
    {
        get => _host.Settings.PlaySound;
        set => _host.UpdateSettings(s => s with { PlaySound = value });
    }

    public bool PrivacyMode
    {
        get => _host.Settings.PrivacyMode;
        set => _host.UpdateSettings(s => s with { PrivacyMode = value });
    }

    public bool HasNoCalendars => !_loading && Calendars.Count == 0 && _host.StorageError is null;

    public string CalendarCountText => Calendars.Count == 0
        ? string.Empty
        : Strings.Format("Settings_CalendarCount", Calendars.Count(c => c.Enabled), Calendars.Count);

    public bool ShowNotificationsOff => _host.Toasts is { } t && (!t.IsRegistered || t.Setting != AppNotificationSetting.Enabled);

    public bool ShowQuiet => !ShowNotificationsOff && WindowsEnvironment.IsNotificationQuietTime();

    public bool ShowElevated => _host.IsElevated;

    public bool ShowStorageError => _host.StorageError is not null;

    public bool ShowDeliveryFailed => _host.Orchestrator?.LastShowFailed == true;

    public bool ShowPaused => _host.GetStatus().Paused;

    public string PausedText => _host.Settings.PausedUntilUtc is { } until && until != DateTimeOffset.MaxValue
        ? Strings.Format("Banner_Paused", until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
        : Strings.Get("Banner_PausedIndefinitely");

    public string Info
    {
        get => _info;
        set => Set(ref _info, value);
    }

    public void Dispose() => _host.StatusChanged -= OnHostStatusChanged;

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            Calendars.Clear();
            if (_host.Calendars is not null)
            {
                foreach (var calendar in await _host.Calendars.GetAllAsync(CancellationToken.None))
                {
                    Calendars.Add(new CalendarRowViewModel(calendar, ToggleAsync));
                }
            }
        }
        finally
        {
            _loading = false;
        }

        RefreshStatuses();
    }

    private void OnHostStatusChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshStatuses);

    private void RefreshStatuses()
    {
        var statuses = _host.Sync?.GetStatuses().ToDictionary(s => s.CalendarId) ?? [];
        foreach (var row in Calendars)
        {
            if (!statuses.TryGetValue(row.Id, out var status))
            {
                row.StatusText = Strings.Get(row.Enabled ? "State_NeverSynced" : "State_Disabled");
                row.Tone = StatusTone.Neutral;
                continue;
            }

            (row.StatusText, row.Tone) = Describe(status);
            row.Detail = status.Message;
        }

        foreach (var name in new[]
                 {
                     nameof(HasNoCalendars), nameof(CalendarCountText), nameof(ShowNotificationsOff), nameof(ShowQuiet), nameof(ShowElevated),
                     nameof(ShowStorageError), nameof(ShowDeliveryFailed), nameof(ShowPaused), nameof(PausedText), nameof(StartWithWindows),
                     nameof(PlaySound), nameof(PrivacyMode),
                 })
        {
            Raise(name);
        }
    }

    internal static (string Text, StatusTone Tone) Describe(CalendarSyncStatus status)
    {
        string Time(DateTimeOffset? t) => t?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) ?? string.Empty;
        if (!status.Enabled)
        {
            return (Strings.Get("State_Disabled"), StatusTone.Neutral);
        }

        if (status.IsStale && status.State != CalendarSyncState.Synced)
        {
            return (Strings.Format("Settings_StatusAt", Strings.Get("State_Stale"), Time(status.LastSuccessUtc)), StatusTone.Critical);
        }

        return status.State switch
        {
            CalendarSyncState.Synced => (Strings.Format("Settings_SyncedAt", Time(status.LastSuccessUtc)), StatusTone.Success),
            CalendarSyncState.Refreshing => (Strings.Get("State_Refreshing"), StatusTone.Neutral),
            CalendarSyncState.NetworkError => (Strings.Get("State_NetworkError") + (status.LastSuccessUtc is null ? string.Empty : " · " + Strings.Format("Settings_SyncedAt", Time(status.LastSuccessUtc))), StatusTone.Caution),
            CalendarSyncState.InvalidCalendar => (Strings.Get("State_InvalidCalendar"), StatusTone.Critical),
            CalendarSyncState.Disabled => (Strings.Get("State_Disabled"), StatusTone.Neutral),
            _ => (Strings.Get("State_NeverSynced"), StatusTone.Neutral),
        };
    }

    private async Task ToggleAsync(CalendarRowViewModel row)
    {
        if (_host.Calendars is null || _host.Sync is null)
        {
            return;
        }

        try
        {
            var updated = row.Source with { Enabled = row.Enabled };
            await _host.Calendars.SaveAsync(updated, CancellationToken.None);
            row.Source = updated;
            await _host.Sync.CalendarUpdatedAsync(row.Id, urlChanged: false, CancellationToken.None);
            Info = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            row.SetEnabledSilently(!row.Enabled);
            Info = Strings.Format("Editor_SaveFailed", LogSanitizer.Redact(ex.Message));
        }

        RefreshStatuses();
    }

    private async Task EditAsync(CalendarRowViewModel? row)
    {
        if (_host.Calendars is null || _host.Sync is null)
        {
            return;
        }

        var existing = await _host.Calendars.GetAllAsync(CancellationToken.None);
        var editor = new CalendarEditorViewModel(_host, row?.Source, existing);
        var window = new CalendarEditorWindow(editor) { Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() };
        if (window.ShowDialog() == true)
        {
            await LoadAsync();
        }
    }

    private async Task CheckNowAsync(CalendarRowViewModel? row)
    {
        if (row is null || _host.Sync is null)
        {
            return;
        }

        row.StatusText = Strings.Get("State_Refreshing");
        var outcome = await _host.Sync.RefreshAsync(row.Id, CancellationToken.None);
        Info = outcome.Message ?? string.Empty;
        RefreshStatuses();
    }

    private async Task RemoveAsync(CalendarRowViewModel? row)
    {
        if (row is null || _host.Calendars is null || _host.Sync is null)
        {
            return;
        }

        var owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (!ConfirmDialog.Ask(owner, Strings.Get("Remove_Title"), Strings.Format("Remove_Message", row.Name), Strings.Get("Remove_Confirm"), destructive: true))
        {
            return;
        }

        await _host.Calendars.DeleteAsync(row.Id, CancellationToken.None);
        await _host.Sync.CalendarRemovedAsync(row.Id, CancellationToken.None);
        Calendars.Remove(row);
        RefreshStatuses();
    }

    private async Task ResetStorageAsync()
    {
        var owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (!ConfirmDialog.Ask(owner, Strings.Get("Reset_Title"), Strings.Get("Reset_Message"), Strings.Get("Reset_Confirm"), destructive: true))
        {
            return;
        }

        await _host.ResetStorageAsync();
        await LoadAsync();
    }
}
