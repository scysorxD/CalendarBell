using System.Windows;
using CalendarBell.App.Services;
using CalendarBell.Core.Resources;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Storage;

namespace CalendarBell.App.Views;

public enum SetupMode
{
    Install,
    Uninstall,
}

public enum SetupResult
{
    Cancelled,
    Installed,
    RunWithoutInstalling,
    Uninstalled,
}

/// <summary>Per-user install (from the extracted ZIP) and uninstall (Apps &amp; features) dialog.</summary>
public partial class SetupWindow : Window
{
    private readonly SetupMode _mode;
    private readonly DataPaths _paths;
    private bool _done;

    public SetupWindow(SetupMode mode, DataPaths paths)
    {
        InitializeComponent();
        _mode = mode;
        _paths = paths;
        var installer = SetupActions.CreateInstaller();
        if (mode == SetupMode.Install)
        {
            Title = Strings.Get("Install_Title");
            Heading.Text = Strings.Get("Install_Title");
            Body.Text = Strings.Get("Install_Message");
            PathText.Text = installer.Paths.InstallDirectory;
            Note.Text = Strings.Get("Install_StartsAutomatically") + (installer.IsInstalled ? Environment.NewLine + Strings.Get("Install_AlreadyInstalled") : string.Empty);
            PrimaryButton.Content = Strings.Get(installer.IsInstalled ? "Install_Update" : "Install_Install");
            SecondaryButton.Content = Strings.Get("Install_RunOnce");
            SecondaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            Title = Strings.Get("Uninstall_Title");
            Heading.Text = Strings.Get("Uninstall_Title");
            Body.Text = Strings.Get("Uninstall_Message");
            PathText.Text = installer.Paths.InstallDirectory;
            Note.Visibility = Visibility.Collapsed;
            Option.Content = Strings.Get("Uninstall_DeleteData");
            Option.Visibility = Visibility.Visible;
            PrimaryButton.Content = Strings.Get("Uninstall_Uninstall");
        }
    }

    public SetupResult Result { get; private set; } = SetupResult.Cancelled;

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }

        SetBusy(true);
        try
        {
            if (_mode == SetupMode.Install)
            {
                await SetupActions.InstallAsync(_paths);
                Result = SetupResult.Installed;
                Close();
            }
            else
            {
                await SetupActions.UninstallAsync(_paths, Option.IsChecked == true);
                Result = SetupResult.Uninstalled;
                Body.Text = Strings.Get("Uninstall_Done");
                Option.Visibility = Visibility.Collapsed;
                PathCard.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = Strings.Get("Common_Close");
                CancelButton.Visibility = Visibility.Collapsed;
                _done = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ErrorLine.Text = Strings.Format("Install_Failed", LogSanitizer.Redact(ex.Message));
            ErrorLine.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Result = SetupResult.RunWithoutInstalling;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !busy;
        SecondaryButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
    }
}
