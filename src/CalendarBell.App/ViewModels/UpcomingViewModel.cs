using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CalendarBell.App.Mvvm;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Notifications;
using CalendarBell.Core.Resources;

namespace CalendarBell.App.ViewModels;

public sealed record SnoozeOption(int Minutes, string Label, string Key);

public sealed record FilterOption(string Label, object? Value);

public sealed class UpcomingItemViewModel : ObservableObject
{
    public UpcomingItemViewModel(UpcomingReminder reminder, bool highlighted, bool showTitle)
    {
        Reminder = reminder;
        Highlighted = highlighted;
        var f = new DisplayFormatter();
        TimeRange = $"{f.Time(reminder.Occurrence.StartUtc)}–{f.Time(reminder.Occurrence.EndUtc)}";
        CalendarName = reminder.CalendarName;
        Availability = DisplayFormatter.AvailabilityLabel(reminder.Occurrence.Availability);
        Title = showTitle ? reminder.Occurrence.PrivacySafeTitle : null;
        StatusText = reminder.Status switch
        {
            ReminderStatus.Snoozed => Strings.Format("Status_Snoozed", f.Time(reminder.Record.SnoozedUntilUtc!.Value)),
            var s => Strings.Get("Status_" + s),
        };
        SnoozeOptions = reminder.AvailableSnoozeTargets.Select(m => new SnoozeOption(m, DisplayFormatter.SnoozeLabel(m), reminder.Record.OccurrenceKey)).ToList();
        CanDismiss = reminder.Status is ReminderStatus.Pending or ReminderStatus.Notified or ReminderStatus.Snoozed;
        IsInactive = reminder.Status is ReminderStatus.Dismissed or ReminderStatus.Cancelled or ReminderStatus.Expired;
    }

    public UpcomingReminder Reminder { get; }

    public string Key => Reminder.Record.OccurrenceKey;

    public bool Highlighted { get; }

    public string TimeRange { get; }

    public string CalendarName { get; }

    public string Availability { get; }

    public string? Title { get; }

    public bool HasTitle => !string.IsNullOrEmpty(Title);

    public string StatusText { get; }

    public IReadOnlyList<SnoozeOption> SnoozeOptions { get; }

    public bool HasSnoozeOptions => SnoozeOptions.Count > 0;

    public bool CanDismiss { get; }

    public bool IsInactive { get; }
}

public sealed class UpcomingGroupViewModel(string header, IReadOnlyList<UpcomingItemViewModel> items)
{
    public string Header { get; } = header;

    public IReadOnlyList<UpcomingItemViewModel> Items { get; } = items;
}

