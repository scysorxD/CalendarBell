using System.Windows;
using System.Windows.Input;
using CalendarBell.App.ViewModels;

namespace CalendarBell.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(AboutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseCommand(object sender, ExecutedRoutedEventArgs e) => Close();
}
