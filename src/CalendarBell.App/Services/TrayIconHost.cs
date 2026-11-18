using System.Drawing;
using System.Windows.Forms;
using CalendarBell.Core.Resources;
using Microsoft.Win32;

namespace CalendarBell.App.Services;

/// <summary>
/// Notification-area icon and context menu (spec §2.4). Uses WinForms NotifyIcon inside the WPF app (no visible
/// WinForms windows). Icons: normal / warning / paused; tooltip "CalendarBell · &lt;state&gt;".
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly AppHost _host;
    private readonly WindowManager _windows;
    private readonly Action _exit;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _normal;
    private readonly Icon _warning;
    private readonly Icon _paused;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _checkNowItem;

    public TrayIconHost(AppHost host, WindowManager windows, Action exit)
    {
        _host = host;
        _windows = windows;
        _exit = exit;
        var iconDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Icons");
        _normal = new Icon(Path.Combine(iconDir, "calendarbell.ico"), SystemInformation.SmallIconSize);
        _warning = new Icon(Path.Combine(iconDir, "calendarbell-warning.ico"), SystemInformation.SmallIconSize);
        _paused = new Icon(Path.Combine(iconDir, "calendarbell-paused.ico"), SystemInformation.SmallIconSize);

        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9f), Padding = new Padding(2, 4, 2, 4) };
        var header = new ToolStripMenuItem(Strings.Get("App_Name")) { Enabled = false, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var upcoming = Item("Tray_Upcoming", () => _windows.ShowUpcoming(null));
        upcoming.Font = new Font(_menu.Font, FontStyle.Bold); // default action (double click)
        _pauseItem = new ToolStripMenuItem(Strings.Get("Tray_Pause"));
        _pauseItem.DropDownItems.Add(Item("Tray_Pause30", () => _host.Pause(TimeSpan.FromMinutes(30))));
        _pauseItem.DropDownItems.Add(Item("Tray_Pause60", () => _host.Pause(TimeSpan.FromHours(1))));
        _pauseItem.DropDownItems.Add(Item("Tray_PauseUntilResumed", () => _host.Pause(null)));
        _resumeItem = Item("Tray_Resume", _host.Resume);
        _startupItem = Item("Tray_StartWithWindows", () => _host.UpdateSettings(s => s with { StartWithWindows = !s.StartWithWindows }));
        _checkNowItem = Item("Tray_CheckNow", () => _ = _host.CheckNowAsync());
        _statusItem = new ToolStripMenuItem(string.Empty) { Enabled = false };

        _menu.Items.AddRange(
        [
            header,
            new ToolStripSeparator(),
            upcoming,
            Item("Tray_Settings", _windows.ShowSettings),
            _checkNowItem,
            _pauseItem,
            _resumeItem,
            _startupItem,
            new ToolStripSeparator(),
            _statusItem,
            Item("Tray_About", _windows.ShowAbout),
            Item("Tray_Exit", _exit),
        ]);
        _menu.Opening += (_, _) => Refresh();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _icon = new NotifyIcon { Icon = _normal, ContextMenuStrip = _menu, Text = "CalendarBell", Visible = true };
        _icon.DoubleClick += (_, _) => _windows.ShowUpcoming(null);
        _host.StatusChanged += OnStatusChanged;
        Refresh();
    }

    public void Dispose()
    {
        _host.StatusChanged -= OnStatusChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _normal.Dispose();
        _warning.Dispose();
        _paused.Dispose();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (_menu.InvokeRequired || !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
        }
        else
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var status = _host.GetStatus();
        _icon.Icon = status.Health switch
        {
            AppHealth.Paused => _paused,
            AppHealth.Warning or AppHealth.StorageError => _warning,
            _ => _normal,
        };
        var tooltip = Strings.Format("Tooltip_Format", status.StateText);
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _statusItem.Text = Strings.Format("Tray_Status", status.Summary);
        _pauseItem.Visible = !status.Paused;
        _resumeItem.Visible = status.Paused;
        _startupItem.Checked = _host.Settings.StartWithWindows;
        _checkNowItem.Enabled = _host.Sync is not null;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyTheme);
        }
    }

    private void ApplyTheme()
    {
        var dark = IsDarkTheme();
        _menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(dark)) { RoundedEdges = true };
        var fore = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 28);
        foreach (var item in _menu.Items.OfType<ToolStripItem>().Concat(_pauseItem.DropDownItems.OfType<ToolStripItem>()))
        {
            item.ForeColor = fore;
        }

        _menu.BackColor = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        _pauseItem.DropDown.BackColor = _menu.BackColor;
    }

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is 0;
    }

    private static ToolStripMenuItem Item(string key, Action action)
    {
        var item = new ToolStripMenuItem(Strings.Get(key)) { Padding = new Padding(4, 3, 4, 3) };
        item.Click += (_, _) => action();
        return item;
    }

    private sealed class MenuColors(bool dark) : ProfessionalColorTable
    {
        private readonly Color _background = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        private readonly Color _hover = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
        private readonly Color _border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);

        public override Color ToolStripDropDownBackground => _background;

        public override Color ImageMarginGradientBegin => _background;

        public override Color ImageMarginGradientMiddle => _background;

        public override Color ImageMarginGradientEnd => _background;

        public override Color MenuBorder => _border;

        public override Color MenuItemBorder => _hover;

        public override Color MenuItemSelected => _hover;

        public override Color MenuItemSelectedGradientBegin => _hover;

        public override Color MenuItemSelectedGradientEnd => _hover;

        public override Color SeparatorDark => _border;

        public override Color SeparatorLight => _border;

        public override Color CheckBackground => _hover;

        public override Color CheckSelectedBackground => _hover;

        public override Color CheckPressedBackground => _hover;
    }
}