/// <summary>Upcoming reminders (spec §3.3): next 24 h grouped by day, filters, individual Snooze/Dismiss.</summary>
public sealed class UpcomingViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private HashSet<string>? _highlight;
    private FilterOption _calendarFilter;
    private FilterOption _statusFilter;
    private bool _showDismissed;
    private string _message = string.Empty;

    public UpcomingViewModel(AppHost host)
    {
        _host = host;
        CalendarFilters.Add(new FilterOption(Strings.Get("Upcoming_AllCalendars"), null));
        StatusFilters.Add(new FilterOption(Strings.Get("Upcoming_AllStatuses"), null));
        foreach (var status in Enum.GetValues<ReminderStatus>())
        {
            StatusFilters.Add(new FilterOption(status == ReminderStatus.Snoozed ? Strings.Format("Status_Snoozed", "…") : Strings.Get("Status_" + status), status));
        }

        _calendarFilter = CalendarFilters[0];
        _statusFilter = StatusFilters[0];
        SnoozeCommand = new AsyncRelayCommand(p => SnoozeAsync(p as SnoozeOption));
        DismissCommand = new AsyncRelayCommand(p => DismissAsync(p as UpcomingItemViewModel));
        ShowAllCommand = new RelayCommand(_ => Highlight(null));
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), Application.Current.Dispatcher);
        _timer.Start();
        _host.StatusChanged += OnHostChanged;
        Refresh();
    }

    public ObservableCollection<UpcomingGroupViewModel> Groups { get; } = [];

    public ObservableCollection<FilterOption> CalendarFilters { get; } = [];

    public ObservableCollection<FilterOption> StatusFilters { get; } = [];

    public ICommand SnoozeCommand { get; }

    public ICommand DismissCommand { get; }

    public ICommand ShowAllCommand { get; }

    public FilterOption CalendarFilter
    {
        get => _calendarFilter;
        set
        {
            if (Set(ref _calendarFilter, value ?? CalendarFilters[0]))
            {
                Refresh();
            }
        }
    }

    public FilterOption StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (Set(ref _statusFilter, value ?? StatusFilters[0]))
            {
                Refresh();
            }
        }
    }

    public bool ShowDismissed
    {
        get => _showDismissed;
        set
        {
            if (Set(ref _showDismissed, value))
            {
                Refresh();
            }
        }
    }

    public bool IsFilteredToNotification => _highlight is { Count: > 0 };

    public bool IsEmpty => Groups.Count == 0;

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public void Highlight(IReadOnlyList<string>? keys)
    {
        _highlight = keys is { Count: > 0 } ? keys.ToHashSet(StringComparer.Ordinal) : null;
        Raise(nameof(IsFilteredToNotification));
        Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _host.StatusChanged -= OnHostChanged;
    }

    private void OnHostChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var now = _host.Time.GetUtcNow();
        var all = _host.Scheduler?.GetUpcoming(now - TimeSpan.FromHours(2), now + TimeSpan.FromHours(24)) ?? [];
        SyncCalendarFilters(all);
        var showTitles = !_host.Settings.PrivacyMode;
        var items = all
            .Where(r => _highlight is null || _highlight.Contains(r.Record.OccurrenceKey))
            .Where(r => _calendarFilter.Value is not Guid id || r.Record.CalendarId == id)
            .Where(r => _statusFilter.Value is ReminderStatus wanted
                ? r.Status == wanted
                : _highlight is not null || ShowDismissed || r.Status is not (ReminderStatus.Dismissed or ReminderStatus.Expired))
            .Select(r => new UpcomingItemViewModel(r, _highlight?.Contains(r.Record.OccurrenceKey) == true, showTitles))
            .ToList();

        var today = DateOnly.FromDateTime(DateTime.Now);
        Groups.Clear();
        foreach (var group in items.GroupBy(i => DateOnly.FromDateTime(i.Reminder.Occurrence.StartUtc.ToLocalTime().DateTime)))
        {
            var header = group.Key == today ? Strings.Get("Upcoming_Today")
                : group.Key == today.AddDays(1) ? Strings.Get("Upcoming_Tomorrow")
                : group.Key.ToString("D", CultureInfo.CurrentCulture);
            Groups.Add(new UpcomingGroupViewModel(header, group.ToList()));
        }

        Raise(nameof(IsEmpty));
    }

    private void SyncCalendarFilters(IReadOnlyList<UpcomingReminder> all)
    {
        foreach (var (id, name) in all.Select(r => (r.Record.CalendarId, r.CalendarName)).Distinct())
        {
            var existing = CalendarFilters.FirstOrDefault(f => f.Value is Guid g && g == id);
            if (existing is null)
            {
                CalendarFilters.Add(new FilterOption(name, id));
            }
        }
    }

    private async Task SnoozeAsync(SnoozeOption? option)
    {
        if (option is null || _host.Scheduler is null)
        {
            return;
        }

        try
        {
            var result = await _host.Scheduler.SnoozeAsync([option.Key], option.Minutes, CancellationToken.None);
            Message = result.All(r => r.Outcome == SnoozeOutcome.Scheduled) ? string.Empty : Strings.Get("Upcoming_ActionFailed");
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Message = Strings.Get("Upcoming_ActionFailed");
        }

        Refresh();
    }

    private async Task DismissAsync(UpcomingItemViewModel? item)
    {
        if (item is null || _host.Scheduler is null)
        {
            return;
        }

        try
        {
            await _host.Scheduler.DismissAsync([item.Key], CancellationToken.None);
            Message = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Message = Strings.Get("Upcoming_ActionFailed");
        }

        Refresh();
    }
}
