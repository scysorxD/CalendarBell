using System.Windows;
using System.Windows.Input;
using CalendarBell.App.ViewModels;

namespace CalendarBell.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // Closing Settings only hides the window: the app keeps running in the notification area.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseCommand(object sender, ExecutedRoutedEventArgs e) => Close();
}
