using System.Windows;
using CalendarBell.App.ViewModels;
using CalendarBell.App.Views;
using CalendarBell.Infrastructure.Storage;

namespace CalendarBell.App.Services;

/// <summary>Opens Settings / Upcoming / About on demand (one of each), restores their last valid position and brings them forward.</summary>
public sealed class WindowManager
{
    private readonly AppHost _host;
    private SettingsWindow? _settings;
    private UpcomingWindow? _upcoming;
    private AboutWindow? _about;

    public WindowManager(AppHost host)
    {
        _host = host;
    }

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(new SettingsViewModel(_host, this));
            Track(_settings, "Settings", () => _settings = null);
            if (!_host.Settings.FirstRunCompleted)
            {
                _host.UpdateSettings(s => s with { FirstRunCompleted = true });
            }
        }

        BringToFront(_settings);
    }

    public void ShowUpcoming(IReadOnlyList<string>? highlightKeys)
    {
        if (_upcoming is null)
        {
            _upcoming = new UpcomingWindow(new UpcomingViewModel(_host));
            Track(_upcoming, "Upcoming", () => _upcoming = null);
        }

        _upcoming.ViewModel.Highlight(highlightKeys);
        BringToFront(_upcoming);
    }

    public void ShowAbout()
    {
        if (_about is null)
        {
            _about = new AboutWindow(new AboutViewModel(_host));
            Track(_about, "About", () => _about = null);
        }

        BringToFront(_about);
    }

    public void CloseAll()
    {
        _settings?.Close();
        _upcoming?.Close();
        _about?.Close();
    }

    private void Track(Window window, string name, Action onClosed)
    {
        RestorePlacement(window, _host.Settings.GetPlacement(name));
        window.Closing += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                var placement = new WindowPlacement(window.Left, window.Top, window.Width, window.Height);
                _host.UpdateSettings(s => s.WithPlacement(name, placement));
            }
        };
        window.Closed += (_, _) =>
        {
            onClosed();
            (window.DataContext as IDisposable)?.Dispose();
        };
    }

    private static void RestorePlacement(Window window, WindowPlacement? placement)
    {
        if (placement is null || !IsVisibleOnSomeScreen(placement))
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = Math.Max(window.MinWidth, placement.Width);
        window.Height = Math.Max(window.MinHeight, placement.Height);
    }

    /// <summary>A saved position is reused only if its title bar area is still on a connected monitor.</summary>
    private static bool IsVisibleOnSomeScreen(WindowPlacement p)
    {
        if (double.IsNaN(p.Left) || double.IsNaN(p.Top) || p.Width < 200 || p.Height < 150)
        {
            return false;
        }

        var titleBar = new Rect(p.Left + 20, p.Top, Math.Max(40, p.Width - 40), 32);
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.IntersectsWith(titleBar))
        {
            return false;
        }

        // Screen reports physical pixels; WPF positions are DIPs relative to the primary monitor's scale.
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        var scale = primary is null || primary.Bounds.Width == 0 ? 1.0 : SystemParameters.PrimaryScreenWidth / primary.Bounds.Width;
        return System.Windows.Forms.Screen.AllScreens.Any(s =>
        {
            var area = s.WorkingArea;
            return new Rect(area.Left * scale, area.Top * scale, area.Width * scale, area.Height * scale).IntersectsWith(titleBar);
        });
    }

    private static void BringToFront(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
