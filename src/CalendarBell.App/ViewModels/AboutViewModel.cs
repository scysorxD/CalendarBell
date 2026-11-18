using System.Windows;
using System.Windows.Input;
using CalendarBell.App.Mvvm;
using CalendarBell.App.Services;
using CalendarBell.Core.Resources;
using CalendarBell.Infrastructure.Sync;

namespace CalendarBell.App.ViewModels;

/// <summary>About and diagnostics (spec §9.2). The diagnostics text is redacted (no URLs, no titles).</summary>
public sealed class AboutViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private string _diagnostics = string.Empty;
    private string _info = string.Empty;

    public AboutViewModel(AppHost host)
    {
        _host = host;
        CheckNowCommand = new AsyncRelayCommand(async _ =>
        {
            await _host.CheckNowAsync();
            Refresh();
        });
        SendTestCommand = new AsyncRelayCommand(async _ => Info = await _host.SendTestNotificationAsync());
        OpenLogsCommand = new RelayCommand(_ => WindowsEnvironment.OpenFolder(_host.Paths.LogsDirectory));
        CopyCommand = new RelayCommand(_ =>
        {
            Clipboard.SetText(_host.BuildDiagnostics());
            Info = Strings.Get("About_Copied");
        });
        _host.StatusChanged += OnChanged;
        Refresh();
    }

    public string VersionText => Strings.Format("About_Version", SyncOptions.AppVersion);

    public ICommand CheckNowCommand { get; }

    public ICommand SendTestCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public ICommand CopyCommand { get; }

    public string Diagnostics
    {
        get => _diagnostics;
        private set => Set(ref _diagnostics, value);
    }

    public string Info
    {
        get => _info;
        set => Set(ref _info, value);
    }

    public void Dispose() => _host.StatusChanged -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(Refresh);

    private void Refresh() => Diagnostics = _host.BuildDiagnostics();
}
