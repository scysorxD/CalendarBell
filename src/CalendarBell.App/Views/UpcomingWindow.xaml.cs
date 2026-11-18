using System.Windows;
using System.Windows.Input;
using CalendarBell.App.ViewModels;

namespace CalendarBell.App.Views;

public partial class UpcomingWindow : Window
{
    public UpcomingWindow(UpcomingViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public UpcomingViewModel ViewModel { get; }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseCommand(object sender, ExecutedRoutedEventArgs e) => Close();
}
